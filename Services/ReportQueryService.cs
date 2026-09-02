using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Builds and executes the Report Builder's two mode-specific query pipelines.
/// Kept separate from the ViewModel so filter composition is independently readable.
/// </summary>
/// <summary>One page of results plus the total the filter matched.</summary>
public sealed record PagedResult<T>(List<T> Rows, int TotalCount, int PageIndex, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public int FirstRowNumber => TotalCount == 0 ? 0 : PageIndex * PageSize + 1;
    public int LastRowNumber  => Math.Min(TotalCount, (PageIndex + 1) * PageSize);
}

public static class ReportQueryService
{
    public static List<Order> RunOrdersQuery(AppDbContext db, ReportFilterState f) =>
        ComposeOrders(db, f).OrderBy(o => o.JobName).ToList();

    /// <summary>
    /// One page of orders, with the total the filter matched. Count and Skip/Take run in SQL,
    /// so a 131k-row table costs one page of objects rather than materialising the lot.
    /// </summary>
    public static PagedResult<Order> RunOrdersQuery(AppDbContext db, ReportFilterState f, int pageIndex, int pageSize)
    {
        var q     = ComposeOrders(db, f);
        int total = q.Count();
        // Skip/Take needs a deterministic order or rows can repeat or vanish between pages.
        var rows  = q.OrderBy(o => o.JobName).ThenBy(o => o.Id)
                     .Skip(pageIndex * pageSize).Take(pageSize).ToList();
        return new PagedResult<Order>(rows, total, pageIndex, pageSize);
    }

    private static IQueryable<Order> ComposeOrders(AppDbContext db, ReportFilterState f)
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
            case "UploadDate":
                if (f.DateFrom.HasValue) q = q.Where(o => o.UploadDate >= f.DateFrom);
                if (f.DateTo.HasValue)   q = q.Where(o => o.UploadDate <= f.DateTo);
                break;
            case "LastModified":
                if (f.DateFrom.HasValue) q = q.Where(o => o.LastModified >= f.DateFrom);
                if (f.DateTo.HasValue)   q = q.Where(o => o.LastModified <= f.DateTo);
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

        return q;
    }

    public static List<SiteRompFlatRow> RunSiteRompQuery(AppDbContext db, ReportFilterState f) =>
        Flatten(db, ComposeRomps(db, f).OrderBy(r => r.Site!.Name).ThenBy(r => r.Name).ToList());

    /// <summary>One page of flattened ROMP rows, with the total the filter matched.</summary>
    public static PagedResult<SiteRompFlatRow> RunSiteRompQuery(
        AppDbContext db, ReportFilterState f, int pageIndex, int pageSize)
    {
        var q     = ComposeRomps(db, f);
        int total = q.Count();
        var romps = q.OrderBy(r => r.Site!.Name).ThenBy(r => r.Name).ThenBy(r => r.Id)
                     .Skip(pageIndex * pageSize).Take(pageSize).ToList();
        return new PagedResult<SiteRompFlatRow>(Flatten(db, romps), total, pageIndex, pageSize);
    }

    private static IQueryable<Romp> ComposeRomps(AppDbContext db, ReportFilterState f)
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

        return q;
    }

    /// <summary>
    /// Flattens ROMPs into report rows. Only the rows handed in are flattened, so on a paged
    /// call the SiteStatus lookup covers just that page's sites.
    /// </summary>
    private static List<SiteRompFlatRow> Flatten(AppDbContext db, List<Romp> romps)
    {
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
            LastModifiedBy       = r.LastModifiedBy,
        }).ToList();
    }
}
