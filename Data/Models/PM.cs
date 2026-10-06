namespace PMDashboard.Data.Models;

public class PM
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string HomeOffice { get; set; } = "";          // FRE, VAN, JAX, Remote
    public string GeographicalLocation { get; set; } = ""; // East, West
    public string DataName { get; set; } = "";
    public double Capacity         { get; set; }
    public double IdealUtilization { get; set; }
    public double ScoreCapacity    { get; set; }  // max total difficulty score = 100% utilization (score mode)

    // Experience multiplier 1–10. Scales effective capacity: factor = SkillBase + SkillPerPoint × multiplier.
    // null → treat as 1.0 factor (no adjustment). Default 10 = full capacity for new/existing PMs.
    public int?   PmMultiplier { get; set; } = 10;

    /// <summary>The PE this Platform Manager reports to. The PE's sub-region limits which clusters
    /// this PM can be assigned (TeamRules).</summary>
    public int?        PeId { get; set; }
    public TeamMember? Pe   { get; set; }

    public bool     IsActive     { get; set; } = true;
    public DateTime LastModified { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}
