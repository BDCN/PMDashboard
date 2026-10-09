using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>Thrown when an assignment would put a PM on a cluster outside their team's area.
/// App.OnDispatcherUnhandledException shows it as a plain "not allowed" message (no crash).</summary>
public sealed class TeamRuleException(string message) : InvalidOperationException(message);

/// <summary>
/// The team boundary for assignments (checked in AssignmentHistoryService and CampusService so no
/// path can bypass it; an out-of-team assignment needs an acknowledged warning — the UI retries
/// inside <see cref="AllowOutsideTeam"/>, see AcknowledgeDialog.RunAssignment):
///   • a cluster mapped to a sub-region (Location.SubRegionId, or a campus's own) can only be
///     assigned to PMs on that sub-region — their default (PM.SubRegionId; for older data the
///     PE's sub-region via PM.PeId) or one of their others (PM.OtherSubRegionIds);
///   • an unmapped cluster can be assigned to any PM in the same region (the PM's sub-region's
///     region if they're on a team, else PM.GeographicalLocation);
///   • a cluster with no region and no sub-region is unrestricted.
/// Load once per context with <see cref="For"/> — it's a small snapshot of PMs/clusters/sub-regions.
/// </summary>
public sealed class TeamRules
{
    /// <summary>SubRegionId = default sub-region; All = default + others.</summary>
    private sealed record PmInfo(string Name, string Region, int? SubRegionId, HashSet<int> All);
    private sealed record LocInfo(string Name, string Region, int? SubRegionId);
    private sealed record CampusInfo(string Name, int LocationId, int? SubRegionId);
    private sealed record SiteInfo(string Name, int LocationId, int? CampusId, int SubRegionId);

    /// <summary>Where an assignment lands, team-wise: the sub-region that governs it (campus's own,
    /// else the cluster's), the region, and how to describe it in a message.</summary>
    private sealed record Area(int? SubRegionId, string Region, string Label);

    private readonly Dictionary<int, PmInfo>     _pms;
    private readonly Dictionary<int, LocInfo>    _locs;
    private readonly Dictionary<int, CampusInfo> _campuses;
    private readonly Dictionary<int, SubRegion>  _subs;
    private readonly Dictionary<int, SiteInfo>   _sites;   // only sites with their own sub-region

    private static readonly ConditionalWeakTable<AppDbContext, TeamRules> Cache = new();

    private TeamRules(Dictionary<int, PmInfo> pms, Dictionary<int, LocInfo> locs,
                      Dictionary<int, CampusInfo> campuses, Dictionary<int, SubRegion> subs, Dictionary<int, SiteInfo> sites)
        => (_pms, _locs, _campuses, _subs, _sites) = (pms, locs, campuses, subs, sites);

    // ── Acknowledged exceptions ──
    // Assigning outside the team is allowed when the user has seen and acknowledged a warning
    // (OutsideTeamDialog). The UI runs the action again inside this scope; Ensure then lets it
    // through and records it in the audit log instead of throwing.
    [ThreadStatic] private static int _allowOutside;
    [ThreadStatic] private static HashSet<string>? _loggedInScope;   // one audit entry per distinct exception, not per site

