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

    /// <summary>The PE this Platform Manager reports to — kept as the (first active) PE of their
    /// default sub-region by PmTeamService. Older data: when SubRegionId is null, the PE's
    /// sub-region is used as the default (TeamRules).</summary>
    public int?        PeId { get; set; }
    public TeamMember? Pe   { get; set; }

    /// <summary>Default sub-region (the team they mainly sit on). A PM — with their PAs — can be
    /// on several sub-regions: this one plus <see cref="OtherSubRegionIds"/>. Set via PmTeamService.</summary>
    public int? SubRegionId { get; set; }

    /// <summary>Further sub-regions, comma-separated Ids ("20000002,20000005"). Stored on the PM row
    /// (not a link table) so two machines editing it can never create duplicate rows — sync has
    /// no deletes.</summary>
    public string OtherSubRegionIds { get; set; } = "";

    public IEnumerable<int> OtherSubRegionIdList => ParseIds(OtherSubRegionIds);

    public static IEnumerable<int> ParseIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).Distinct();

    public bool     IsActive     { get; set; } = true;
    public DateTime LastModified { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>Display only (EF-ignored): "East1 — Brenda Meade-Nolan", filled by PM Maintenance.</summary>
    public string TeamLabel { get; set; } = "";
}
