using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Single source of truth for "which PM does this ROMP's score count toward" — a ROMP with its
/// own AssignedPmId counts toward THAT PM even if it differs from its Campus's assigned PM (an
/// override, e.g. a specialist handling one piece of equipment while the generalist covers the
/// rest of the campus); a ROMP with no override falls back to its Campus's PM. Every place that
/// aggregates ROMP scores/counts per PM (WorkloadViewModel, PmDetailViewModel,
/// AssignmentViewModel's candidate scoring) must use this instead of re-deriving the rule
/// independently, so the three can't quietly drift out of sync with each other.
/// </summary>
public static class AssignmentResolver
{
    public static int? EffectivePmId(int? rompAssignedPmId, int? siteAssignedPmId) =>
        rompAssignedPmId ?? siteAssignedPmId;

    public static int? EffectivePmId(Romp romp) =>
        EffectivePmId(romp.AssignedPmId, romp.Site?.AssignedPmId);
}
