using PMDashboard.Services;

namespace PMDashboard.Data.Models;

public class Romp
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string   Status       { get; set; } = "Ordered";
    public DateTime LastModified { get; set; }

    /// <summary>Email of whoever last wrote this row; stamped by AppDbContext.SaveChanges.</summary>
    public string LastModifiedBy { get; set; } = "";

    // Equipment types directly assigned to this ROMP (many-to-many via RompEquipmentType).
    public ICollection<RompEquipmentType> EquipmentTypes { get; set; } = new List<RompEquipmentType>();

    // Derived from grandparent location via site
    public string GeographicalLocation => Site?.Location?.GeographicalLocation ?? "";

    // ── Display helpers (require EquipmentTypes to be eagerly loaded) ────────

    /// <summary>
    /// Comma-separated list of equipment category names on this ROMP.
    /// Returns "—" when no equipment types are assigned.
    /// </summary>
    public string EquipmentTypesText =>
        EquipmentTypes.Count == 0
            ? "—"
            : string.Join(", ", EquipmentTypes
                .Select(et => et.Category?.Name ?? "?")
                .OrderBy(n => n));

    /// <summary>
    /// Multiplicative ROMP score: activity_weight × equip_weight_sum.
    /// Requires EquipmentTypes (+ Category) to be loaded and StatusConfig/ScoreConfig to be initialised.
    /// </summary>
    public double RompScore
    {
        get
        {
            double activity = StatusConfig.ScoreWeights.TryGetValue(Status, out var aw) ? aw : 0;
            double equipSum = ScoreConfig.EquipmentWeightSum(
                                  EquipmentTypes.Select(et => et.Category?.Name ?? ""));
            return ScoringEngine.RompScore(activity, equipSum);
        }
    }
}
