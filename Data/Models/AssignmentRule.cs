namespace PMDashboard.Data.Models;

public class AssignmentRule
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public string RuleKey     { get; set; } = "Custom"; // RegionMatch | LocationFamiliarity | CapacityAvailability | Custom
    public int    Weight      { get; set; } = 1;        // multiplier applied to the rule's raw score
    public bool   IsActive    { get; set; } = true;
    public int    SortOrder   { get; set; }
    public bool     IsBuiltIn    { get; set; }             // built-in rules cannot be deleted
    public DateTime LastModified { get; set; }
}
