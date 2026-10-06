namespace PMDashboard.Data.Models;

/// <summary>
/// One row per (Site, PM) or (Romp, PM) assignment period. Created when a campus or an
/// individual ROMP is assigned to a PM; closed out (UnassignedAtUtc set) when reassigned to
/// someone else or unassigned. AssignedPmId (on Site/Romp) only ever holds the CURRENT PM, so
/// this table is the only place the full history survives — including through a reassignment or
/// the campus/ROMP later being closed (closing never touches this table, only
/// Site.IsClosed/ROMP statuses).
///
/// RompId is null for a campus-level (Site) assignment, and set for an individual-ROMP
/// assignment — a ROMP always belongs to a Site, so SiteId is always populated either way (via
/// the ROMP's own Site when RompId is set), letting "history for this campus" and "history for
/// this ROMP" both query the same table.
/// </summary>
public class AssignmentHistory
{
    public int Id { get; set; }

    public int  SiteId { get; set; }
    public Site Site   { get; set; } = null!;

    public int?  RompId { get; set; }
    public Romp? Romp   { get; set; }

    public int PmId { get; set; }
    public PM  Pm   { get; set; } = null!;

    public DateTime  AssignedAtUtc   { get; set; }
    public DateTime? UnassignedAtUtc { get; set; }

    public string  AssignedBy   { get; set; } = "";
    public string? UnassignedBy { get; set; }

    public DateTime LastModified { get; set; }

    public bool IsActive => UnassignedAtUtc == null;
}
