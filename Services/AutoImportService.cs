using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Background service that polls an import folder for new Excel files and auto-imports them.
/// Runs on a thread-pool thread; UI notifications are dispatched back to the WPF dispatcher.
/// </summary>
public sealed class AutoImportService : IDisposable
{
    private System.Threading.Timer? _timer;
    private string  _folder      = "";
    private int     _pollSeconds = 60;

    // Tracks filenames that have already been processed in this session.
    // Prevents re-importing if MoveToProcessed fails (e.g. file locked).
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    // Fires on the WPF dispatcher thread when an import completes or fails.
    public event Action<string>? StatusChanged;

    // Fires on the WPF dispatcher thread after a successful auto-import, so any open page
    // (e.g. Orders) can refresh a "last import" summary without polling.
    public event Action<ImportResult>? ImportCompleted;

    public void Configure(string folder, int pollSeconds)
    {
        _folder      = folder;
        _pollSeconds = Math.Max(60, pollSeconds);
    }

    public void Start()
    {
        _timer?.Dispose();
        var interval = TimeSpan.FromSeconds(_pollSeconds);
        _timer = new System.Threading.Timer(_ => Poll(), null, interval, interval);
        Notify($"Auto-import active — polling \"{_folder}\" every {_pollSeconds}s");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        Notify("Auto-import stopped.");
    }

    private void Poll()
    {
        // This runs on a raw Timer callback (a thread-pool thread), not a Task — an exception
        // escaping here isn't just a failed import, it's an unhandled exception on a thread-pool
        // thread, which crashes the whole process by default in .NET. Directory.GetFiles (and
        // File.GetLastWriteTime) throw for things well outside this service's control — a
        // network share blipping, permissions changing, a drive letter disappearing — so the
        // whole poll pass needs its own safety net, not just the per-file TryImport below.
        try
        {
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder)) return;

            var files = Directory.GetFiles(_folder, "*.xlsx")
                         .Concat(Directory.GetFiles(_folder, "*.xls"))
                         .OrderBy(File.GetLastWriteTime)
                         .ToArray();

