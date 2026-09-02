using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

public partial class OrdersViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<Order> _orders = new();
    [ObservableProperty] private int    _totalCount;
    [ObservableProperty] private string _searchText     = "";
    [ObservableProperty] private string _lastUploadDate = "No imports yet";

    public void Load()
    {
        using var db = AppDbContext.ForRead();
        var list = db.Orders.AsNoTracking().OrderBy(o => o.JobName).ToList();
        Orders     = new ObservableCollection<Order>(list);
        TotalCount = list.Count;

        var latest = db.Orders.Max(o => (DateTime?)o.UploadDate);
        LastUploadDate = latest.HasValue
            ? latest.Value.ToString("MMM d, yyyy  h:mm tt")
            : "No imports yet";
    }

    public void Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { Load(); return; }

        using var db  = AppDbContext.ForRead();
        var lower     = text.ToLower();
        var list      = db.Orders.AsNoTracking()
            .Where(o => (o.JobName      != null && o.JobName.ToLower().Contains(lower))
                     || (o.CustomerPO   != null && o.CustomerPO.ToLower().Contains(lower))
                     || (o.CustomerOrder != null && o.CustomerOrder.ToLower().Contains(lower))
                     || (o.Item         != null && o.Item.ToLower().Contains(lower))
                     || (o.CustNum      != null && o.CustNum.ToLower().Contains(lower)))
            .OrderBy(o => o.JobName)
            .ToList();
        Orders     = new ObservableCollection<Order>(list);
        TotalCount = list.Count;
    }

    public void ClearAll()
    {
        using var db = new AppDbContext();
        int removed = db.Orders.Count();
        db.Orders.RemoveRange(db.Orders);
        db.SaveChanges();
        AuditService.Log("Delete", "Order", details: $"Cleared all orders ({removed:N0} row(s))");
        Load();
    }

    // Upserts orders by (CustomerOrder, OrdLine) key, auto-creates Locations/Sites/ROMPs from JobName.
    public ImportResult UpsertOrders(IEnumerable<Order> incoming)
    {
        var uploadTime = DateTime.Now;
        int added = 0, updated = 0, skipped = 0, locationsCreated = 0, sitesCreated = 0, rompsCreated = 0;

        using var db = new AppDbContext();

        // Existing orders — AsNoTracking, filtered in SQL before materialising.
        // Duplicate keys are tolerated (last/newest wins) rather than throwing: none of
        // these tables has a unique constraint, so a database that already contains a
        // duplicate would otherwise fail every future import.
        var existingOrders = db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerOrder != null && o.OrdLine != null)
            .AsEnumerable()
            .ToDictionaryLastWins(o => (o.CustomerOrder!, o.OrdLine!), out int dbDuplicates);

        // Location / Site caches — tracked so EF can wire up FK relationships for new children.
        var locationCache = db.Locations.ToDictionaryLastWins(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var siteCache     = db.Sites.ToDictionaryLastWins(s => s.Name,     StringComparer.OrdinalIgnoreCase);

        // Orders added during this run, so a key repeated inside the imported file
        // overwrites the pending row instead of inserting a second copy of it.
        var batchOrders = new Dictionary<(string, string), Order>();
        int fileDuplicates = 0;

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
            var orderKey = row.CustomerOrder != null && row.OrdLine != null
                ? ((string, string)?)(row.CustomerOrder, row.OrdLine)
                : null;

            if (orderKey is { } key && existingOrders.TryGetValue(key, out var found))
            {
                if (OrderMerge.HasChanged(found, row))
                {
                    OrderMerge.ApplyChanges(found, row);
                    db.Orders.Update(found); // untracked (AsNoTracking), must mark explicitly
                    updated++;
                }
                else skipped++;
            }
            else if (orderKey is { } dupKey && batchOrders.TryGetValue(dupKey, out var pending))
            {
                // Same key twice in one file — overwrite the row already queued for insert
                // so the last occurrence wins, rather than inserting a duplicate.
                OrderMerge.ApplyChanges(pending, row);
                fileDuplicates++;
            }
            else
            {
                db.Orders.Add(row);
                if (orderKey is { } newKey) batchOrders[newKey] = row;
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
        Load();

        AuditService.Log("Import", "Order", details:
            $"{added} added, {updated} updated, {skipped} unchanged, "
          + $"{dbDuplicates + fileDuplicates} duplicates; created {locationsCreated} location(s), "
          + $"{sitesCreated} site(s), {rompsCreated} ROMP(s)");

        return new ImportResult(added, updated, skipped, locationsCreated, sitesCreated, rompsCreated,
                                dbDuplicates + fileDuplicates);
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
