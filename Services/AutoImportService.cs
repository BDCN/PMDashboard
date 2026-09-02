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

    private void TryImport(string filePath)
    {
        // Mark seen immediately so a failed move doesn't cause an infinite retry loop
        _seen.Add(Path.GetFileName(filePath));

        try
        {
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

            MoveToProcessed(filePath, "imported");
            Notify($"Auto-imported \"{Path.GetFileName(filePath)}\": " +
                   $"{result.Added} added, {result.Updated} updated, {result.Skipped} unchanged, " +
                   (result.DuplicatesSkipped > 0 ? $"{result.DuplicatesSkipped} duplicates skipped, " : "") +
                   $"{result.LocationsCreated} locations, {result.SitesCreated} sites, {result.RompsCreated} ROMPs.");
        }
        catch (Exception ex)
        {
            MoveToProcessed(filePath, "error");
            Notify($"Error processing \"{Path.GetFileName(filePath)}\": {ex.Message}");
        }
    }

    private static ImportResult RunUpsert(List<Order> incoming)
    {
        var uploadTime = DateTime.Now;
        int added = 0, updated = 0, skipped = 0, locsCreated = 0, sitesCreated = 0, rompsCreated = 0;

        using var db = new AppDbContext();

        // Duplicate keys kept (last/newest wins) rather than throwing — these tables have
        // no unique constraint, so an existing duplicate would break every future import.
        var existingOrders = db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerOrder != null && o.OrdLine != null)
            .AsEnumerable()
            .ToDictionaryLastWins(o => (o.CustomerOrder!, o.OrdLine!), out int dbDuplicates);

        var locationCache = db.Locations.ToDictionaryLastWins(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var siteCache     = db.Sites.ToDictionaryLastWins(s => s.Name,     StringComparer.OrdinalIgnoreCase);

        // Keys inserted during this run, so a key repeated inside the file does not
        // insert a second copy of the same order.
        var batchOrderKeys = new HashSet<(string, string)>();
        int fileDuplicates = 0;

        var existingRompSet = db.Romps.AsNoTracking()
            .Select(r => new { SiteName = r.Site!.Name, RompName = r.Name })
            .AsEnumerable()
            .Select(r => $"{r.SiteName}|{r.RompName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchRompSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in incoming)
        {
            row.UploadDate = uploadTime;

            var orderKey = row.CustomerOrder != null && row.OrdLine != null
                ? ((string, string)?)(row.CustomerOrder, row.OrdLine)
                : null;

            if (orderKey is { } key && existingOrders.TryGetValue(key, out var found))
            {
                // Compare field by field, the same way the manual import does.
                //
                // This previously marked every existing row Modified whenever the upload time
                // differed, without copying a single value across. Two bugs in one: changed
                // data was never actually applied, and SaveChanges restamped LastModified on
                // every poll - which is what the report's "Last Changed" column reads, so every
                // row looked freshly changed after any automatic import.
                if (OrderMerge.HasChanged(found, row))
                {
                    OrderMerge.ApplyChanges(found, row);
                    db.Orders.Update(found);   // untracked (AsNoTracking), must mark explicitly
                    updated++;
                }
                else skipped++;
            }
            else if (orderKey is { } dupKey && batchOrderKeys.Contains(dupKey))
            {
                // Same key twice in one file — skip the repeat instead of inserting a duplicate.
                fileDuplicates++;
            }
            else
            {
                db.Orders.Add(row);
                if (orderKey is { } newKey) batchOrderKeys.Add(newKey);
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
        return new ImportResult(added, updated, skipped, locsCreated, sitesCreated, rompsCreated,
                                dbDuplicates + fileDuplicates);
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
