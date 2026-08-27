namespace PMDashboard.Services;

/// <summary>
/// Pure, stateless scoring logic. No DB or WPF dependencies — fully unit-testable.
///
/// Formula:
///   equip_weight_sum  = Σ weight[t]  for each equipment type t on the ROMP
///   romp_score        = activity_weight × equip_weight_sum
///   site_factor       = 1 + site_overhead × distinct_site_count
///   pm_load           = site_factor × Σ romp_score  (over all active ROMPs)
///   utilization       = pm_load / target_capacity
///   availability      = max(0, 1 − utilization)
/// </summary>
public static class ScoringEngine
{
    /// <summary>Configurable parameters for workload scoring.</summary>
    public readonly record struct WorkloadParams(
        double SiteOverhead,
        double TargetCapacity,
        double NearFullThreshold,
        double OverloadThreshold)
    {
        // Skill-factor params — stored as init properties so the positional constructor
        // still takes exactly the same 4 arguments (backward-compatible with tests).
        public double SkillBase     { get; init; } = 0.40;
        public double SkillPerPoint { get; init; } = 0.06;
    }

    public static readonly WorkloadParams DefaultParams = new(
        SiteOverhead:      0.05,
        TargetCapacity:    259.2,
        NearFullThreshold: 0.80,
        OverloadThreshold: 1.00);

    // ── Per-ROMP ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Equipment weight sum for a ROMP: sum of Weight values for each named category.
    /// Unknown category names contribute 0 (fail-safe).
    /// </summary>
    public static double EquipWeightSum(
        IEnumerable<string> categoryNames,
        IReadOnlyDictionary<string, double> categoryWeights)
    {
        double sum = 0;
        foreach (var name in categoryNames)
            sum += categoryWeights.TryGetValue(name, out var w) ? w : 0;
        return sum;
    }

    /// <summary>
    /// Score for a single ROMP.
    /// Unknown/unmapped activity weight → 0 (fail-safe, no crash).
    /// </summary>
    public static double RompScore(double activityWeight, double equipWeightSum)
        => activityWeight * equipWeightSum;

    // ── Per-PM ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Multiplier that scales ROMP score sum to account for the overhead of managing
    /// multiple distinct sites.  distinctSites = count of non-complete sites.
    /// </summary>
    public static double SiteFactor(int distinctSites, WorkloadParams p)
        => 1.0 + p.SiteOverhead * distinctSites;

    /// <summary>
    /// Total PM load = site_factor × Σ romp_scores.
    /// distinctSites should be the count of unique non-complete sites (not ROMP count).
    /// </summary>
    public static double PmLoad(IEnumerable<double> rompScores, int distinctSites, WorkloadParams p)
        => SiteFactor(distinctSites, p) * rompScores.Sum();

    // ── Skill / experience factor ─────────────────────────────────────────────

    /// <summary>
    /// skill_factor = skill_base + skill_per_point × pm_multiplier.
    /// Returns 1.0 when pmMultiplier is null (no adjustment — same as multiplier 10 at defaults).
    /// </summary>
    public static double SkillFactor(int? pmMultiplier, WorkloadParams p)
        => pmMultiplier.HasValue
           ? p.SkillBase + p.SkillPerPoint * pmMultiplier.Value
           : 1.0;

    /// <summary>
    /// eff_capacity = target_capacity × skill_factor.
    /// Use this as the utilization denominator instead of TargetCapacity when a PM multiplier is set.
    /// </summary>
    public static double EffectiveCapacity(WorkloadParams p, int? pmMultiplier)
        => p.TargetCapacity * SkillFactor(pmMultiplier, p);

    // ── Availability & status ─────────────────────────────────────────────────

    /// <summary>
    /// Fraction of capacity consumed, using a pre-computed effective capacity.
    /// This is the primary overload used by ViewModels — pass EffectiveCapacity(p, pm.PmMultiplier).
    /// </summary>
    public static double Utilization(double pmLoad, double effCapacity)
        => effCapacity > 0 ? pmLoad / effCapacity : 0;

    /// <summary>
    /// Backward-compatible overload: uses TargetCapacity directly (skill_factor = 1.0).
    /// Kept so existing unit tests compile unchanged.
    /// </summary>
    public static double Utilization(double pmLoad, WorkloadParams p)
        => Utilization(pmLoad, p.TargetCapacity);

    /// <summary>Available fraction (0..1). Clamped to 0 when overloaded.</summary>
    public static double Availability(double utilization)
        => Math.Max(0.0, 1.0 - utilization);

    /// <summary>
    /// Human-readable PM status derived from utilization ratio.
    ///   > overload_threshold  → "Overloaded"
    ///   ≥ nearfull_threshold  → "Near Full"
    ///   else                  → "Has Capacity"
    /// </summary>
    public static string PmStatus(double utilization, WorkloadParams p)
    {
        if (utilization > p.OverloadThreshold)  return "Overloaded";
        if (utilization >= p.NearFullThreshold) return "Near Full";
        return "Has Capacity";
    }
}
