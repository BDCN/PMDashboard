using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

// ── Org chart nodes (read-only display) ──

public class OrgRegionNode
{
    public string Region    { get; init; } = "";
    public string Directors { get; init; } = "";
    public List<OrgSubRegionNode> SubRegions { get; init; } = new();
    public List<OrgPmNode>        PmsWithoutTeam { get; init; } = new();
    public bool HasPmsWithoutTeam => PmsWithoutTeam.Count > 0;
}

public class OrgSubRegionNode
{
    public string Name     { get; init; } = "";
    public string Pes      { get; init; } = "";
    public string Clusters { get; init; } = "";
    public string Campuses { get; init; } = "";   // campuses split off into this sub-region from other clusters
    public bool   HasCampuses => Campuses != "";
    public List<OrgPmNode> Pms { get; init; } = new();
}

public class OrgPmNode
{
    public string Name      { get; init; } = "";
    public string Pas       { get; init; } = "";
    public int    SiteCount { get; init; }
    public string Detail    => (SiteCount > 0 ? $"{SiteCount} site(s)" : "no sites") + (Pas != "" ? $" · PA: {Pas}" : "");
}

// ── People grid ──

public class PersonRow
{
    public string      Role      { get; init; } = "";
    public int         RoleOrder { get; init; }
    public string      Name      { get; init; } = "";
    public string      Email     { get; init; } = "";
    public string      Phone     { get; init; } = "";
    public string      Region    { get; init; } = "";
    public string      SubRegion { get; init; } = "";
    public string      ReportsTo { get; init; } = "";
    public bool        IsActive  { get; init; }
    public TeamMember? Member    { get; init; }   // Director / PE / PA
    public PM?         Pm        { get; init; }   // Platform Manager
    public bool        IsPm      => Pm != null;
    public string      ActiveText => IsActive ? "Yes" : "No";
}

// ── Sub-regions & clusters ──

public class SubRegionRow
{
    // Clusters counts whole clusters mapped here; Campuses counts campuses split off into it from other clusters.
    public SubRegion Sub      { get; init; } = null!;
    public string    Name     => Sub.Name;
    public string    Region   => Sub.Region;
    public string    Pes      { get; init; } = "";
    public int       Clusters { get; init; }
    public int       Campuses { get; init; }
    public int       Pms      { get; init; }
}

/// <summary>A sub-region choice for a cluster; Id 0 = none (unmapped).</summary>
public record SubRegionOption(int Id, string Name);

public partial class ClusterRow : ObservableObject
{
    public Location Location { get; init; } = null!;
    public string   Name     => Location.Name;
    public string   Region   => Location.GeographicalLocation;
    public string   Pms      { get; init; } = "";
    public int      OutOfTeam { get; init; }
    /// <summary>"2 campuses: Central 1" when some campuses have their own sub-region, else "".</summary>
    public string   SplitInfo { get; init; } = "";

    [ObservableProperty] private int _subRegionKey;   // 0 = none

    /// <summary>Set by the VM once the row is built, so the initial value doesn't count as an edit.</summary>
    public Action<ClusterRow>? Changed { get; set; }
    partial void OnSubRegionKeyChanged(int value) => Changed?.Invoke(this);
}

/// <summary>A campus in the selected cluster; SubRegionKey 0 = same as the cluster.</summary>
public partial class CampusTeamRow : ObservableObject
{
    public Campus Campus    { get; init; } = null!;
    public string Name      => Campus.Name;
    public int    Sites     { get; init; }
    public string Pm        { get; init; } = "";
    public string Effective { get; init; } = "";   // what actually governs it, e.g. "East 1 (cluster)"
    public int    OutOfTeam { get; init; }

    [ObservableProperty] private int _subRegionKey;

    public Action<CampusTeamRow>? Changed { get; set; }
    partial void OnSubRegionKeyChanged(int value) => Changed?.Invoke(this);
}

public class ConflictRow
{
    public string Cluster   { get; init; } = "";
    public string SubRegion { get; init; } = "";
    public string What      { get; init; } = "";
    public string Pm        { get; init; } = "";
    public string Reason    { get; init; } = "";
}

