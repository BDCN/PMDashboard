namespace PMDashboard.Data.Models;

public class RompStatus
{
    public int    Id             { get; set; }
    public string Name           { get; set; } = "";
    public bool   IsComplete     { get; set; }   // excluded from workload counts
    public bool   CountsAsActive { get; set; }   // site with any ROMP here → SiteStatus = "Active"
    public double ScoreWeight    { get; set; }   // activity multiplier for ROMPs in this status (used in romp_score = activity × equip_weight_sum)
    public int      SortOrder    { get; set; }
    public bool     IsBuiltIn    { get; set; }   // cannot be deleted
    public DateTime LastModified { get; set; }
}
