using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

public partial class OrdersViewModel : ObservableObject
{
    [ObservableProperty] private int    _totalCount;
    [ObservableProperty] private string _lastUploadDate = "No imports yet";

    // ── Last import results — persistent card, replaces the old one-shot MessageBox ──
    [ObservableProperty] private bool   _hasLastImport;
    [ObservableProperty] private string _lastImportHeadline = "";
    [ObservableProperty] private string _lastImportDetails  = "";
    [ObservableProperty] private string _lastImportWhen     = "";
    [ObservableProperty] private string _lastImportBy       = "";

    // ── Import-in-progress state — disables the button and shows the lock/push status ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotImporting))]
    private bool _isImporting;
    [ObservableProperty] private string _importingStatus = "";

    public bool IsNotImporting => !IsImporting;

    public void Load()
    {
        using var db = new AppDbContext();
        TotalCount = db.Orders.Count();

        var latest = db.Orders.Max(o => (DateTime?)o.UploadDate);
        LastUploadDate = latest.HasValue
            ? latest.Value.ToString("MMM d, yyyy  h:mm tt")
            : "No imports yet";

        // Semi-automatic imports can land while this page isn't the active one — subscribing
        // here (idempotent, same -=/+= pattern as ConfigViewModel) keeps the card current
        // whenever the user does navigate back, without a separate always-running listener.
        App.AutoImport.ImportCompleted -= OnAutoImportCompleted;
        App.AutoImport.ImportCompleted += OnAutoImportCompleted;
    }

    private void OnAutoImportCompleted(ImportResult result)
    {
        SetLastImportResult(result, "Automatic import");
        Load();
    }

    private void SetLastImportResult(ImportResult result, string by)
    {
        HasLastImport      = true;
        LastImportHeadline = $"{result.Added:N0} added  ·  {result.Updated:N0} updated  ·  {result.Skipped:N0} unchanged";

        var created = new List<string>();
        if (result.LocationsCreated > 0) created.Add($"{result.LocationsCreated:N0} cluster(s)");
        if (result.SitesCreated     > 0) created.Add($"{result.SitesCreated:N0} site(s)");
        if (result.RompsCreated     > 0) created.Add($"{result.RompsCreated:N0} ROMP(s)");
        LastImportDetails = created.Count > 0
            ? $"Created: {string.Join(", ", created)}"
            : "No new clusters, sites, or ROMPs created.";

        LastImportWhen = DateTime.Now.ToString("MMM d, yyyy  h:mm tt");
        LastImportBy   = by;
    }

    public void ClearAll()
    {
        using var db = new AppDbContext();
        db.Orders.RemoveRange(db.Orders);
        db.SaveChanges();
        AuditService.Log("Delete", "Orders", details: "Cleared all imported order records");
        Load();
    }

    // Upserts orders by (CustomerOrder, OrdLine) key, auto-creates Locations/Sites/ROMPs from JobName.
    public ImportResult UpsertOrders(IEnumerable<Order> incoming)
    {
        var uploadTime = DateTime.Now;
        int added = 0, updated = 0, skipped = 0, locationsCreated = 0, sitesCreated = 0, rompsCreated = 0;

        using var db = new AppDbContext();

        // Existing orders — AsNoTracking, filtered in SQL before materialising.
        var existingOrders = db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerOrder != null && o.OrdLine != null)
            .AsEnumerable()
            .ToDictionary(o => (o.CustomerOrder!, o.OrdLine!));

        // Location / Site caches — tracked so EF can wire up FK relationships for new children.
        // Safe against duplicate names (no DB-level uniqueness on Location/Site.Name) — see
        // ToSafeNameDictionary.
        var locationCache = db.Locations.AsEnumerable().ToSafeNameDictionary(l => l.Name, l => l.LastModified);
        var siteCache     = db.Sites.AsEnumerable().ToSafeNameDictionary(s => s.Name,     s => s.LastModified);

