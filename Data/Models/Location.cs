namespace PMDashboard.Data.Models;

public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = "";               // e.g. "IAD", "PDX"
    public string GeographicalLocation { get; set; } = ""; // East, West

    public DateTime LastModified { get; set; }
    public ICollection<Site> Sites { get; set; } = new List<Site>();
}
