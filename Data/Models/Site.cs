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

    // Derived from child ROMP statuses — not stored, ignored by EF Core in OnModelCreating.
    // Uses StatusConfig so the Active/Complete determination is driven by the configured statuses.
    public string SiteStatus
    {
        get
        {
            if (!Romps.Any()) return "Ordered";
            if (Romps.Any(r => StatusConfig.Active.Contains(r.Status))) return "Active";
            if (Romps.All(r => StatusConfig.Complete.Contains(r.Status))) return "Complete";
            return "Ordered";
        }
    }
}
