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
        using var db = new AppDbContext();
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

        using var db  = new AppDbContext();
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
        db.Orders.RemoveRange(db.Orders);
        db.SaveChanges();
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
        var locationCache = db.Locations.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var siteCache     = db.Sites.ToDictionary(s => s.Name,     StringComparer.OrdinalIgnoreCase);

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
                if (OrderHasChanged(found, row))
                {
                    ApplyOrderChanges(found, row);
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
        Load();
        return new ImportResult(added, updated, skipped, locationsCreated, sitesCreated, rompsCreated);
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

    private static bool OrderHasChanged(Order a, Order b) =>
        a.CustomerPO != b.CustomerPO || a.CustPOLine != b.CustPOLine ||
        a.ShipHold != b.ShipHold || a.FaceSite != b.FaceSite ||
        a.CustomerOrder != b.CustomerOrder || a.OrdLine != b.OrdLine ||
        a.AssignSite != b.AssignSite || a.AssignWhse != b.AssignWhse ||
        a.JobName != b.JobName || a.IPN != b.IPN ||
        a.Item != b.Item || a.ItemDescription != b.ItemDescription ||
        a.ItemDesignation != b.ItemDesignation ||
        a.QtyOrd != b.QtyOrd || a.QtyShip != b.QtyShip ||
        a.ShipDate != b.ShipDate || a.DeliveryDate != b.DeliveryDate ||
        a.WarrantyExpiration != b.WarrantyExpiration ||
        a.EstimatedDeliveryDate != b.EstimatedDeliveryDate ||
        a.EstimatedCompletionDate != b.EstimatedCompletionDate ||
        a.ActualCompletionDate != b.ActualCompletionDate ||
        a.RequestedCompletion != b.RequestedCompletion ||
        a.ProductionVariance != b.ProductionVariance ||
        a.RequestedOSD != b.RequestedOSD || a.DaysLate != b.DaysLate ||
        a.PreviousEstCompletionDate != b.PreviousEstCompletionDate ||
        a.EstCompletionDateChange != b.EstCompletionDateChange ||
        a.ScheduledStartDate != b.ScheduledStartDate ||
        a.ActualStartDate != b.ActualStartDate || a.InvoiceDate != b.InvoiceDate ||
        a.CustNum != b.CustNum || a.OrderShipTo != b.OrderShipTo ||
        a.LineItemShipTo != b.LineItemShipTo ||
        a.CreditHold != b.CreditHold || a.Slsman != b.Slsman ||
        a.OrderStatus != b.OrderStatus || a.ProductCode != b.ProductCode ||
        a.Price != b.Price || a.ExtPrice != b.ExtPrice ||
        a.SectionCount != b.SectionCount ||
        a.EstCompletionDateChangeReason != b.EstCompletionDateChangeReason ||
        a.OrderDate != b.OrderDate || a.OrderLineStatus != b.OrderLineStatus ||
        a.PromiseDate != b.PromiseDate || a.LastShipDate != b.LastShipDate ||
        a.QtyInvoiced != b.QtyInvoiced || a.SalesTax1 != b.SalesTax1 ||
        a.SalesTax2 != b.SalesTax2 || a.TotalCOPrice != b.TotalCOPrice ||
        a.ProjectManagerDataName != b.ProjectManagerDataName ||
        a.CustomerName != b.CustomerName;

    private static void ApplyOrderChanges(Order t, Order s)
    {
        t.CustomerPO = s.CustomerPO; t.CustPOLine = s.CustPOLine;
        t.ShipHold = s.ShipHold; t.FaceSite = s.FaceSite;
        t.CustomerOrder = s.CustomerOrder; t.OrdLine = s.OrdLine;
        t.AssignSite = s.AssignSite; t.AssignWhse = s.AssignWhse;
        t.JobName = s.JobName; t.IPN = s.IPN;
        t.Item = s.Item; t.ItemDescription = s.ItemDescription;
        t.ItemDesignation = s.ItemDesignation;
        t.QtyOrd = s.QtyOrd; t.QtyShip = s.QtyShip;
        t.ShipDate = s.ShipDate; t.DeliveryDate = s.DeliveryDate;
        t.WarrantyExpiration = s.WarrantyExpiration;
        t.EstimatedDeliveryDate = s.EstimatedDeliveryDate;
        t.EstimatedCompletionDate = s.EstimatedCompletionDate;
        t.ActualCompletionDate = s.ActualCompletionDate;
        t.RequestedCompletion = s.RequestedCompletion;
        t.ProductionVariance = s.ProductionVariance;
        t.RequestedOSD = s.RequestedOSD; t.DaysLate = s.DaysLate;
        t.PreviousEstCompletionDate = s.PreviousEstCompletionDate;
        t.EstCompletionDateChange = s.EstCompletionDateChange;
        t.ScheduledStartDate = s.ScheduledStartDate;
        t.ActualStartDate = s.ActualStartDate; t.InvoiceDate = s.InvoiceDate;
        t.CustNum = s.CustNum; t.OrderShipTo = s.OrderShipTo;
        t.LineItemShipTo = s.LineItemShipTo;
        t.CreditHold = s.CreditHold; t.Slsman = s.Slsman;
        t.OrderStatus = s.OrderStatus; t.ProductCode = s.ProductCode;
        t.Price = s.Price; t.ExtPrice = s.ExtPrice;
        t.SectionCount = s.SectionCount;
        t.EstCompletionDateChangeReason = s.EstCompletionDateChangeReason;
        t.OrderDate = s.OrderDate; t.OrderLineStatus = s.OrderLineStatus;
        t.PromiseDate = s.PromiseDate; t.LastShipDate = s.LastShipDate;
        t.QtyInvoiced = s.QtyInvoiced; t.SalesTax1 = s.SalesTax1;
        t.SalesTax2 = s.SalesTax2; t.TotalCOPrice = s.TotalCOPrice;
        t.ProjectManagerDataName = s.ProjectManagerDataName;
        t.CustomerName = s.CustomerName;
        t.UploadDate = s.UploadDate;
    }
}
