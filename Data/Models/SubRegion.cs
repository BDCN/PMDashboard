namespace PMDashboard.Data.Models;

/// <summary>
/// A part of a region (e.g. "East 1" in East), controlled by a PE. Clusters are mapped to one
/// (Location.SubRegionId) and a cluster can only be assigned to PMs on that sub-region's team —
/// see TeamRules.
/// </summary>
public class SubRegion
{
    public int      Id           { get; set; }
    public string   Name         { get; set; } = "";
    public string   Region       { get; set; } = "";   // East | Central | West | …
    public int      SortOrder    { get; set; }
    public DateTime LastModified { get; set; }

    public override string ToString() => Name;
}
