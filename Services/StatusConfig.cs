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
    private static List<string>            _complete     = new() { "Complete" };
    private static List<string>            _active       = new() { "Construction", "CX" };
    private static List<string>            _workload     = new() { "Ordered", "OFCI", "Construction", "CX" };
    // Activity multipliers (double): Complete=0.1, Ordered=0.4, OFCI=0.6, Construction=1.0, CX=1.2
    private static Dictionary<string,double> _scoreWeights = new()
    {
        ["Ordered"] = 0.4, ["OFCI"] = 0.6, ["Construction"] = 1.0, ["CX"] = 1.2, ["Complete"] = 0.1
    };

    /// <summary>All statuses in display order.</summary>
    public static IReadOnlyList<string> All      => _all;

    /// <summary>Statuses flagged IsComplete — excluded from workload site/ROMP counts.</summary>
    public static IReadOnlyList<string> Complete => _complete;

    /// <summary>Statuses flagged CountsAsActive — site with any ROMP here is "Active" (yellow).</summary>
    public static IReadOnlyList<string> Active   => _active;

    /// <summary>Non-complete statuses — shown in workload tooltip breakdown.</summary>
    public static IReadOnlyList<string> Workload => _workload;

    /// <summary>Status name → activity multiplier (used in romp_score = multiplier × equip_weight_sum).</summary>
    public static IReadOnlyDictionary<string, double> ScoreWeights => _scoreWeights;

    public static void Reload()
    {
        using var db = new AppDbContext();
        var statuses = db.RompStatuses.OrderBy(s => s.SortOrder).ToList();
        if (!statuses.Any()) return;
        _all          = statuses.Select(s => s.Name).ToList();
        _complete     = statuses.Where(s => s.IsComplete).Select(s => s.Name).ToList();
        _active       = statuses.Where(s => s.CountsAsActive).Select(s => s.Name).ToList();
        _workload     = statuses.Where(s => !s.IsComplete).Select(s => s.Name).ToList();
        _scoreWeights = statuses.ToDictionary(s => s.Name, s => s.ScoreWeight);
    }
}
