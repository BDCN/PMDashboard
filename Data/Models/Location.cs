namespace PMDashboard.Data.Models;

public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = "";               // e.g. "IAD", "PDX"
    public string GeographicalLocation { get; set; } = ""; // East, West

    /// <summary>The sub-region this cluster belongs to (e.g. IAD → East 1). Only PMs on that
    /// sub-region's team can be assigned here; unmapped clusters allow any PM in the same region.</summary>
    public int?       SubRegionId { get; set; }
    public SubRegion? SubRegion   { get; set; }

    public DateTime LastModified { get; set; }
    public ICollection<Site> Sites { get; set; } = new List<Site>();
    public ICollection<Campus> Campuses { get; set; } = new List<Campus>();

    /// <summary>
    /// Direct "owner" PM for this cluster itself — distinct from the per-Site assignments
    /// cascaded by SitesViewModel.AssignPmToLocation. Exists for a cluster with no campuses (or
    /// no campuses yet): there's nothing else to carry a PM assignment, so the cluster record
    /// holds its own. Assigned from the Assignment page's "Assign PM to Cluster" action, which
    /// only shows when the selected cluster has zero campuses — a cluster that does have
    /// campuses should be assigned PM-by-campus instead, not here.
    /// </summary>
    public int? AssignedPmId { get; set; }
    public PM?  AssignedPm   { get; set; }

    // Derived from child Site statuses — not stored, ignored by EF Core in OnModelCreating.
    // Relies on Sites (and each Site's Romps) being eager-loaded by the caller; defaults to
    // false when they aren't, which is the safe "don't hide it" outcome.
    public bool IsFullyClosed => Sites.Any() && Sites.All(s => s.SiteStatus == "Complete");

    // Sites page cluster-list "Assigned PM" column (EF-ignored). The direct cluster PM wins
    // (zero-campus clusters, where it's the only place an assignment can live); otherwise the
    // campuses' PM(s). Needs AssignedPm and Sites.AssignedPm eager-loaded — blank if not.
    public string AssignedPmDisplay
    {
        get
        {
            if (AssignedPm != null) return AssignedPm.FullName;
            var names = Sites.Where(s => s.AssignedPm != null)
                             .Select(s => s.AssignedPm!.FullName)
                             .Distinct()
                             .ToList();
            return names.Count switch
            {
                0 => "",
                1 => names[0],
                _ => $"{names.Count} PMs",
            };
        }
    }
}
