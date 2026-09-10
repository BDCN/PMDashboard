using PMDashboard.Services;

namespace PMDashboard.Data.Models;

public class Site
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;

    public int?     AssignedPmId { get; set; }
    public PM?      AssignedPm   { get; set; }
    public DateTime LastModified { get; set; }

    /// <summary>Email of whoever last wrote this row; stamped by AppDbContext.SaveChanges.</summary>
    public string LastModifiedBy { get; set; } = "";

    public ICollection<Romp> Romps { get; set; } = new List<Romp>();

    /// <summary>
    /// Explicit "close this site" flag, set by AddEditSiteDialog's "Close All ROMPs & This Site"
    /// checkbox (and cascaded from AddEditLocationDialog's Location-level equivalent). Exists
    /// because SiteStatus below is otherwise entirely ROMP-derived, and a site with zero ROMPs
    /// can never reach "Complete" through ROMP data alone — there's nothing to mark complete.
    /// Deliberately only consulted in the zero-ROMP branch of SiteStatus: once a site has real
    /// ROMPs, their actual statuses govern SiteStatus so a later active ROMP (e.g. from a fresh
    /// Orders import) surfaces the site again instead of staying silently hidden.
    /// </summary>
    public bool IsClosed { get; set; }

    // Derived from child ROMP statuses (and IsClosed for the zero-ROMP case) — not stored,
    // ignored by EF Core in OnModelCreating. Uses StatusConfig so the Active/Complete
    // determination is driven by the configured statuses.
    public string SiteStatus
    {
        get
        {
            if (!Romps.Any()) return IsClosed ? "Complete" : "Ordered";
            if (Romps.Any(r => StatusConfig.Active.Contains(r.Status))) return "Active";
            if (Romps.All(r => StatusConfig.Complete.Contains(r.Status))) return "Complete";
            return "Ordered";
        }
    }
}