/// <summary>
/// Teams page: Director › PE (sub-region) › PM › PA, which cluster is in which sub-region, and any
/// existing assignments that break the team boundary (TeamRules). Everyone can view; only Super
/// Admins and Director accounts can change it, since it restricts every assignment in the app.
/// </summary>
public partial class TeamsViewModel : ObservableObject
{
    public static readonly string[] DefaultRegions = ["East", "Central", "West"];

    [ObservableProperty] private ObservableCollection<OrgRegionNode>   _orgChart   = new();
    [ObservableProperty] private ObservableCollection<PersonRow>       _people     = new();
    [ObservableProperty] private ObservableCollection<PersonRow>       _filteredPeople = new();
    [ObservableProperty] private ObservableCollection<SubRegionRow>    _subRegions = new();
    [ObservableProperty] private ObservableCollection<ClusterRow>      _clusters   = new();
    [ObservableProperty] private ObservableCollection<ClusterRow>      _filteredClusters = new();
    [ObservableProperty] private ObservableCollection<ConflictRow>     _conflicts  = new();
    [ObservableProperty] private ClusterRow?                           _selectedCluster;
    [ObservableProperty] private ObservableCollection<CampusTeamRow>   _campusRows = new();
    [ObservableProperty] private List<SubRegionOption>                 _campusSubRegionOptions = new();
    [ObservableProperty] private List<SubRegionOption>                 _subRegionOptions = new();
    [ObservableProperty] private List<string>                          _regions = DefaultRegions.ToList();

    [ObservableProperty] private string _peopleRoleFilter = "All";
    [ObservableProperty] private string _peopleSearch     = "";
    [ObservableProperty] private bool   _showInactive;
    [ObservableProperty] private string _clusterRegionFilter = "All";
    [ObservableProperty] private string _clusterSearch       = "";
    [ObservableProperty] private bool   _unmappedOnly;
    [ObservableProperty] private string _statusMessage       = "";

    public static readonly string[] RoleFilters = ["All", TeamRoles.Director, TeamRoles.Pe, TeamRoles.Pm, TeamRoles.Pa];
    public List<string> ClusterRegionFilters => new[] { "All" }.Concat(Regions).ToList();

    /// <summary>Super Admins and Director accounts (the same people who manage users) can change teams.</summary>
    public bool   IsEditable    => CurrentUser.IsSuperAdmin || CurrentUser.IsDirector;
    public string ConflictsTab  => Conflicts.Count == 0 ? "Conflicts" : $"Conflicts ({Conflicts.Count})";
    public int    UnmappedCount => Clusters.Count(c => c.SubRegionKey == 0);

    partial void OnPeopleRoleFilterChanged(string value) => ApplyPeopleFilter();
    partial void OnPeopleSearchChanged(string value)     => ApplyPeopleFilter();
    partial void OnShowInactiveChanged(bool value)       => ApplyPeopleFilter();
    partial void OnClusterRegionFilterChanged(string value) => ApplyClusterFilter();
    partial void OnClusterSearchChanged(string value)       => ApplyClusterFilter();
    partial void OnUnmappedOnlyChanged(bool value)          => ApplyClusterFilter();
    partial void OnConflictsChanged(ObservableCollection<ConflictRow> value) => OnPropertyChanged(nameof(ConflictsTab));
    partial void OnRegionsChanged(List<string> value) => OnPropertyChanged(nameof(ClusterRegionFilters));
    partial void OnSelectedClusterChanged(ClusterRow? value) => LoadCampusRows();

    public bool HasSelectedCluster => SelectedCluster != null;

    // ── Load ──

