using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// The one place a PM's team membership changes: default sub-region + other sub-regions (their
/// PAs follow them). Keeps PM.PeId pointing at the default sub-region's first active PE, so the
/// "reports to" link and older builds that only know PeId stay consistent. Doesn't save.
/// </summary>
public static class PmTeamService
{
    public static void SetSubRegions(AppDbContext db, PM pm, int? defaultSubRegionId, IEnumerable<int> others)
    {
        var rest = others.Where(id => id > 0 && id != defaultSubRegionId).Distinct().OrderBy(id => id).ToList();
        // No default but some others → the first other becomes the default.
        if (defaultSubRegionId == null && rest.Count > 0) { defaultSubRegionId = rest[0]; rest.RemoveAt(0); }

        pm.SubRegionId       = defaultSubRegionId;
        pm.OtherSubRegionIds = string.Join(",", rest);
        pm.PeId = defaultSubRegionId is { } sub
            ? db.TeamMembers.Where(t => t.Role == TeamRoles.Pe && t.IsActive && t.SubRegionId == sub)
                            .OrderBy(t => t.LastName).ThenBy(t => t.FirstName).Select(t => (int?)t.Id).FirstOrDefault()
            : null;
    }

    /// <summary>"East1 (default), East2" style text for a PM's sub-regions.</summary>
    public static string Describe(int? defaultId, IEnumerable<int> others, IReadOnlyDictionary<int, string> names)
    {
        var parts = new List<string>();
        if (defaultId is { } d && names.TryGetValue(d, out var dn)) parts.Add(dn + " (default)");
        parts.AddRange(others.Where(o => o != defaultId && names.ContainsKey(o)).Select(o => names[o]));
        return string.Join(", ", parts);
    }
}
