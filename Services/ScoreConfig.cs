using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;

namespace PMDashboard.Services;

/// <summary>
/// App-lifetime cache of equipment scoring categories and workload parameters.
/// Call Reload() whenever EquipmentCategories, EquipmentKeywords, or workload config changes.
/// </summary>
public static class ScoreConfig
{
    public record CategoryRule(string Name, IReadOnlyList<string> Keywords, double Weight, bool IsExcluded);

    private static List<CategoryRule>    _categories    = DefaultCategories();
    private static string                _itemPrefix    = "SOLI";
    private static ScoringEngine.WorkloadParams _params = ScoringEngine.DefaultParams;

    /// <summary>All categories in sort order — exclusions first, then scoring categories.</summary>
    public static IReadOnlyList<CategoryRule> Categories => _categories;

    /// <summary>Orders with Item starting with this prefix are eligible for equipment scoring (legacy import display).</summary>
    public static string ItemFilterPrefix => _itemPrefix;

    /// <summary>Current workload scoring parameters loaded from AppConfig.</summary>
    public static ScoringEngine.WorkloadParams WorkloadParams => _params;

    /// <summary>Category name → weight map for non-excluded categories. Used by ScoringEngine.EquipWeightSum.</summary>
    public static IReadOnlyDictionary<string, double> CategoryWeights { get; private set; }
        = BuildCategoryWeights(DefaultCategories());

    public static void Reload()
    {
        using var db = new AppDbContext();
        var cats = db.EquipmentCategories
                     .AsNoTracking()
                     .Include(c => c.Keywords)
                     .OrderBy(c => c.SortOrder)
                     .ToList();

        if (cats.Any())
        {
            _categories = cats.Select(c => new CategoryRule(
                c.Name,
                c.Keywords.Select(k => k.Keyword).ToList(),
                c.Weight,
                c.IsExcluded
            )).ToList();
        }

        CategoryWeights = BuildCategoryWeights(_categories);

        var cs = new ConfigService();
        _itemPrefix = cs.Get(ConfigKeys.ItemFilterPrefix, "SOLI");

        _params = new ScoringEngine.WorkloadParams(
            SiteOverhead:      ParseDouble(cs.Get(ConfigKeys.SiteOverhead,      "0.05"), 0.05),
            TargetCapacity:    ParseDouble(cs.Get(ConfigKeys.TargetCapacity,    "259.2"), 259.2),
            NearFullThreshold: ParseDouble(cs.Get(ConfigKeys.NearFullThreshold, "0.80"), 0.80),
            OverloadThreshold: ParseDouble(cs.Get(ConfigKeys.OverloadThreshold, "1.00"), 1.00))
        {
            SkillBase     = ParseDouble(cs.Get(ConfigKeys.SkillBase,     "0.40"), 0.40),
            SkillPerPoint = ParseDouble(cs.Get(ConfigKeys.SkillPerPoint, "0.06"), 0.06),
        };
    }

    /// <summary>
    /// Sum of weights for the named equipment categories present on a ROMP.
    /// Delegates to ScoringEngine so callers don't need to pass the weight map separately.
    /// </summary>
    public static double EquipmentWeightSum(IEnumerable<string> categoryNames)
        => ScoringEngine.EquipWeightSum(categoryNames, CategoryWeights);

    private static IReadOnlyDictionary<string, double> BuildCategoryWeights(IEnumerable<CategoryRule> cats)
        => cats.Where(c => !c.IsExcluded)
               .ToDictionary(c => c.Name, c => c.Weight, StringComparer.OrdinalIgnoreCase);

    private static double ParseDouble(string s, double fallback)
        => double.TryParse(s, System.Globalization.NumberStyles.Any,
               System.Globalization.CultureInfo.InvariantCulture, out var v)
           ? v : fallback;

    private static List<CategoryRule> DefaultCategories() => new()
    {
        new("FS Exclusion", new[] { "FSPS", "FSWTY", "FS Field" }, 0.0, true),
        new("PAX",  new[] { "PIU", "PIE", "CDP", "ATS" },           1.6, false),
        new("LV",   new[] { "LVSW", "CEELV", "LV" },                1.3, false),
        new("MV",   new[] { "MV SWGR", "SWGR", "MV" },              1.0, false),
        new("PDC",  new[] { "PDU", "PDC" },                          0.6, false),
    };
}