    public void Load()
    {
        using var db = new AppDbContext();
        var subs    = db.SubRegions.AsNoTracking().OrderBy(s => s.Region).ThenBy(s => s.SortOrder).ThenBy(s => s.Name).ToList();
        var members = db.TeamMembers.AsNoTracking().OrderBy(m => m.LastName).ThenBy(m => m.FirstName).ToList();
        var pms     = db.PMs.AsNoTracking().OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ToList();
        var locs    = db.Locations.AsNoTracking().OrderBy(l => l.Name).ToList();
        var sites   = db.Sites.AsNoTracking().Where(s => s.AssignedPmId != null)
                        .Select(s => new { s.Id, s.Name, s.LocationId, s.CampusId, PmId = s.AssignedPmId!.Value, s.IsClosed }).ToList();
        var romps   = db.Romps.AsNoTracking().Where(r => r.AssignedPmId != null)
                        .Select(r => new { r.Name, SiteName = r.Site!.Name, r.Site.LocationId, r.Site.CampusId, PmId = r.AssignedPmId!.Value }).ToList();
        var splitCampuses = db.Campuses.AsNoTracking().Where(c => c.SubRegionId != null)
                        .Select(c => new { c.Id, c.Name, c.LocationId, Sub = c.SubRegionId!.Value }).ToList();
        var rules   = TeamRules.For(db);

        var subById = subs.ToDictionary(s => s.Id);
        var pmById  = pms.ToDictionary(p => p.Id);
        var pes     = members.Where(m => m.Role == TeamRoles.Pe).ToDictionary(m => m.Id);
        string SubName(int? id) => id is { } i && subById.TryGetValue(i, out var s) ? s.Name : "";

        Regions = DefaultRegions
            .Concat(members.Where(m => m.Role == TeamRoles.Director).Select(m => m.Region))
            .Concat(subs.Select(s => s.Region))
            .Concat(pms.Select(p => p.GeographicalLocation))
            .Concat(locs.Select(l => l.GeographicalLocation))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => Array.IndexOf(DefaultRegions, r) is var i && i >= 0 ? i : 99).ThenBy(r => r)
            .ToList();

        // ── People ──
        var people = new List<PersonRow>();
        foreach (var m in members)
        {
            var sub = m.Role == TeamRoles.Pe ? SubName(m.SubRegionId) : "";
            var region = m.Role switch
            {
                TeamRoles.Director => m.Region,
                TeamRoles.Pe       => m.SubRegionId is { } s && subById.TryGetValue(s, out var sr) ? sr.Region : "",
                _                  => m.PmId is { } p ? rules.PmTeam(p).Region : "",
            };
            var reportsTo = m.Role switch
            {
                TeamRoles.Pe => string.Join(", ", members.Where(d => d.Role == TeamRoles.Director && d.IsActive &&
                                    string.Equals(d.Region, region, StringComparison.OrdinalIgnoreCase)).Select(d => d.FullName)),
                TeamRoles.Pa => m.PmId is { } p && pmById.TryGetValue(p, out var pm) ? pm.FullName : "",
                _            => "",
            };
            if (m.Role == TeamRoles.Pa && m.PmId is { } paPm) sub = rules.PmTeam(paPm).SubRegion;
            people.Add(new PersonRow
            {
                Role = m.Role, RoleOrder = RoleOrder(m.Role), Name = m.FullName, Email = m.Email, Phone = m.Phone,
                Region = region, SubRegion = sub, ReportsTo = reportsTo, IsActive = m.IsActive, Member = m,
            });
        }
        foreach (var p in pms)
        {
            var (sub, region) = rules.PmTeam(p.Id);
            var allSubs = rules.PmSubRegions(p.Id);
            var defaultSubId = subs.FirstOrDefault(s => s.Name == sub)?.Id;
            people.Add(new PersonRow
            {
                Role = TeamRoles.Pm, RoleOrder = RoleOrder(TeamRoles.Pm), Name = p.FullName,
                Region = region,
                SubRegion = allSubs.Count <= 1 ? sub : $"{sub} (+{string.Join(", ", allSubs.Skip(1))})",
                // Reports to the PE(s) of their default sub-region.
                ReportsTo = defaultSubId is { } ds
                    ? string.Join(", ", members.Where(m => m.Role == TeamRoles.Pe && m.IsActive && m.SubRegionId == ds).Select(m => m.FullName))
                    : "",
                IsActive = p.IsActive, Pm = p,
            });
        }
        People = new(people.OrderBy(r => r.RoleOrder).ThenBy(r => r.Region).ThenBy(r => r.SubRegion).ThenBy(r => r.Name));
        ApplyPeopleFilter();