    /// <summary>Within this scope, out-of-team assignments are allowed (and audited).</summary>
    public static IDisposable AllowOutsideTeam()
    {
        if (_allowOutside++ == 0) _loggedInScope = new();
        return new Scope();
    }
    public static bool OutsideTeamAllowed => _allowOutside > 0;
    private sealed class Scope : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--_allowOutside == 0) _loggedInScope = null;
        }
    }

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
                    .Select(p => new { p.Id, p.FirstName, p.LastName, p.GeographicalLocation, p.PeId, p.SubRegionId, p.OtherSubRegionIds })
                    .AsEnumerable()
                    .ToDictionary(p => p.Id, p =>
                    {
                        // Default: PM.SubRegionId; older rows (set before multi-sub-region) → the PE's.
                        int? sub = Valid(p.SubRegionId) ?? Valid(p.PeId is { } pe && peSub.TryGetValue(pe, out var s) ? s : null);
                        var all = PM.ParseIds(p.OtherSubRegionIds)
                                      .Where(id => subs.ContainsKey(id)).ToHashSet();
                        if (sub != null) all.Add(sub.Value);
                        else if (all.Count > 0) sub = all.Min();
                        var region = sub != null ? subs[sub.Value].Region : p.GeographicalLocation ?? "";
                        return new PmInfo($"{p.FirstName} {p.LastName}".Trim(), region, sub, all);
                    });
        var locs = db.Locations.AsNoTracking()
                     .Select(l => new { l.Id, l.Name, l.GeographicalLocation, l.SubRegionId })
                     .AsEnumerable()
                     .ToDictionary(l => l.Id, l => new LocInfo(l.Name, l.GeographicalLocation ?? "", Valid(l.SubRegionId)));
        var campuses = db.Campuses.AsNoTracking()
                         .Select(c => new { c.Id, c.Name, c.LocationId, c.SubRegionId })
                         .AsEnumerable()
                         .ToDictionary(c => c.Id, c => new CampusInfo(c.Name, c.LocationId, Valid(c.SubRegionId)));
        var sites = db.Sites.AsNoTracking().Where(st => st.SubRegionId != null)
                      .Select(st => new { st.Id, st.Name, st.LocationId, st.CampusId, st.SubRegionId })
                      .AsEnumerable()
                      .Where(st => Valid(st.SubRegionId) != null)
                      .ToDictionary(st => st.Id, st => new SiteInfo(st.Name, st.LocationId, st.CampusId, st.SubRegionId!.Value));
        return new TeamRules(pms, locs, campuses, subs, sites);
    }

    /// <summary>The most specific own sub-region wins: site's, else campus's, else the cluster's;
    /// otherwise the cluster's region. (Clusters and campuses can be split across sub-regions and
    /// regions — the user acknowledges a warning when splitting.)</summary>
    private Area? AreaOf(int locationId, int? campusId, int? siteId = null)
    {
        _locs.TryGetValue(locationId, out var loc);
        CampusInfo? campus = campusId is { } cid && _campuses.TryGetValue(cid, out var c) ? c : null;
        loc ??= campus != null && _locs.TryGetValue(campus.LocationId, out var cl) ? cl : null;
        if (loc == null) return null;

        if (siteId is { } sid && _sites.TryGetValue(sid, out var site))
            return new Area(site.SubRegionId, _subs[site.SubRegionId].Region, $"site {site.Name}");
        if (campus?.SubRegionId is { } own)
            return new Area(own, _subs[own].Region, $"campus {campus.Name}");
        if (loc.SubRegionId is { } inherited)
            return new Area(inherited, _subs[inherited].Region, campus != null ? $"campus {campus.Name} (cluster {loc.Name})" : $"cluster {loc.Name}");
        return new Area(null, loc.Region, campus != null ? $"campus {campus.Name} (cluster {loc.Name})" : $"cluster {loc.Name}");
    }

    public bool Allows(int pmId, int locationId, int? campusId = null, int? siteId = null) => Reason(pmId, locationId, campusId, siteId) == null;

    /// <summary>Why this PM can't take this cluster/campus, or null when they can. Pass the campus
    /// whenever there is one — a campus can belong to a different sub-region than its cluster.</summary>
    public string? Reason(int pmId, int locationId, int? campusId = null, int? siteId = null)
    {
        if (AreaOf(locationId, campusId, siteId) is not { } area) return null;
        if (!_pms.TryGetValue(pmId, out var pm)) return null; // unknown PM — nothing to check against

        if (area.SubRegionId is { } sub)
        {
            if (pm.All.Contains(sub)) return null;
            var subName = _subs[sub].Name;
            return pm.All.Count > 0
                ? $"{pm.Name} is on {string.Join(", ", pm.All.Select(i => _subs[i].Name).OrderBy(n => n))}, but {area.Label} is in {subName}. Only PMs on the {subName} team can be assigned there."
                : $"{pm.Name} isn't on a team yet, and {area.Label} is in {subName}. Add them to {subName} first (Org Chart → Edit, or PM Maintenance → Sub-regions).";
        }

        if (string.IsNullOrWhiteSpace(area.Region)) return null;
        if (string.Equals(pm.Region, area.Region, StringComparison.OrdinalIgnoreCase)) return null;
        return $"{pm.Name} is in {(string.IsNullOrWhiteSpace(pm.Region) ? "no region" : pm.Region)}, but {area.Label} is in {area.Region}. " +
               "PMs can only be assigned within their own region (and sub-region, once there is one).";
    }

    /// <summary>Throws <see cref="TeamRuleException"/> if this PM can't take this cluster/campus/site —
    /// unless inside <see cref="AllowOutsideTeam"/>, where it's allowed and audited instead.</summary>
    public void Ensure(int pmId, int locationId, int? campusId = null, int? siteId = null)
    {
        if (Reason(pmId, locationId, campusId, siteId) is not { } why) return;
        if (!OutsideTeamAllowed) throw new TeamRuleException(why);
        if (_loggedInScope?.Add($"{pmId}|{why}") != false)
            AuditService.Log("OutsideTeam", "PM", pmId, "Assigned outside team (acknowledged): " + why);
    }

    /// <summary>The PMs from <paramref name="pms"/> that can be assigned to this cluster/campus/site.</summary>
    public List<PM> Eligible(IEnumerable<PM> pms, int locationId, int? campusId = null, int? siteId = null) =>
        pms.Where(p => Allows(p.Id, locationId, campusId, siteId)).ToList();

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

    /// <summary>All of a PM's sub-region names, default first.</summary>
    public List<string> PmSubRegions(int pmId) =>
        _pms.TryGetValue(pmId, out var pm)
            ? pm.All.OrderBy(i => i == pm.SubRegionId ? 0 : 1).ThenBy(i => _subs[i].Name).Select(i => _subs[i].Name).ToList()
            : new();

    /// <summary>True when the PM is on this sub-region (default or other).</summary>
    public bool PmOn(int pmId, string subRegionName) => PmSubRegions(pmId).Contains(subRegionName);

    /// <summary>The PM's default sub-region name ("" when not on a team) and its region.</summary>
    public (string SubRegion, string Region) PmTeam(int pmId) =>
        _pms.TryGetValue(pmId, out var pm) ? (pm.SubRegionId is { } s ? _subs[s].Name : "", pm.Region) : ("", "");

    /// <summary>The cluster's own sub-region name, or "" when unmapped (campuses may differ).</summary>
    public string ClusterSubRegion(int locationId) =>
        _locs.TryGetValue(locationId, out var l) && l.SubRegionId is { } s ? _subs[s].Name : "";

    /// <summary>The sub-region that governs this campus/cluster ("" when none) and its region.</summary>
    public (string SubRegion, string Region) EffectiveArea(int locationId, int? campusId = null, int? siteId = null) =>
        AreaOf(locationId, campusId, siteId) is { } a ? (a.SubRegionId is { } s ? _subs[s].Name : "", a.Region) : ("", "");

    /// <summary>True when any campus or site in the cluster has its own sub-region different from the cluster's.</summary>
    public bool IsSplit(int locationId)
    {
        var clusterSub = _locs.TryGetValue(locationId, out var l) ? l.SubRegionId : null;
        return _campuses.Values.Any(c => c.LocationId == locationId && c.SubRegionId != null && c.SubRegionId != clusterSub)
            || _sites.Values.Any(st => st.LocationId == locationId && st.SubRegionId != clusterSub);
    }

    /// <summary>True when any site in the campus has its own sub-region different from the campus's area.</summary>
    public bool IsCampusSplit(int locationId, int campusId)
    {
        var campusSub = AreaOf(locationId, campusId)?.SubRegionId;
        return _sites.Values.Any(st => st.CampusId == campusId && st.SubRegionId != campusSub);
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
