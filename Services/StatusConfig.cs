using PMDashboard.Data;

namespace PMDashboard.Services;

/// <summary>
/// App-lifetime cache of ROMP status configuration.
/// Call Reload() whenever the RompStatuses table changes.
/// Site.SiteStatus uses this cache since entity properties can't query the DB.
/// </summary>
public static class StatusConfig
{
    private static List<string>            _all          = new() { "Ordered", "OFCI", "Construction", "CX", "Complete" };
    private static List<string>            _selectable   = new() { "Ordered", "OFCI", "Construction", "CX", "Complete" };
    private static List<string>            _complete     = new() { "Complete" };
    private static List<string>            _active       = new() { "Construction", "CX" };
    private static List<string>            _workload     = new() { "Ordered", "OFCI", "Construction", "CX" };
    // Activity multipliers (double): Complete=0.1, Ordered=0.4, OFCI=0.6, Construction=1.0, CX=1.2
    private static Dictionary<string,double> _scoreWeights = new()
    {
        ["Ordered"] = 0.4, ["OFCI"] = 0.6, ["Construction"] = 1.0, ["CX"] = 1.2, ["Complete"] = 0.1
    };
    private static Dictionary<string,string> _colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ordered"] = "#94A3B8", ["OFCI"] = "#2563EB", ["Construction"] = "#F59E0B",
        ["CX"] = "#F97316", ["Complete"] = "#22C55E"
    };
    private const string FallbackColor = "#94A3B8";

    /// <summary>All statuses in display order — includes disabled ones, so scoring/SiteStatus and
    /// view/filter/report contexts still account for ROMPs sitting on a disabled status.</summary>
    public static IReadOnlyList<string> All      => _all;

    /// <summary>Statuses available to newly assign — i.e. not flagged IsDisabled. Use this (not
    /// All) for any "pick a status" control: new-ROMP dropdowns, bulk-set actions, import
    /// templates/validation. A ROMP already on a disabled status keeps it until explicitly moved
    /// off; this list only governs what can be picked going forward.</summary>
    public static IReadOnlyList<string> Selectable => _selectable;

    /// <summary>Statuses flagged IsComplete — excluded from workload site/ROMP counts.</summary>
    public static IReadOnlyList<string> Complete => _complete;

    /// <summary>Statuses flagged CountsAsActive — site with any ROMP here is "Active" (yellow).</summary>
    public static IReadOnlyList<string> Active   => _active;

    /// <summary>Non-complete, non-disabled statuses — shown in the Workload tooltip and PM
    /// Detail's ROMP Status Breakdown. A disabled status is meant to disappear from the app
    /// going forward, these breakdowns included, not just "assign a status" pickers.</summary>
    public static IReadOnlyList<string> Workload => _workload;

    /// <summary>Status name → activity multiplier (used in romp_score = multiplier × equip_weight_sum).</summary>
    public static IReadOnlyDictionary<string, double> ScoreWeights => _scoreWeights;

    /// <summary>Status name → indicator-dot hex color (Configuration page color picker). Prefer
    /// ColorFor(name) over indexing this directly — it never throws for an unknown/blank name.</summary>
    public static IReadOnlyDictionary<string, string> Colors => _colors;

    /// <summary>Hex color for a status name, falling back to a neutral grey for null/blank/unknown
    /// names (e.g. a ROMP whose status was deleted/renamed out from under it).</summary>
    public static string ColorFor(string? statusName) =>
        !string.IsNullOrEmpty(statusName) && _colors.TryGetValue(statusName, out var c) ? c : FallbackColor;

    public static void Reload()
    {
        using var db = new AppDbContext();
        var statuses = db.RompStatuses.OrderBy(s => s.SortOrder).ToList();
        if (!statuses.Any()) return;
        _all          = statuses.Select(s => s.Name).ToList();
        _selectable   = statuses.Where(s => !s.IsDisabled).Select(s => s.Name).ToList();
        _complete     = statuses.Where(s => s.IsComplete).Select(s => s.Name).ToList();
        _active       = statuses.Where(s => s.CountsAsActive).Select(s => s.Name).ToList();
        _workload     = statuses.Where(s => !s.IsComplete && !s.IsDisabled).Select(s => s.Name).ToList();
        _scoreWeights = statuses.ToDictionary(s => s.Name, s => s.ScoreWeight);
        _colors       = statuses.ToDictionary(s => s.Name, s => string.IsNullOrWhiteSpace(s.Color) ? FallbackColor : s.Color,
                                               StringComparer.OrdinalIgnoreCase);
    }
}
