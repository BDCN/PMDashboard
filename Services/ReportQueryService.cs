using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Builds and executes the Report Builder's two mode-specific query pipelines.
/// Kept separate from the ViewModel so filter composition is independently readable.
/// </summary>
public static class ReportQueryService
{
    public static List<Order> RunOrdersQuery(AppDbContext db, ReportFilterState f)
    {
        IQueryable<Order> q = db.Orders.AsNoTracking();

        switch (f.DateField)
        {
            case "ShipDate":
                if (f.DateFrom.HasValue) q = q.Where(o => o.ShipDate >= f.DateFrom);
                if (f.DateTo.HasValue)   q = q.Where(o => o.ShipDate <= f.DateTo);
                break;
            case "DeliveryDate":
                if (f.DateFrom.HasValue) q = q.Where(o => o.DeliveryDate >= f.DateFrom);
                if (f.DateTo.HasValue)   q = q.Where(o => o.DeliveryDate <= f.DateTo);
                break;
            case "PromiseDate":
                if (f.DateFrom.HasValue) q = q.Where(o => o.PromiseDate >= f.DateFrom);
                if (f.DateTo.HasValue)   q = q.Where(o => o.PromiseDate <= f.DateTo);
                break;
            case "RequestedCompletion":
                if (f.DateFrom.HasValue) q = q.Where(o => o.RequestedCompletion >= f.DateFrom);
                if (f.DateTo.HasValue)   q = q.Where(o => o.RequestedCompletion <= f.DateTo);
                break;
            default: // OrderDate
                if (f.DateFrom.HasValue) q = q.Where(o => o.OrderDate >= f.DateFrom);
                if (f.DateTo.HasValue)   q = q.Where(o => o.OrderDate <= f.DateTo);
                break;
        }

        if (f.OrderStatuses is { Count: > 0 })
            q = q.Where(o => o.OrderStatus != null && f.OrderStatuses.Contains(o.OrderStatus));

        if (f.PmIds is { Count: > 0 })
        {
            var dataNames = db.PMs.AsNoTracking()
                                  .Where(p => f.PmIds.Contains(p.Id))
                                  .Select(p => p.DataName)
                                  .ToList();
            q = q.Where(o => o.ProjectManagerDataName != null && dataNames.Contains(o.ProjectManagerDataName));
        }

        if (!string.IsNullOrWhiteSpace(f.TextSearch))
        {
            var lower = f.TextSearch.ToLower();
            q = q.Where(o =>
                (o.JobName != null && o.JobName.ToLower().Contains(lower)) ||
                (o.CustomerName != null && o.CustomerName.ToLower().Contains(lower)) ||
                (o.CustomerOrder != null && o.CustomerOrder.ToLower().Contains(lower)));
        }

        return q.OrderBy(o => o.JobName).ToList();
    }

    public static List<SiteRompFlatRow> RunSiteRompQuery(AppDbContext db, ReportFilterState f)
    {
        IQueryable<Romp> q = db.Romps.AsNoTracking()
            .Include(r => r.Site).ThenInclude(s => s.Location)
            .Include(r => r.Site).ThenInclude(s => s.AssignedPm)
            .Include(r => r.EquipmentTypes).ThenInclude(et => et.Category);

        if (f.LocationIds is { Count: > 0 })
            q = q.Where(r => f.LocationIds.Contains(r.Site.LocationId));

        if (f.SiteIds is { Count: > 0 })
            q = q.Where(r => f.SiteIds.Contains(r.SiteId));

        if (f.PmIds is { Count: > 0 })
            q = q.Where(r => r.Site.AssignedPmId != null && f.PmIds.Contains(r.Site.AssignedPmId.Value));

        if (f.RompStatuses is { Count: > 0 })
            q = q.Where(r => f.RompStatuses.Contains(r.Status));

        if (f.DateFrom.HasValue)
            q = q.Where(r => r.LastModified >= f.DateFrom);
        if (f.DateTo.HasValue)
            q = q.Where(r => r.LastModified <= f.DateTo);

        if (!string.IsNullOrWhiteSpace(f.TextSearch))
        {
            var lower = f.TextSearch.ToLower();
            q = q.Where(r => r.Name.ToLower().Contains(lower) || r.Site.Name.ToLower().Contains(lower));
        }

        var romps = q.ToList();

        // Site.SiteStatus depends on ALL of a site's ROMPs, not just the ones in this filtered
        // list — compute it from a separate site-rooted query rather than Include(Site.Romps)
        // on the Romp query above, which would form an EF Core include cycle (Romp → Site → Romps).
        var siteIds = romps.Select(r => r.SiteId).Distinct().ToList();
        var siteStatuses = db.Sites.AsNoTracking()
            .Include(s => s.Romps)
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionary(s => s.Id, s => s.SiteStatus);

        return romps.Select(r => new SiteRompFlatRow
        {
            RompId               = r.Id,
            LocationName         = r.Site?.Location?.Name ?? "",
            GeographicalLocation = r.Site?.Location?.GeographicalLocation ?? "",
            SiteName             = r.Site?.Name ?? "",
            SiteStatus           = siteStatuses.GetValueOrDefault(r.SiteId, ""),
            AssignedPmName       = r.Site?.AssignedPm?.FullName ?? "",
            RompName             = r.Name,
            RompStatus           = r.Status,
            EquipmentTypes       = r.EquipmentTypesText,
            LastModified         = r.LastModified,
        }).ToList();
    }
}
