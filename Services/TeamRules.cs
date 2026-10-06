using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>Thrown when an assignment would put a PM on a cluster outside their team's area.
/// App.OnDispatcherUnhandledException shows it as a plain "not allowed" message (no crash).</summary>
public sealed class TeamRuleException(string message) : InvalidOperationException(message);

/// <summary>
/// The team boundary for assignments (hard rule, checked in AssignmentHistoryService and
/// CampusService so no path can bypass it):
///   • a cluster mapped to a sub-region (Location.SubRegionId) can only be assigned to PMs whose
///     PE controls that sub-region (PM.PeId → TeamMember.SubRegionId);
///   • an unmapped cluster can be assigned to any PM in the same region (the PM's sub-region's
///     region if they're on a team, else PM.GeographicalLocation);
///   • a cluster with no region and no sub-region is unrestricted.
/// Load once per context with <see cref="For"/> — it's a small snapshot of PMs/clusters/sub-regions.
/// </summary>
public sealed class TeamRules
{
    private sealed record PmInfo(string Name, string Region, int? SubRegionId);
    private sealed record LocInfo(string Name, string Region, int? SubRegionId);
    private sealed record CampusInfo(string Name, int LocationId, int? SubRegionId);

    /// <summary>Where an assignment lands, team-wise: the sub-region that governs it (campus's own,
    /// else the cluster's), the region, and how to describe it in a message.</summary>
    private sealed record Area(int? SubRegionId, string Region, string Label);

    private readonly Dictionary<int, PmInfo>     _pms;
    private readonly Dictionary<int, LocInfo>    _locs;
    private readonly Dictionary<int, CampusInfo> _campuses;
    private readonly Dictionary<int, SubRegion>  _subs;

    private static readonly ConditionalWeakTable<AppDbContext, TeamRules> Cache = new();

    private TeamRules(Dictionary<int, PmInfo> pms, Dictionary<int, LocInfo> locs,
                      Dictionary<int, CampusInfo> campuses, Dictionary<int, SubRegion> subs)
        => (_pms, _locs, _campuses, _subs) = (pms, locs, campuses, subs);

    /// <summary>The rules as of this context's first use (cached per context).</summary>
    public static TeamRules For(AppDbContext db) => Cache.GetValue(db, Load);

    /// <summary>A fresh snapshot (own context) — for UI filtering.</summary>
    public static TeamRules Current()
    {
        using var db = new AppDbContext();
        return Load(db);
    }

    private static TeamRules Load(AppDbContext db)
    {
        var subs = db.SubRegions.AsNoTracking().ToDictionary(s => s.Id);
        int? Valid(int? id) => id is { } i && subs.ContainsKey(i) ? i : null;
        var peSub = db.TeamMembers.AsNoTracking()
                      .Where(t => t.Role == TeamRoles.Pe)
                      .Select(t => new { t.Id, t.SubRegionId })
                      .ToDictionary(t => t.Id, t => t.SubRegionId);
        var pms = db.PMs.AsNoTracking()
                    .Select(p => new { p.Id, p.FirstName, p.LastName, p.GeographicalLocation, p.PeId })
                    .AsEnumerable()
                    .ToDictionary(p => p.Id, p =>
                    {
                        int? sub = Valid(p.PeId is { } pe && peSub.TryGetValue(pe, out var s) ? s : null);
                        var region = sub != null ? subs[sub.Value].Region : p.GeographicalLocation ?? "";
                        return new PmInfo($"{p.FirstName} {p.LastName}".Trim(), region, sub);
                    });
        var locs = db.Locations.AsNoTracking()
                     .Select(l => new { l.Id, l.Name, l.GeographicalLocation, l.SubRegionId })
                     .AsEnumerable()
                     .ToDictionary(l => l.Id, l => new LocInfo(l.Name, l.GeographicalLocation ?? "", Valid(l.SubRegionId)));
        var campuses = db.Campuses.AsNoTracking()
                         .Select(c => new { c.Id, c.Name, c.LocationId, c.SubRegionId })
                         .AsEnumerable()
                         .ToDictionary(c => c.Id, c => new CampusInfo(c.Name, c.LocationId, Valid(c.SubRegionId)));
        return new TeamRules(pms, locs, campuses, subs);
    }

    /// <summary>A campus's own sub-region wins; otherwise the cluster's; otherwise the cluster's region.</summary>
    private Area? AreaOf(int locationId, int? campusId)
    {
        _locs.TryGetValue(locationId, out var loc);
        CampusInfo? campus = campusId is { } cid && _campuses.TryGetValue(cid, out var c) ? c : null;
        loc ??= campus != null && _locs.TryGetValue(campus.LocationId, out var cl) ? cl : null;
        if (loc == null) return null;

        if (campus?.SubRegionId is { } own)
            return new Area(own, _subs[own].Region, $"campus {campus.Name}");
        if (loc.SubRegionId is { } inherited)
            return new Area(inherited, _subs[inherited].Region, campus != null ? $"campus {campus.Name} (cluster {loc.Name})" : $"cluster {loc.Name}");
        return new Area(null, loc.Region, campus != null ? $"campus {campus.Name} (cluster {loc.Name})" : $"cluster {loc.Name}");
    }