        // Existing ROMP dedup — projection only, no entity tracking needed.
        var existingRompSet = db.Romps
            .AsNoTracking()
            .Select(r => new { SiteName = r.Site!.Name, RompName = r.Name })
            .AsEnumerable()
            .Select(r => $"{r.SiteName}|{r.RompName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var batchRompSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in incoming)
        {
            row.UploadDate = uploadTime;

            // --- Order upsert ---
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

            // --- Location / Site / ROMP auto-creation ---
            var (locationName, siteName, rompName) = ParseJobName(row.JobName);
            if (locationName == null || siteName == null) continue;

            if (!locationCache.TryGetValue(locationName, out var location))
            {
                var geoRegion = PMDashboard.Services.AirportLookup.Lookup(locationName)?.GeoRegion ?? "";
                location = new Location { Name = locationName, GeographicalLocation = geoRegion };
                db.Locations.Add(location);
                locationCache[locationName] = location;
                locationsCreated++;
            }

            if (!siteCache.TryGetValue(siteName, out var site))
            {
                site = new Site { Name = siteName, Location = location };
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

        var result = new ImportResult(added, updated, skipped, locationsCreated, sitesCreated, rompsCreated);
        SetLastImportResult(result, CurrentUser.DisplayName);
        AuditService.Log("Import", "Orders", details:
            $"Imported orders: {added} added, {updated} updated, {skipped} unchanged, " +
            $"{locationsCreated} cluster(s), {sitesCreated} site(s), {rompsCreated} ROMP(s) created");
        Load();
        // New sites go into campuses — the forecast campus if known, else one of their own name.
        if (sitesCreated > 0) try { CampusArrangementService.Arrange(); } catch { /* best-effort; runs again at next startup/sync */ }
        return result;
    }

    /// <summary>
    /// Full import flow: acquire the cross-user lock (Cloud mode only — a no-op elsewhere),
    /// import to local SQLite (fast, unchanged), push the result to the cloud and wait for it,
    /// then release the lock. This keeps the local-first speed of a normal import while still
    /// stopping two people from importing over each other in Cloud mode.
    /// </summary>
    public async Task<(bool Success, string? Message)> ImportWithLockAsync(List<Order> incoming)
    {
        IsImporting     = true;
        ImportingStatus = "Checking import lock…";
        try
        {
            var (acquired, heldByMessage) = ImportLockService.TryAcquire(CurrentUser.Email);
            if (!acquired) return (false, heldByMessage);

            try
            {
                ImportingStatus = NetworkDb.IsShared ? "Importing to the network database…" : "Importing locally…";
                UpsertOrders(incoming);

                if (CloudSyncService.IsAvailable)
                {
                    ImportingStatus = "Uploading to the cloud…";
                    await App.CloudSync.PushNowAndWaitAsync();
                }
                return (true, null);
            }
            finally
            {
                ImportLockService.Release(CurrentUser.Email);
            }
        }
        finally
        {
            IsImporting     = false;
            ImportingStatus = "";
        }
    }

    // ── Helpers ──

    private static readonly Regex JobNameWithRomp =
        new(@"^([A-Za-z]{2,6}\d+)(ROMP\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiteOnly =
        new(@"^([A-Za-z]{2,6}\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (string? locationName, string? siteName, string? rompName) ParseJobName(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName)) return (null, null, null);

        var s      = jobName.Trim();
        var hyphen = s.IndexOf('-');
        if (hyphen >= 0) s = s[..hyphen]; // strip suffix: "IAD115ROMP01-LV-4A" → "IAD115ROMP01"

        var m = JobNameWithRomp.Match(s);
        if (m.Success)
        {
            var site = m.Groups[1].Value.ToUpper();
            var romp = m.Groups[2].Value.ToUpper();
            return (site.Length >= 3 ? site[..3] : site, site, romp);
        }

        var sm = SiteOnly.Match(s);
        if (sm.Success)
        {
            var site = sm.Groups[1].Value.ToUpper();
            return (site.Length >= 3 ? site[..3] : site, site, null);
        }

        return (null, null, null);
    }

}
