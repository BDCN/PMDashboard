namespace PMDashboard.Data.Models;

public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = "";               // e.g. "IAD", "PDX"
    public string GeographicalLocation { get; set; } = ""; // East, West

    public DateTime LastModified { get; set; }
    public ICollection<Site> Sites { get; set; } = new List<Site>();

    // Derived from child Site statuses — not stored, ignored by EF Core in OnModelCreating.
    // Relies on Sites (and each Site's Romps) being eager-loaded by the caller; defaults to
    // false when they aren't, which is the safe "don't hide it" outcome.
    public bool IsFullyClosed => Sites.Any() && Sites.All(s => s.SiteStatus == "Complete");
}
