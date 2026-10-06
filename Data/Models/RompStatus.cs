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
    // Hides this status from "assign a status" pickers (new ROMPs, bulk set, import) without
    // touching ROMPs already on it — they keep scoring/counting exactly as before. See
    // StatusConfig.Selectable vs StatusConfig.All.
    public bool     IsDisabled   { get; set; }
    // Indicator-dot color, "#RRGGBB". Drives Romp.StatusColor directly and Site.StatusColor via
    // the most-advanced-status-present rule — see StatusConfig.ColorFor.
    public string   Color        { get; set; } = "#94A3B8";
    public DateTime LastModified { get; set; }
}