        // ── Sub-regions ──
        var clustersBySub = locs.Where(l => l.SubRegionId != null).GroupBy(l => l.SubRegionId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        SubRegions = new(subs.Select(s => new SubRegionRow
        {
            Sub      = s,
            Pes      = string.Join(", ", members.Where(m => m.Role == TeamRoles.Pe && m.IsActive && m.SubRegionId == s.Id).Select(m => m.FullName)),
            Clusters = clustersBySub.GetValueOrDefault(s.Id)?.Count ?? 0,
            Campuses = splitCampuses.Count(c => c.Sub == s.Id && locs.FirstOrDefault(l => l.Id == c.LocationId)?.SubRegionId != s.Id),
            Pms      = pms.Count(p => p.IsActive && rules.PmOn(p.Id, s.Name)),
        }));
        SubRegionOptions = new[] { new SubRegionOption(0, "— none —") }
            .Concat(subs.Select(s => new SubRegionOption(s.Id, $"{s.Name}  ({s.Region})"))).ToList();

        // ── Clusters ──
        var pmNamesByLoc = sites.GroupBy(s => s.LocationId).ToDictionary(g => g.Key,
            g => g.Select(s => s.PmId).Distinct().Select(id => pmById.TryGetValue(id, out var p) ? p.FullName : "?").OrderBy(n => n).ToList());
        var conflicts = new List<ConflictRow>();
        foreach (var s in sites)
            if (rules.Reason(s.PmId, s.LocationId, s.CampusId) is { } why)
                conflicts.Add(new ConflictRow
                {
                    Cluster = locs.FirstOrDefault(l => l.Id == s.LocationId)?.Name ?? "?",
                    SubRegion = rules.EffectiveArea(s.LocationId, s.CampusId).SubRegion,
                    What = $"Site {s.Name}" + (s.IsClosed ? " (closed)" : ""),
                    Pm = pmById.TryGetValue(s.PmId, out var p) ? p.FullName : "?", Reason = why,
                });
        foreach (var r in romps)
            if (rules.Reason(r.PmId, r.LocationId, r.CampusId) is { } why)
                conflicts.Add(new ConflictRow
                {
                    Cluster = locs.FirstOrDefault(l => l.Id == r.LocationId)?.Name ?? "?",
                    SubRegion = rules.EffectiveArea(r.LocationId, r.CampusId).SubRegion,
                    What = $"ROMP {r.SiteName} / {r.Name}",
                    Pm = pmById.TryGetValue(r.PmId, out var p) ? p.FullName : "?", Reason = why,
                });
        foreach (var l in locs.Where(l => l.AssignedPmId != null))
            if (rules.Reason(l.AssignedPmId!.Value, l.Id) is { } why)
                conflicts.Add(new ConflictRow
                {
                    Cluster = l.Name, SubRegion = rules.ClusterSubRegion(l.Id), What = "Cluster (direct PM)",
                    Pm = pmById.TryGetValue(l.AssignedPmId.Value, out var p) ? p.FullName : "?", Reason = why,
                });
        Conflicts = new(conflicts.OrderBy(c => c.Cluster).ThenBy(c => c.What));
        var outByLoc = conflicts.GroupBy(c => c.Cluster).ToDictionary(g => g.Key, g => g.Count());

        var clusterRows = locs.Select(l => new ClusterRow
        {
            Location     = l,
            SubRegionKey = l.SubRegionId is { } sid && subById.ContainsKey(sid) ? sid : 0,
            Pms          = pmNamesByLoc.TryGetValue(l.Id, out var names)
                               ? (names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} +{names.Count - 3}")
                               : (l.AssignedPmId is { } d && pmById.TryGetValue(d, out var dp) ? dp.FullName : ""),
            OutOfTeam    = outByLoc.GetValueOrDefault(l.Name),
            SplitInfo    = splitCampuses.Where(c => c.LocationId == l.Id && c.Sub != l.SubRegionId).ToList() is { Count: > 0 } own
                               ? $"{own.Count} campus(es): {string.Join(", ", own.Select(c => subById[c.Sub].Name).Distinct())}"
                               : "",
        }).ToList();
        foreach (var row in clusterRows) row.Changed = OnClusterSubRegionEdited;
        var prevClusterId = SelectedCluster?.Location.Id;
        Clusters = new(clusterRows);
        ApplyClusterFilter();
        CampusSubRegionOptions = new[] { new SubRegionOption(0, "— same as cluster —") }
            .Concat(subs.Select(s => new SubRegionOption(s.Id, $"{s.Name}  ({s.Region})"))).ToList();
        SelectedCluster = prevClusterId is { } pc ? FilteredClusters.FirstOrDefault(c => c.Location.Id == pc) : null;
        LoadCampusRows();
        OnPropertyChanged(nameof(UnmappedCount));

        // ── Org chart ──
        var activePms = pms.Where(p => p.IsActive).ToList();
        var siteCountByPm = sites.Where(s => !s.IsClosed).GroupBy(s => s.PmId).ToDictionary(g => g.Key, g => g.Count());
        OrgPmNode PmNode(PM p) => new()
        {
            Name = p.FullName,
            Pas  = string.Join(", ", members.Where(m => m.Role == TeamRoles.Pa && m.IsActive && m.PmId == p.Id).Select(m => m.FullName)),
            SiteCount = siteCountByPm.GetValueOrDefault(p.Id),
        };
        OrgChart = new(Regions.Select(region => new OrgRegionNode
        {
            Region    = region,
            Directors = string.Join(", ", members.Where(m => m.Role == TeamRoles.Director && m.IsActive &&
                            string.Equals(m.Region, region, StringComparison.OrdinalIgnoreCase)).Select(m => m.FullName)),
            SubRegions = subs.Where(s => string.Equals(s.Region, region, StringComparison.OrdinalIgnoreCase)).Select(s => new OrgSubRegionNode
            {
                Name     = s.Name,
                Pes      = string.Join(", ", members.Where(m => m.Role == TeamRoles.Pe && m.IsActive && m.SubRegionId == s.Id).Select(m => m.FullName)),
                Clusters = clustersBySub.TryGetValue(s.Id, out var cl) ? string.Join(", ", cl.Select(c => c.Name)) : "",
                Campuses = string.Join(", ", splitCampuses
                    .Where(c => c.Sub == s.Id && locs.FirstOrDefault(l => l.Id == c.LocationId)?.SubRegionId != s.Id)
                    .Select(c => $"{c.Name} ({locs.FirstOrDefault(l => l.Id == c.LocationId)?.Name})")),
                Pms      = activePms.Where(p => rules.PmOn(p.Id, s.Name)).Select(PmNode).ToList(),
            }).ToList(),
            PmsWithoutTeam = activePms.Where(p => rules.PmTeam(p.Id).SubRegion == "" &&
                                 string.Equals(p.GeographicalLocation, region, StringComparison.OrdinalIgnoreCase)).Select(PmNode).ToList(),
        }).Where(n => n.Directors != "" || n.SubRegions.Count > 0 || n.PmsWithoutTeam.Count > 0));
    }