            foreach (var file in files)
            {
                if (_seen.Contains(Path.GetFileName(file))) continue; // already processed this session
                TryImport(file);
            }
        }
        catch (Exception ex)
        {
            Notify($"Auto-import poll failed — will retry next cycle: {ex.Message}");
        }
    }

    private void TryImport(string filePath)
    {
        // Mark seen immediately so a failed move doesn't cause an infinite retry loop
        _seen.Add(Path.GetFileName(filePath));

        // Network/Cloud mode: every computer running SAT may be watching the same folder. Only
        // one imports at a time (same lock as a manual import); the others try again next poll,
        // by which time the file has normally been moved to Processed by whoever imported it.
        var (acquired, _) = ImportLockService.TryAcquire(CurrentUser.Email);
        if (!acquired)
        {
            _seen.Remove(Path.GetFileName(filePath));
            return;
        }

        try
        {
            if (!File.Exists(filePath)) return; // another computer imported it meanwhile

            // 1. Parse — route to the configured source type
            var parser     = new ExcelImportService();
            var sourceType = new ConfigService().Get(ConfigKeys.SourceDataType, "VADATA");
            var orders = sourceType switch
            {
                "Tableau"   => parser.ImportTableauOrders(filePath),
                "OpenLines" => parser.ImportOpenLinesOrders(filePath),
                _           => parser.ImportOrders(filePath),
            };

            if (orders.Count == 0)
            {
                MoveToProcessed(filePath, "empty");
                Notify($"Skipped \"{Path.GetFileName(filePath)}\" — no data rows found.");
                return;
            }

            // 2. Basic format check: first order must have at least a non-null JobName
            if (orders.All(o => string.IsNullOrWhiteSpace(o.JobName)))
            {
                MoveToProcessed(filePath, "invalid");
                Notify($"Skipped \"{Path.GetFileName(filePath)}\" — file format does not match.");
                return;
            }

            // 3. Import — replicate the UpsertOrders logic without going through the ViewModel
            var result = RunUpsert(orders);
            if (result.SitesCreated > 0)
                try { CampusArrangementService.Arrange(); } catch { /* best-effort; runs again at next startup/sync */ }

            MoveToProcessed(filePath, "imported");
            Notify($"Auto-imported \"{Path.GetFileName(filePath)}\": " +
                   $"{result.Added} added, {result.Updated} updated, {result.Skipped} unchanged, " +
                   $"{result.LocationsCreated} clusters, {result.SitesCreated} sites, {result.RompsCreated} ROMPs.");
            Application.Current?.Dispatcher.InvokeAsync(() => ImportCompleted?.Invoke(result));
        }
        catch (Exception ex)
        {
            MoveToProcessed(filePath, "error");
            Notify($"Error processing \"{Path.GetFileName(filePath)}\": {ex.Message}");
        }
        finally
        {
            ImportLockService.Release(CurrentUser.Email);
        }
    }

    private static ImportResult RunUpsert(List<Order> incoming)
    {
        var uploadTime = DateTime.Now;
        int added = 0, updated = 0, skipped = 0, locsCreated = 0, sitesCreated = 0, rompsCreated = 0;

        using var db = new AppDbContext();

        var existingOrders = db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerOrder != null && o.OrdLine != null)
            .AsEnumerable()
            .ToDictionary(o => (o.CustomerOrder!, o.OrdLine!));

        // Safe against duplicate names (no DB-level uniqueness on Location/Site.Name) — see ToSafeNameDictionary.
        var locationCache = db.Locations.AsEnumerable().ToSafeNameDictionary(l => l.Name, l => l.LastModified);
        var siteCache     = db.Sites.AsEnumerable().ToSafeNameDictionary(s => s.Name,     s => s.LastModified);

        var existingRompSet = db.Romps.AsNoTracking()
            .Select(r => new { SiteName = r.Site!.Name, RompName = r.Name })
            .AsEnumerable()
            .Select(r => $"{r.SiteName}|{r.RompName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchRompSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in incoming)
        {
            row.UploadDate = uploadTime;

            if (row.CustomerOrder != null && row.OrdLine != null
                && existingOrders.TryGetValue((row.CustomerOrder, row.OrdLine), out var found))
            {
                if (OrderMerge.HasChanged(found, row))
                {
                    OrderMerge.ApplyChanges(found, row);
                    db.Orders.Update(found); // untracked (AsNoTracking), must mark explicitly
                    updated++;
                }
                else skipped++;
            }
            else
            {
                db.Orders.Add(row);
                added++;
            }

            // Location / Site / ROMP auto-creation
            var (locName, siteName, rompName) = ParseJobName(row.JobName);
            if (locName == null || siteName == null) continue;

            if (!locationCache.TryGetValue(locName, out var loc))
            {
                var geoRegion = AirportLookup.Lookup(locName)?.GeoRegion ?? "";
                loc = new Location { Name = locName, GeographicalLocation = geoRegion };
                db.Locations.Add(loc);
                locationCache[locName] = loc;
                locsCreated++;
            }

            if (!siteCache.TryGetValue(siteName, out var site))
            {
                site = new Site { Name = siteName, Location = loc };
                db.Sites.Add(site);
                siteCache[siteName] = site;
                sitesCreated++;
            }

            if (rompName != null)
            {
                var rompKey = $"{siteName}|{rompName}";
                if (!existingRompSet.Contains(rompKey) && !batchRompSet.Contains(rompKey))
                {
                    db.Romps.Add(new Romp { Name = rompName, Site = site });
                    batchRompSet.Add(rompKey);
                    rompsCreated++;
                }
            }
        }

        db.SaveChanges();
        return new ImportResult(added, updated, skipped, locsCreated, sitesCreated, rompsCreated);
    }

    // Same parsing as OrdersViewModel.ParseJobName
    private static readonly System.Text.RegularExpressions.Regex _jobWithRomp =
        new(@"^([A-Za-z]{2,6}\d+)(ROMP\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _siteOnly =
        new(@"^([A-Za-z]{2,6}\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (string? loc, string? site, string? romp) ParseJobName(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName)) return (null, null, null);
        var s = jobName.Trim();
        var h = s.IndexOf('-');
        if (h >= 0) s = s[..h];
        var m = _jobWithRomp.Match(s);
        if (m.Success)
        {
            var site = m.Groups[1].Value.ToUpper();
            var romp = m.Groups[2].Value.ToUpper();
            return (site.Length >= 3 ? site[..3] : site, site, romp);
        }
        var sm = _siteOnly.Match(s);
        if (sm.Success)
        {
            var site = sm.Groups[1].Value.ToUpper();
            return (site.Length >= 3 ? site[..3] : site, site, null);
        }
        return (null, null, null);
    }

    private static void MoveToProcessed(string filePath, string tag)
    {
        try
        {
            var dir         = Path.GetDirectoryName(filePath)!;
            var processed   = Path.Combine(dir, "processed");
            Directory.CreateDirectory(processed);
            var newName     = $"{Path.GetFileNameWithoutExtension(filePath)}_{DateTime.Now:yyyyMMdd_HHmmss}_{tag}{Path.GetExtension(filePath)}";
            File.Move(filePath, Path.Combine(processed, newName), overwrite: true);
        }
        catch { /* ignore move errors */ }
    }

    private void Notify(string message)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => StatusChanged?.Invoke(message));
    }

    public void Dispose() => Stop();
}
