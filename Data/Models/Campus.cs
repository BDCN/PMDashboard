namespace PMDashboard.Data.Models;

/// <summary>
/// The level between a Cluster (Location) and its Sites: Cluster → Campus → Site → ROMP.
/// E.g. cluster ATL → campus ATL089 → sites ATL085, ATL087, ATL089 → ROMP ATL085ROMP06.
///
/// The forecast sheets name a site's campus in their "az" column (Type O "az", Type L's column
/// SAT calls "Cluster" — see ForecastField.IsCampusField); CampusArrangementService uses that to
/// place sites. Campus names are kept exactly as the forecast writes them ("IAD12" and "IAD012"
/// are different campuses, by decision). A site the forecast says nothing about gets a campus of
/// its own name.
///
/// PM assignment: a campus has a PM (AssignedPmId), and assigning one writes that PM onto every
/// site in the campus that isn't a site-level override (Site.PmOverride). Site.AssignedPmId stays
/// the effective, materialised PM per site, so Workload/PM Detail/Coverage Planner/reports keep
/// reading the site's PM exactly as before — see CampusService.
/// </summary>
public class Campus
{
    public int    Id         { get; set; }
    public string Name       { get; set; } = "";

    public int      LocationId { get; set; }
    public Location Location   { get; set; } = null!;

    public int? AssignedPmId { get; set; }
    public PM?  AssignedPm   { get; set; }

    /// <summary>Team sub-region for this campus when it differs from its cluster's (a cluster split
    /// across sub-regions/regions). Null = same as the cluster (Location.SubRegionId). See TeamRules.</summary>
    public int?       SubRegionId { get; set; }
    public SubRegion? SubRegion   { get; set; }

    public DateTime LastModified { get; set; }

    /// <summary>Email of whoever last wrote this row; stamped by AppDbContext.SaveChanges.</summary>
    public string LastModifiedBy { get; set; } = "";

    public ICollection<Site> Sites { get; set; } = new List<Site>();

    // ── Display helpers (EF-ignored; need Sites, and each Site's Romps, eager-loaded) ──
    public int SiteCount => Sites.Count;
    public int RompCount => Sites.Sum(s => s.Romps.Count);
    public int OverrideCount => Sites.Count(s => s.PmOverride);

    /// <summary>Active if any site is Active, Complete when every site is Complete, else Ordered —
    /// the same rule a site applies to its ROMPs, one level up.</summary>
    public string CampusStatus =>
        Sites.Count == 0 ? "Ordered"
        : Sites.Any(s => s.SiteStatus == "Active") ? "Active"
        : Sites.All(s => s.SiteStatus == "Complete") ? "Complete"
        : "Ordered";

    /// <summary>"Jane Smith", "Jane Smith (+2 sites with their own PM)", or "" when unassigned.</summary>
    /// <summary>"Central 1" when the campus has its own sub-region, "East 1 (cluster)" when it
    /// follows its cluster's, else "". Needs SubRegion and Location.SubRegion eager-loaded.</summary>
    public string SubRegionDisplay =>
        SubRegion?.Name ?? (Location?.SubRegion?.Name is { } s ? $"{s} (cluster)" : "");

    public string PmDisplay =>
        (AssignedPm?.FullName ?? "") + (OverrideCount > 0
            ? $"{(AssignedPm == null ? "" : " ")}(+{OverrideCount} site{(OverrideCount == 1 ? "" : "s")} with own PM)"
            : "");
}