    private static int RoleOrder(string role) => role switch
    {
        TeamRoles.Director => 0, TeamRoles.Pe => 1, TeamRoles.Pm => 2, TeamRoles.Pa => 3, _ => 4,
    };

    private void ApplyPeopleFilter()
    {
        var q = People.AsEnumerable();
        if (PeopleRoleFilter != "All") q = q.Where(p => p.Role == PeopleRoleFilter);
        if (!ShowInactive) q = q.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(PeopleSearch))
        {
            var t = PeopleSearch.Trim();
            q = q.Where(p => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Email.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || p.SubRegion.Contains(t, StringComparison.OrdinalIgnoreCase) || p.ReportsTo.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        FilteredPeople = new(q);
    }

    private void ApplyClusterFilter()
    {
        var q = Clusters.AsEnumerable();
        if (ClusterRegionFilter != "All") q = q.Where(c => string.Equals(c.Region, ClusterRegionFilter, StringComparison.OrdinalIgnoreCase));
        if (UnmappedOnly) q = q.Where(c => c.SubRegionKey == 0);
        if (!string.IsNullOrWhiteSpace(ClusterSearch))
        {
            var t = ClusterSearch.Trim();
            q = q.Where(c => c.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || c.Pms.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        FilteredClusters = new(q);
    }

    // ── People actions ──

    public List<PM> ActivePms()
    {
        using var db = new AppDbContext();
        return db.PMs.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ToList();
    }

    public List<SubRegion> AllSubRegions()
    {
        using var db = new AppDbContext();
        return db.SubRegions.AsNoTracking().OrderBy(s => s.Region).ThenBy(s => s.SortOrder).ThenBy(s => s.Name).ToList();
    }

    public void SaveMember(TeamMember m)
    {
        if (!IsEditable) return;
        using var db = new AppDbContext();
        bool isNew = m.Id == 0;
        if (isNew) db.TeamMembers.Add(m);
        else
        {
            var tracked = db.TeamMembers.Find(m.Id);
            if (tracked == null) return;
            db.Entry(tracked).CurrentValues.SetValues(m);
        }
        db.SaveChanges();
        AuditService.Log(isNew ? "Create" : "Update", "TeamMember", m.Id, $"{m.Role} {m.FullName}" + (isNew ? " added" : " updated"));
        StatusMessage = $"{m.Role} {m.FullName} {(isNew ? "added" : "saved")}." + ConflictNote(db);
        Load();
    }

    public void SetMemberActive(TeamMember m, bool active)
    {
        if (!IsEditable) return;
        using var db = new AppDbContext();
        var tracked = db.TeamMembers.Find(m.Id);
        if (tracked == null) return;
        tracked.IsActive = active;
        db.SaveChanges();
        AuditService.Log(active ? "Activate" : "Deactivate", "TeamMember", m.Id, $"{m.Role} {m.FullName}");
        StatusMessage = $"{m.FullName} {(active ? "reactivated" : "deactivated")}.";
        Load();
    }

    /// <summary>Deletes a Director/PE/PA. PMs keep their sub-regions (their PE link is cleared).</summary>
    public void DeleteMember(TeamMember m)
    {
        if (!IsEditable) return;
        using var db = new AppDbContext();
        var tracked = db.TeamMembers.Find(m.Id);
        if (tracked == null) return;
        foreach (var pm in db.PMs.Where(p => p.PeId == m.Id).ToList()) pm.PeId = null; // explicit, so the change syncs
        db.TeamMembers.Remove(tracked);
        db.SaveChanges();
        AuditService.Log("Delete", "TeamMember", m.Id, $"{m.Role} {m.FullName} deleted");
        StatusMessage = $"{m.Role} {m.FullName} deleted." + ConflictNote(db);
        Load();
    }

    /// <summary>Sets a PM's default sub-region and the others they cover (PmTeamService).</summary>
    public void SetPmSubRegions(PM pm, int? defaultId, IEnumerable<int> others)
    {
        if (!IsEditable) return;
        using var db = new AppDbContext();
        var tracked = db.PMs.Find(pm.Id);
        if (tracked == null) return;
        PmTeamService.SetSubRegions(db, tracked, defaultId, others);
        db.SaveChanges();
        var text = TeamText(db, tracked);
        AuditService.Log("Update", "PM", pm.Id, $"{pm.FullName} sub-regions: {text}");
        StatusMessage = $"{pm.FullName}: {text}." + ConflictNote(db, pm.Id);
        Load();
    }

    /// <summary>Makes one sub-region the default for several PMs at once (their old default is
    /// dropped, other sub-regions kept); null = take them off every team.</summary>
    public void SetPmsDefaultSubRegion(IEnumerable<PM> pms, int? subRegionId)
    {
        if (!IsEditable) return;
        var ids = pms.Select(p => p.Id).Distinct().ToList();
        if (ids.Count == 0) return;
        using var db = new AppDbContext();
        var tracked = db.PMs.Where(p => ids.Contains(p.Id)).ToList();
        foreach (var p in tracked)
            PmTeamService.SetSubRegions(db, p, subRegionId, subRegionId == null ? Array.Empty<int>() : p.OtherSubRegionIdList);
        db.SaveChanges();
        var subName = subRegionId is { } sid ? db.SubRegions.Find(sid)?.Name ?? "?" : "no team";
        AuditService.Log("Update", "PM", ids.Count == 1 ? ids[0] : null,
            $"{string.Join(", ", tracked.Select(p => p.FullName))} default sub-region → {subName}");
        StatusMessage = $"{tracked.Count} PM(s) now on {subName}" + (subRegionId == null ? "." : " (default).") + ConflictNote(db);
        Load();
    }

    private static string TeamText(AppDbContext db, PM pm)
    {
        var names = db.SubRegions.AsNoTracking().ToDictionary(s => s.Id, s => s.Name);
        var t = PmTeamService.Describe(pm.SubRegionId, pm.OtherSubRegionIdList, names);
        return t == "" ? "no team" : t;
    }

    // ── Sub-region actions ──

    public void SaveSubRegion(SubRegion s)
    {
        if (!IsEditable) return;
        using var db = new AppDbContext();
        bool isNew = s.Id == 0;
        if (isNew)
        {
            s.SortOrder = (db.SubRegions.Where(x => x.Region == s.Region).Max(x => (int?)x.SortOrder) ?? 0) + 1;
            db.SubRegions.Add(s);
        }
        else
        {
            var tracked = db.SubRegions.Find(s.Id);
            if (tracked == null) return;
            tracked.Name = s.Name; tracked.Region = s.Region;
        }
        db.SaveChanges();
        AuditService.Log(isNew ? "Create" : "Update", "SubRegion", s.Id, $"Sub-region {s.Name} ({s.Region})");
        StatusMessage = $"Sub-region {s.Name} {(isNew ? "added" : "saved")}." + ConflictNote(db);
        Load();
    }

    /// <summary>Deletes a sub-region; its clusters become unmapped and its PEs lose it.</summary>
    public void DeleteSubRegion(SubRegion s)
    {
        if (!IsEditable) return;
        using var db = new AppDbContext();
        var tracked = db.SubRegions.Find(s.Id);
        if (tracked == null) return;
        foreach (var l in db.Locations.Where(l => l.SubRegionId == s.Id).ToList()) l.SubRegionId = null;
        foreach (var t in db.TeamMembers.Where(t => t.SubRegionId == s.Id).ToList()) t.SubRegionId = null;
        foreach (var c in db.Campuses.Where(c => c.SubRegionId == s.Id).ToList()) c.SubRegionId = null;
        db.SubRegions.Remove(tracked);
        db.SaveChanges();
        AuditService.Log("Delete", "SubRegion", s.Id, $"Sub-region {s.Name} deleted");
        StatusMessage = $"Sub-region {s.Name} deleted — its clusters are unmapped now." + ConflictNote(db);
        Load();
    }

    private void OnClusterSubRegionEdited(ClusterRow row)
    {
        // Deferred: this runs inside the cell ComboBox's binding update, and saving reloads (replaces)
        // the grid's rows — let the edit finish first.
        void Apply()
        {
            if (!IsEditable) { StatusMessage = "Only Super Admins and Directors can change teams."; Load(); return; }
            SetClusterSubRegion(new[] { row.Location.Id }, row.SubRegionKey == 0 ? null : row.SubRegionKey);
        }
        if (System.Windows.Application.Current?.Dispatcher is { } d) d.InvokeAsync(Apply);
        else Apply();
    }

    /// <summary>Maps clusters to a sub-region (null = unmapped). Existing assignments are kept;
    /// any that now fall outside their team show on the Conflicts tab.</summary>
    public void SetClusterSubRegion(IEnumerable<int> locationIds, int? subRegionId)
    {
        if (!IsEditable) return;
        var ids = locationIds.ToList();
        if (ids.Count == 0) return;
        using var db = new AppDbContext();
        var locs = db.Locations.Where(l => ids.Contains(l.Id)).ToList();
        foreach (var l in locs) l.SubRegionId = subRegionId;
        db.SaveChanges();
        var subName = subRegionId is { } sid ? db.SubRegions.Find(sid)?.Name ?? "?" : "no sub-region";
        AuditService.Log("Update", "Location", ids.Count == 1 ? ids[0] : null,
            $"{string.Join(", ", locs.Select(l => l.Name))} → {subName}");
        StatusMessage = $"{(locs.Count == 1 ? $"Cluster {locs[0].Name}" : $"{locs.Count} clusters")} set to {subName}." + ConflictNote(db);
        Load();
    }

    // ── Campus-level sub-regions (split clusters) ──

    private void LoadCampusRows()
    {
        OnPropertyChanged(nameof(HasSelectedCluster));
        if (SelectedCluster is not { } cl) { CampusRows = new(); return; }
        using var db = new AppDbContext();
        var rules = TeamRules.For(db);
        var campuses = db.Campuses.AsNoTracking().Include(c => c.AssignedPm).Include(c => c.Sites)
                         .Where(c => c.LocationId == cl.Location.Id).OrderBy(c => c.Name).ToList();
        var rows = campuses.Select(c =>
        {
            var (sub, region) = rules.EffectiveArea(c.LocationId, c.Id);
            return new CampusTeamRow
            {
                Campus       = c,
                Sites        = c.Sites.Count,
                Pm           = c.PmDisplay,
                SubRegionKey = c.SubRegionId ?? 0,
                Effective    = sub != "" ? (c.SubRegionId != null ? sub : $"{sub} (cluster)")
                             : region != "" ? $"{region} (no sub-region)" : "—",
                OutOfTeam    = c.Sites.Count(s => s.AssignedPmId is { } pm && !rules.Allows(pm, c.LocationId, c.Id)),
            };
        }).ToList();
        foreach (var r in rows) r.Changed = OnCampusSubRegionEdited;
        CampusRows = new(rows);
    }

    private void OnCampusSubRegionEdited(CampusTeamRow row)
    {
        void Apply()
        {
            if (!IsEditable) { StatusMessage = "Only Super Admins and Directors can change teams."; LoadCampusRows(); return; }
            SetCampusSubRegion(new[] { row.Campus.Id }, row.SubRegionKey == 0 ? null : row.SubRegionKey);
        }
        if (System.Windows.Application.Current?.Dispatcher is { } d) d.InvokeAsync(Apply);
        else Apply();
    }

    /// <summary>Gives campuses their own sub-region (splitting the cluster), or null = follow the
    /// cluster again. Existing assignments are kept; any now outside the team show on Conflicts.</summary>
    public void SetCampusSubRegion(IEnumerable<int> campusIds, int? subRegionId)
    {
        if (!IsEditable) return;
        var ids = campusIds.ToList();
        if (ids.Count == 0) return;
        using var db = new AppDbContext();
        var campuses = db.Campuses.Where(c => ids.Contains(c.Id)).ToList();
        foreach (var c in campuses) c.SubRegionId = subRegionId;
        db.SaveChanges();
        var subName = subRegionId is { } sid ? db.SubRegions.Find(sid)?.Name ?? "?" : null;
        AuditService.Log("Update", "Campus", ids.Count == 1 ? ids[0] : null,
            $"{string.Join(", ", campuses.Select(c => c.Name))} → {subName ?? "same as cluster"}");
        var what = campuses.Count == 1 ? $"Campus {campuses[0].Name}" : $"{campuses.Count} campuses";
        StatusMessage = (subName != null ? $"{what} moved to {subName}." : $"{what} follow{(campuses.Count == 1 ? "s" : "")} the cluster's sub-region again.")
                        + ConflictNote(db);
        Load();
    }

    /// <summary>" 3 existing assignment(s) are now outside their team — see Conflicts." or "".</summary>
    private static string ConflictNote(AppDbContext db, int? pmId = null)
    {
        var rules = TeamRules.Current();
        int n = db.Sites.AsNoTracking().Where(s => s.AssignedPmId != null && (pmId == null || s.AssignedPmId == pmId))
                  .Select(s => new { Pm = s.AssignedPmId!.Value, s.LocationId, s.CampusId }).AsEnumerable()
                  .Count(s => !rules.Allows(s.Pm, s.LocationId, s.CampusId));
        return n == 0 ? "" : $"  {n} existing site assignment(s) are outside their team — see Conflicts.";
    }
}
