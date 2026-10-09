using PMDashboard.Services;

namespace PMDashboard.Data.Models;

public class Site
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;

    /// <summary>The campus this site belongs to (Cluster → Campus → Site → ROMP). Null only
    /// until CampusArrangementService places it — every site gets one.</summary>
    public int?    CampusId { get; set; }
    public Campus? Campus   { get; set; }

    /// <summary>True once a user has moved this site to a campus by hand. Forecast uploads then
    /// never move it again — a manual re-association always wins over the forecast's az column.</summary>
    public bool CampusManual { get; set; }

    /// <summary>The site's current, effective PM. Normally the campus's PM (written down from
    /// Campus.AssignedPmId); see <see cref="PmOverride"/> for exceptions.</summary>
    public int?     AssignedPmId { get; set; }
    public PM?      AssignedPm   { get; set; }

    /// <summary>True when this site deliberately has a different PM from its campus — assigning
    /// the campus then leaves it alone. Maintained by AssignmentHistoryService.RecordChange.</summary>
    public bool PmOverride { get; set; }

    /// <summary>Team sub-region for this site when it differs from its campus's/cluster's (a campus
    /// split across sub-regions or regions). Null = same as its campus. See TeamRules.</summary>
    public int?       SubRegionId { get; set; }
    public SubRegion? SubRegion   { get; set; }

    /// <summary>Display only (EF-ignored): the sub-region that governs this site, "(split)" when
    /// it's the site's own. Filled by the Sites page.</summary>
    public string TeamAreaLabel { get; set; } = "";

    /// <summary>Assigned PM's name, marked when it's a site-level override (EF-ignored).</summary>
    public string PmDisplay => AssignedPm == null ? "" : AssignedPm.FullName + (PmOverride ? "  (own PM)" : "");
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

    /// <summary>
    /// Admin-chosen stand-in status for a zero-ROMP campus, set via the same "Set All ROMPs to
    /// Status" picker used when ROMPs exist (AddEditSiteDialog/AddEditLocationDialog) — with no
    /// ROMP to actually carry a status, the campus acts as its own virtual ROMP instead of the
    /// picker silently doing nothing. Consulted only in the zero-ROMP branch of SiteStatus/
    /// StatusColor, same rule as IsClosed: once a real ROMP exists, its actual status takes over
    /// and this goes dormant (not cleared — it's ready again if the campus goes back to zero ROMPs).
    /// </summary>
    public string? StatusWhenEmpty { get; set; }

    // Derived from child ROMP statuses (and StatusWhenEmpty/IsClosed for the zero-ROMP case) —
    // not stored, ignored by EF Core in OnModelCreating. Uses StatusConfig so the Active/Complete
    // determination is driven by the configured statuses.
    public string SiteStatus
    {
        get
        {
            if (!Romps.Any())
            {
                var standInStatus = EffectiveEmptyStatus;
                if (standInStatus != null)
                {
                    if (StatusConfig.Active.Contains(standInStatus))   return "Active";
                    if (StatusConfig.Complete.Contains(standInStatus)) return "Complete";
                }
                return "Ordered";
            }
            if (Romps.Any(r => StatusConfig.Active.Contains(r.Status))) return "Active";
            if (Romps.All(r => StatusConfig.Complete.Contains(r.Status))) return "Complete";
            return "Ordered";
        }
    }

    /// <summary>
    /// Indicator-dot color for the Campuses list. Unlike SiteStatus's 3-way Active/Complete/
    /// Ordered bucket, this reflects the color of whichever individual status is furthest along
    /// among this site's ROMPs — "furthest along" meaning highest position in the admin-ordered
    /// Status list (Configuration page), not a hardcoded notion of progress. A campus with ROMPs
    /// in both Construction and CX shows CX's color, since CX sorts later. Zero-ROMP case defers
    /// to EffectiveEmptyStatus (StatusWhenEmpty, then the IsClosed fallback).
    /// </summary>
    public string StatusColor => Romps.Any()
        ? StatusConfig.ColorFor(Romps.Select(r => r.Status).Distinct()
              .OrderByDescending(s => StatusConfig.All.ToList().IndexOf(s)).FirstOrDefault())
        : StatusConfig.ColorFor(EffectiveEmptyStatus);

    /// <summary>StatusWhenEmpty if an admin picked one, else the same IsClosed-based guess
    /// SiteStatus/StatusColor used before StatusWhenEmpty existed (Complete when closed, else
    /// whatever sorts first — normally "Ordered").</summary>
    private string? EffectiveEmptyStatus =>
        StatusWhenEmpty ?? (IsClosed ? StatusConfig.Complete.FirstOrDefault() : StatusConfig.All.FirstOrDefault());
}