    public bool Allows(int pmId, int locationId, int? campusId = null) => Reason(pmId, locationId, campusId) == null;

    /// <summary>Why this PM can't take this cluster/campus, or null when they can. Pass the campus
    /// whenever there is one — a campus can belong to a different sub-region than its cluster.</summary>
    public string? Reason(int pmId, int locationId, int? campusId = null)
    {
        if (AreaOf(locationId, campusId) is not { } area) return null;
        if (!_pms.TryGetValue(pmId, out var pm)) return null; // unknown PM — nothing to check against

        if (area.SubRegionId is { } sub)
        {
            if (pm.SubRegionId == sub) return null;
            var subName = _subs[sub].Name;
            return pm.SubRegionId is { } pmSub
                ? $"{pm.Name} is on the {_subs[pmSub].Name} team, but {area.Label} is in {subName}. Only PMs on the {subName} team can be assigned there."
                : $"{pm.Name} isn't on a team yet, and {area.Label} is in {subName}. Put them under the {subName} PE on the Teams page first.";
        }

        if (string.IsNullOrWhiteSpace(area.Region)) return null;
        if (string.Equals(pm.Region, area.Region, StringComparison.OrdinalIgnoreCase)) return null;
        return $"{pm.Name} is in {(string.IsNullOrWhiteSpace(pm.Region) ? "no region" : pm.Region)}, but {area.Label} is in {area.Region}. " +
               "PMs can only be assigned within their own region (and sub-region, once there is one).";
    }

    /// <summary>Throws <see cref="TeamRuleException"/> if this PM can't take this cluster/campus.</summary>
    public void Ensure(int pmId, int locationId, int? campusId = null)
    {
        if (Reason(pmId, locationId, campusId) is { } why) throw new TeamRuleException(why);
    }

    /// <summary>The PMs from <paramref name="pms"/> that can be assigned to this cluster/campus.</summary>
    public List<PM> Eligible(IEnumerable<PM> pms, int locationId, int? campusId = null) =>
        pms.Where(p => Allows(p.Id, locationId, campusId)).ToList();

    /// <summary>PMs allowed on every campus of a cluster (and the cluster itself when it has none) —
    /// for actions that assign a whole cluster or several campuses at once.</summary>
    public List<PM> EligibleForAll(IEnumerable<PM> pms, int locationId, IEnumerable<int> campusIds)
    {
        var ids = campusIds.ToList();
        return ids.Count == 0
            ? Eligible(pms, locationId)
            : pms.Where(p => ids.All(c => Allows(p.Id, locationId, c))).ToList();
    }

    /// <summary>Campus Ids in a cluster (from the snapshot).</summary>
    public List<int> CampusIdsIn(int locationId) =>
        _campuses.Where(kv => kv.Value.LocationId == locationId).Select(kv => kv.Key).ToList();

    /// <summary>"East 1" for a PM on a team, else their region ("East"), else "".</summary>
    public string PmArea(int pmId) =>
        _pms.TryGetValue(pmId, out var pm) ? (pm.SubRegionId is { } s ? _subs[s].Name : pm.Region) : "";

    /// <summary>The PM's sub-region name ("" when not on a team) and region.</summary>
    public (string SubRegion, string Region) PmTeam(int pmId) =>
        _pms.TryGetValue(pmId, out var pm) ? (pm.SubRegionId is { } s ? _subs[s].Name : "", pm.Region) : ("", "");

    /// <summary>The cluster's own sub-region name, or "" when unmapped (campuses may differ).</summary>
    public string ClusterSubRegion(int locationId) =>
        _locs.TryGetValue(locationId, out var l) && l.SubRegionId is { } s ? _subs[s].Name : "";

    /// <summary>The sub-region that governs this campus/cluster ("" when none) and its region.</summary>
    public (string SubRegion, string Region) EffectiveArea(int locationId, int? campusId = null) =>
        AreaOf(locationId, campusId) is { } a ? (a.SubRegionId is { } s ? _subs[s].Name : "", a.Region) : ("", "");

    /// <summary>True when any campus in the cluster has its own sub-region different from the cluster's.</summary>
    public bool IsSplit(int locationId)
    {
        var clusterSub = _locs.TryGetValue(locationId, out var l) ? l.SubRegionId : null;
        return _campuses.Values.Any(c => c.LocationId == locationId && c.SubRegionId != null && c.SubRegionId != clusterSub);
    }

    /// <summary>Sub-region for a forecast site not in the system: its campus code (if a campus
    /// with that name exists in the cluster) first, else the cluster's.</summary>
    public string SubRegionForForecast(string clusterName, string? campusName)
    {
        var locId = _locs.FirstOrDefault(kv => string.Equals(kv.Value.Name, clusterName, StringComparison.OrdinalIgnoreCase)).Key;
        if (locId == 0) return "";
        int? campusId = string.IsNullOrWhiteSpace(campusName) ? null
            : _campuses.FirstOrDefault(kv => kv.Value.LocationId == locId && kv.Value.Name == campusName).Key is var k && k != 0 ? k : null;
        return EffectiveArea(locId, campusId).SubRegion;
    }
}
