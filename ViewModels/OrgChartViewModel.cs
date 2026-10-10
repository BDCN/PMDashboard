using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

// ── Chart nodes (display-only; rebuilt on every load/search/edit) ──

public class OrgChartPa
{
    public int    Id      { get; init; }
    public string Name    { get; init; } = "";
    public bool   IsMatch { get; init; }
    public bool   IsChanged { get; init; }   // moved to another PM, not saved yet
    /// <summary>" · supports Sheila Betancourt" — a PA is managed by the PE and supports one PM.</summary>
    public string SupportsText { get; init; } = "";
    public bool   IsPlaceholder { get; init; }   // TBD — PA still to be hired
    public bool   IsTeamOnSeveral { get; init; } // their PM covers several sub-regions (green)
}

public class OrgChartPm
{
    public int              PmId        { get; init; }
    public string           Name        { get; init; } = "";
    public string           Detail      { get; init; } = "";   // "12 sites · Remote"
    public List<OrgChartPa> Pas         { get; init; } = new();
    public bool             HasPas      => Pas.Count > 0;
    public bool             IsPlaceholder { get; init; }      // TBD — PM still to be hired
    public bool             HasSeveralSubRegions { get; init; } // on more than one sub-region (green gradient)
    public bool             HasSeveralPas => Pas.Count > 1;

    /// <summary>Campuses the PM currently covers (has assigned, open sites in).</summary>
    public List<string>     Campuses    { get; init; } = new();
    public bool             HasCampuses => Campuses.Count > 0;
    public string           CampusHeader => Campuses.Count == 1 ? "Campus: " : $"Campuses ({Campuses.Count}): ";
    public string           CampusText  => Campuses.Count <= 6 ? string.Join(", ", Campuses)
                                         : $"{string.Join(", ", Campuses.Take(6))} +{Campuses.Count - 6} more";
    public string?          CampusTip   => Campuses.Count > 6 ? string.Join(", ", Campuses) : null;
    public bool             IsLast      { get; init; }         // draws the end of the bracket line
    public bool             IsMatch     { get; init; }         // search highlight
    public int?             SubRegionId { get; init; }         // the column this card sits in (null = No team)
    public bool             IsDefault   { get; init; }         // this column is the PM's default sub-region
    public bool             IsSecondary => SubRegionId != null && !IsDefault;   // faded card
    public bool             InSubRegion => SubRegionId != null;
    public string           TagText     { get; init; } = "";   // "also East2, East3" / "default: East1"
    public bool             HasTag      => TagText != "";
    public bool             IsChanged   { get; init; }         // edited, not saved yet
}

/// <summary>One column under a region: a sub-region with its PE(s) and PMs, or the region's
/// "No team yet" column (PMs not on any sub-region).</summary>
public class OrgChartColumn
{
    public int?              SubRegionId { get; init; }
    public string            Title    { get; init; } = "";   // "East1" / "No team yet"
    public List<string>      Pes      { get; init; } = new();
    public string            PeText   => Pes.Count > 0 ? string.Join(", ", Pes) : "No PE yet";
    public bool              HasPe    => Pes.Count > 0;
    public bool              PeMatch  { get; init; }
    public string            Clusters { get; init; } = "";
    public bool              HasClusters => Clusters != "";
    public List<OrgChartPm>  Pms      { get; init; } = new();
    public bool              HasPms   => Pms.Count > 0;
    public string            PmCountText => Pms.Count == 1 ? "1 PM" : $"{Pms.Count} PMs";
    // Under the PM count: how many of those are TBD, and the PAs (with their TBDs). PAs are counted
    // from every PM card in the column, PA chips hidden or not.
    public int               TbdPmCount  => Pms.Count(p => p.IsPlaceholder);
    public int               PaCount     { get; init; }
    public int               TbdPaCount  { get; init; }
    public string            TbdPmText   => $"{TbdPmCount} TBD PM{(TbdPmCount == 1 ? "" : "s")}";
    public string            PaCountText => $"{PaCount} PA{(PaCount == 1 ? "" : "s")} · {TbdPaCount} TBD";
    public bool              IsNoTeam { get; init; }
    public bool              CanAddTbd => !IsNoTeam && SubRegionId != null;
    public bool              IsFirst  { get; init; }
    public bool              IsLast   { get; init; }
}

public class OrgChartRegion
{
    public string               Region    { get; init; } = "";
    public List<string>         Directors { get; init; } = new();
    public string               DirectorText => Directors.Count > 0 ? string.Join(", ", Directors) : "No director yet";
    public bool                 HasDirector  => Directors.Count > 0;
    public bool                 DirectorMatch { get; init; }
    public List<OrgChartColumn> Columns   { get; init; } = new();
    public bool                 HasColumns => Columns.Count > 0;
    public string               Summary   { get; init; } = "";
}

/// <summary>
/// Org Chart page: Director (region) › PE (sub-region) › PM › PA drawn as a tree from the Teams
/// data. A PM (with their PAs) can be on several sub-regions — they appear in every one, full card
/// under the default and a faded card elsewhere.
///
/// Edit mode (Super Admins / Directors): drag a PM to another sub-region (plain drop = move there
/// and make it the default, keeping the others; Ctrl+drop = add it), drop on "No team yet" to
/// take them off every team, drag a PA onto another PM; ★/✕ on a card make default / remove.
/// Changes are kept in a working copy and only written on Save (PmTeamService for PMs,
/// TeamMember.PmId for PAs), which the Teams page then shows. Adding people stays on PM
/// Maintenance (PMs) and Teams (Directors/PEs/PAs).
/// </summary>
public partial class OrgChartViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<OrgChartRegion> _regions = new();
    [ObservableProperty] private List<string> _notes = new();
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private bool   _showClusters = true;
    [ObservableProperty] private bool   _showPas = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _matchInfo = "";
    [ObservableProperty] private bool   _isEditMode;
    [ObservableProperty] private string _changeSummary = "";
    [ObservableProperty] private string _statusMessage = "";

    public bool HasNotes => Notes.Count > 0;
    partial void OnNotesChanged(List<string> value) => OnPropertyChanged(nameof(HasNotes));
    public string ZoomText => $"{Zoom * 100:0}%";
    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));
    public bool CanEdit => CurrentUser.IsSuperAdmin || CurrentUser.IsDirector;
    public bool ShowEditButton => CanEdit && !IsEditMode;
    partial void OnIsEditModeChanged(bool value) { OnPropertyChanged(nameof(ShowEditButton)); Rebuild(); }

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnShowClustersChanged(bool value) => Rebuild();
    partial void OnShowPasChanged(bool value)      => Rebuild();

    public Action<int>? OpenPm    { get; set; }
    public Action?      OpenTeams { get; set; }

    private string _geoFilter = "All";
    public void ApplyGeoFilter(string geo) { _geoFilter = geo; Rebuild(); }

    // ── Snapshot ──
    private List<SubRegion>  _subs     = new();
    private List<TeamMember> _members  = new();
    private List<PM>         _pms      = new();
    private Dictionary<int, int>          _siteCounts    = new();
    private Dictionary<int, List<string>> _campusesByPm  = new();
    private Dictionary<int, List<string>> _clustersBySub = new();

    // ── Working copy (edit mode) vs. what's saved ──
    private sealed record Team(int? Default, IReadOnlyList<int> Others)
    {
        public IEnumerable<int> All => Default is { } d ? Others.Prepend(d) : Others;
        public bool Same(Team o) => Default == o.Default && Others.OrderBy(x => x).SequenceEqual(o.Others.OrderBy(x => x));
    }
    private Dictionary<int, Team> _savedTeams = new(), _teams = new();
    private Dictionary<int, int?> _savedPaPm = new(), _paPm = new();

    public bool HasChanges => ChangedPmIds().Any() || ChangedPaIds().Any();
    private IEnumerable<int> ChangedPmIds() => _teams.Where(kv => !_savedTeams[kv.Key].Same(kv.Value)).Select(kv => kv.Key);
    private IEnumerable<int> ChangedPaIds() => _paPm.Where(kv => _savedPaPm[kv.Key] != kv.Value).Select(kv => kv.Key);

    public void Load()
    {
        using var db = new AppDbContext();
        _subs    = db.SubRegions.AsNoTracking().OrderBy(s => s.Name).ToList();
        _members = db.TeamMembers.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.LastName).ThenBy(m => m.FirstName).ToList();
        _pms     = db.PMs.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ToList();
        _siteCounts = db.Sites.AsNoTracking().Where(s => s.AssignedPmId != null && !s.IsClosed)
                        .GroupBy(s => s.AssignedPmId!.Value).Select(g => new { g.Key, N = g.Count() })
                        .ToDictionary(x => x.Key, x => x.N);

        // Campuses each PM currently covers: the campus of every open site assigned to them.
        var campusNames = db.Campuses.AsNoTracking().ToDictionary(c => c.Id, c => c.Name);
        _campusesByPm = db.Sites.AsNoTracking().Where(s => s.AssignedPmId != null && !s.IsClosed)
                          .Select(s => new { Pm = s.AssignedPmId!.Value, s.CampusId, s.Name }).AsEnumerable()
                          .GroupBy(s => s.Pm)
                          .ToDictionary(g => g.Key, g => g.Select(s => s.CampusId is { } c && campusNames.TryGetValue(c, out var n) ? n : s.Name)
                                                          .Distinct().OrderBy(n => n).ToList());

        var locs = db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name, l.SubRegionId }).ToList();
        var splitSites = db.Sites.AsNoTracking().Where(st => st.SubRegionId != null)
                           .Select(st => new { st.Name, Sub = st.SubRegionId!.Value }).ToList();
        var split = db.Campuses.AsNoTracking().Where(c => c.SubRegionId != null)
                      .Select(c => new { c.Name, c.LocationId, Sub = c.SubRegionId!.Value }).ToList();
        _clustersBySub = _subs.ToDictionary(s => s.Id, s =>
            locs.Where(l => l.SubRegionId == s.Id).Select(l => l.Name)
                .Concat(split.Where(c => c.Sub == s.Id && locs.FirstOrDefault(l => l.Id == c.LocationId)?.SubRegionId != s.Id)
                             .Select(c => c.Name))
                .Concat(splitSites.Where(x => x.Sub == s.Id).Select(x => x.Name))
                .OrderBy(n => n).ToList());

        // Teams as the rules see them (handles older rows whose default is only known via the PE).
        var rules  = TeamRules.For(db);
        var idByName = _subs.ToDictionary(s => s.Name, s => s.Id);
        _savedTeams = _pms.ToDictionary(p => p.Id, p =>
        {
            var names = rules.PmSubRegions(p.Id);
            var ids = names.Where(idByName.ContainsKey).Select(n => idByName[n]).ToList();
            return new Team(ids.Count > 0 ? ids[0] : null, ids.Skip(1).ToList());
        });
        _teams     = new(_savedTeams);
        _savedPaPm = _members.Where(m => m.Role == TeamRoles.Pa).ToDictionary(m => m.Id, m => m.PmId);
        _paPm      = new(_savedPaPm);
        StatusMessage = "";
        Rebuild();
    }

    /// <summary>Background sync refresh — skipped while there are unsaved edits (they'd be lost).</summary>
    public void RefreshFromSync()
    {
        if (IsEditMode && HasChanges) return;
        Load();
    }

    // ── Edit operations (working copy only) ──

    public void BeginEdit() { if (CanEdit) { IsEditMode = true; StatusMessage = ""; } }

    public void CancelEdit()
    {
        IsEditMode = false;
        Load();
    }

    /// <summary>Drag a PM card from one column to another. Plain = move (target becomes the
    /// default, other sub-regions kept); add = Ctrl+drop (target added as another sub-region);
    /// toSub null = "No team yet" (off every team).</summary>
    public void MovePm(int pmId, int? fromSub, int? toSub, bool add)
    {
        if (!IsEditMode || !_teams.TryGetValue(pmId, out var t)) return;
        if (toSub == null) { _teams[pmId] = new Team(null, Array.Empty<int>()); Rebuild(); return; }
        if (fromSub == toSub && !add) return;

        var all = t.All.ToList();
        if (add)
        {
            if (all.Contains(toSub.Value)) return;
            _teams[pmId] = t.Default == null ? new Team(toSub, t.Others) : new Team(t.Default, t.Others.Append(toSub.Value).ToList());
        }
        else
        {
            if (fromSub is { } f) all.Remove(f);
            all.Remove(toSub.Value);
            _teams[pmId] = new Team(toSub, all);   // previous default (if kept) becomes an "other"
        }
        Rebuild();
    }

    /// <summary>Every sub-region, for the card's "Move to…" menu (region order, then name).</summary>
    public List<(int Id, string Name, string Region)> SubRegionChoices() =>
        _subs.OrderBy(s => Array.IndexOf(TeamsViewModel.DefaultRegions, s.Region) is var i && i >= 0 ? i : 99)
             .ThenBy(s => s.Region).ThenBy(s => s.Name)
             .Select(s => (s.Id, s.Name, s.Region)).ToList();

    public void MakeDefault(int pmId, int subId)
    {
        if (!IsEditMode || !_teams.TryGetValue(pmId, out var t) || t.Default == subId) return;
        _teams[pmId] = new Team(subId, t.All.Where(i => i != subId).ToList());
        Rebuild();
    }

    public void RemoveFromSubRegion(int pmId, int subId)
    {
        if (!IsEditMode || !_teams.TryGetValue(pmId, out var t)) return;
        var rest = t.All.Where(i => i != subId).ToList();
        // Removing the default promotes the next one; removing the last leaves them with no team.
        int? newDefault = t.Default != subId ? t.Default : rest.Count > 0 ? rest[0] : null;
        _teams[pmId] = new Team(newDefault, rest.Where(i => i != newDefault).ToList());
        Rebuild();
    }

    public void MovePa(int paId, int toPmId)
    {
        if (!IsEditMode || !_paPm.ContainsKey(paId) || _paPm[paId] == toPmId) return;
        _paPm[paId] = toPmId;
        Rebuild();
    }

    // ── TBD placeholders (saved immediately; only when there are no unsaved moves, so a reload
    //    can't drop them) ──

    /// <summary>"" when TBD actions can run now, else why not.</summary>
    public string TbdBlockedReason => !CanEdit ? "Only Super Admins and Directors can change teams."
        : HasChanges ? "Save or cancel your other changes first." : "";

    /// <summary>Adds a TBD PM on a sub-region — assignable like any PM until someone is hired.</summary>
    public void AddTbdPm(int subRegionId)
    {
        if (TbdBlockedReason != "") return;
        using var db = new AppDbContext();
        var sub = db.SubRegions.Find(subRegionId);
        if (sub == null) return;
        int n = db.PMs.Count(p => p.IsPlaceholder && p.LastName.StartsWith("PM (" + sub.Name + ")")) + 1;
        var pm = new PM
        {
            FirstName = "TBD", LastName = $"PM ({sub.Name})" + (n > 1 ? $" {n}" : ""),
            DataName = $"tbd.{sub.Name.ToLowerInvariant().Replace(" ", "")}.{n}",
            GeographicalLocation = sub.Region, HomeOffice = "", IdealUtilization = 80,
            IsPlaceholder = true, IsActive = true,
        };
        db.PMs.Add(pm);
        db.SaveChanges();
        PmTeamService.SetSubRegions(db, pm, sub.Id, Array.Empty<int>());
        db.SaveChanges();
        AuditService.Log("Create", "PM", pm.Id, $"TBD PM added to {sub.Name} (placeholder)");
        Load();
        StatusMessage = $"TBD PM added to {sub.Name}. Assign work to \"{pm.FullName}\" now; use Fill… when someone is hired.";
    }

    /// <summary>Adds a TBD PA supporting a PM (a PA still to be hired).</summary>
    public void AddTbdPa(int pmId)
    {
        if (TbdBlockedReason != "") return;
        using var db = new AppDbContext();
        var pm = db.PMs.Find(pmId);
        if (pm == null) return;
        var pa = new TeamMember { Role = TeamRoles.Pa, FirstName = "TBD", LastName = "PA", PmId = pmId, IsPlaceholder = true, IsActive = true };
        db.TeamMembers.Add(pa);
        db.SaveChanges();
        AuditService.Log("Create", "TeamMember", pa.Id, $"TBD PA added to support {pm.FullName} (placeholder)");
        Load();
        StatusMessage = $"TBD PA added for {pm.FullName}. Use Fill… when the PA is hired.";
    }

    /// <summary>Turns a TBD PM into the hired person — same record, so every assignment stays.</summary>
    public void FillPm(int pmId, PM hired)
    {
        if (TbdBlockedReason != "") return;
        using var db = new AppDbContext();
        var pm = db.PMs.Find(pmId);
        if (pm == null || !pm.IsPlaceholder) return;
        var was = pm.FullName;
        (pm.FirstName, pm.LastName, pm.DataName, pm.HomeOffice, pm.GeographicalLocation) =
            (hired.FirstName, hired.LastName, hired.DataName, hired.HomeOffice, hired.GeographicalLocation);
        (pm.Capacity, pm.IdealUtilization, pm.PmMultiplier, pm.IsActive) = (hired.Capacity, hired.IdealUtilization, hired.PmMultiplier, hired.IsActive);
        pm.IsPlaceholder = false;
        db.SaveChanges();
        AuditService.Log("Update", "PM", pm.Id, $"{was} filled by {pm.FullName}");
        Load();
        StatusMessage = $"{was} is now {pm.FullName} — their sub-regions and assignments are unchanged.";
    }

    /// <summary>Turns a TBD PA into the hired person.</summary>
    public void FillPa(int paId, TeamMember hired)
    {
        if (TbdBlockedReason != "") return;
        using var db = new AppDbContext();
        var pa = db.TeamMembers.Find(paId);
        if (pa == null || !pa.IsPlaceholder) return;
        (pa.FirstName, pa.LastName, pa.Email, pa.Phone) = (hired.FirstName, hired.LastName, hired.Email, hired.Phone);
        if (hired.PmId != null) pa.PmId = hired.PmId;
        pa.IsPlaceholder = false;
        db.SaveChanges();
        AuditService.Log("Update", "TeamMember", pa.Id, $"TBD PA filled by {pa.FullName}");
        Load();
        StatusMessage = $"TBD PA is now {pa.FullName}.";
    }

    /// <summary>Removes a TBD (deactivates — sync can't carry deletes). A TBD PM with assigned
    /// sites can't be removed: Fill it, or reassign its work first.</summary>
    public string RemoveTbd(int? pmId, int? paId)
    {
        if (TbdBlockedReason is { Length: > 0 } why) return why;
        using var db = new AppDbContext();
        if (pmId is { } pid && db.PMs.Find(pid) is { IsPlaceholder: true } pm)
        {
            int sites = db.Sites.Count(s => s.AssignedPmId == pid);
            if (sites > 0) return $"{pm.FullName} still has {sites} site(s) assigned. Fill it with the hired PM, or reassign the sites first.";
            pm.IsActive = false;
            db.SaveChanges();
            AuditService.Log("Deactivate", "PM", pm.Id, $"{pm.FullName} removed (placeholder)");
        }
        else if (paId is { } aid && db.TeamMembers.Find(aid) is { IsPlaceholder: true } pa)
        {
            pa.IsActive = false;
            db.SaveChanges();
            AuditService.Log("Deactivate", "TeamMember", pa.Id, "TBD PA removed (placeholder)");
        }
        else return "";
        Load();
        StatusMessage = "TBD removed.";
        return "";
    }

    /// <summary>Writes the working copy: PMs via PmTeamService (also re-points their PE), PAs'
    /// PmId. One save, one audit entry per person.</summary>
    public string SaveEdits()
    {
        if (!CanEdit) return "Only Super Admins and Directors can change teams.";
        var pmIds = ChangedPmIds().ToList();
        var paIds = ChangedPaIds().ToList();
        if (pmIds.Count + paIds.Count == 0) { IsEditMode = false; return "No changes to save."; }

        using var db = new AppDbContext();
        var names = _subs.ToDictionary(s => s.Id, s => s.Name);
        foreach (var pm in db.PMs.Where(p => pmIds.Contains(p.Id)).ToList())
        {
            var t = _teams[pm.Id];
            PmTeamService.SetSubRegions(db, pm, t.Default, t.Others);
            var text = PmTeamService.Describe(pm.SubRegionId, pm.OtherSubRegionIdList, names);
            AuditService.Log("Update", "PM", pm.Id, $"{pm.FullName} sub-regions: {(text == "" ? "no team" : text)} (Org Chart)");
        }
        foreach (var pa in db.TeamMembers.Where(m => paIds.Contains(m.Id)).ToList())
        {
            pa.PmId = _paPm[pa.Id];
            var pmName = _pms.FirstOrDefault(p => p.Id == pa.PmId)?.FullName ?? "no PM";
            AuditService.Log("Update", "TeamMember", pa.Id, $"PA {pa.FullName} now supports {pmName} (Org Chart)");
        }
        db.SaveChanges();

        IsEditMode = false;
        Load();
        var msg = $"Saved: {pmIds.Count} PM(s), {paIds.Count} PA(s) updated.";
        var outside = CountOutsideTeam();
        StatusMessage = msg + (outside > 0 ? $"  {outside} existing site assignment(s) are now outside their team — see Teams → Conflicts." : "");
        return StatusMessage;
    }

    private static int CountOutsideTeam()
    {
        using var db = new AppDbContext();
        var rules = TeamRules.For(db);
        return db.Sites.AsNoTracking().Where(s => s.AssignedPmId != null)
                 .Select(s => new { Pm = s.AssignedPmId!.Value, s.LocationId, s.CampusId, s.Id }).AsEnumerable()
                 .Count(s => !rules.Allows(s.Pm, s.LocationId, s.CampusId, s.Id));
    }

    // ── Build the tree from the working copy ──

    private bool Match(string name) =>
        !string.IsNullOrWhiteSpace(SearchText) && name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);

    private string RegionOf(PM p) =>
        _teams.TryGetValue(p.Id, out var t) && t.Default is { } d && _subs.FirstOrDefault(s => s.Id == d) is { } sub
            ? sub.Region : p.GeographicalLocation ?? "";

    private void Rebuild()
    {
        if (_subs == null) return;
        int matches = 0;
        bool M(string n) { var m = Match(n); if (m) matches++; return m; }
        var subName = _subs.ToDictionary(s => s.Id, s => s.Name);
        var changedPms = ChangedPmIds().ToHashSet();
        var changedPas = ChangedPaIds().ToHashSet();

        var regionNames = TeamsViewModel.DefaultRegions
            .Concat(_members.Where(m => m.Role == TeamRoles.Director).Select(m => m.Region))
            .Concat(_subs.Select(s => s.Region))
            .Concat(_pms.Select(RegionOf))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => Array.IndexOf(TeamsViewModel.DefaultRegions, r) is var i && i >= 0 ? i : 99).ThenBy(r => r)
            .Where(r => _geoFilter == "All" || string.Equals(r, _geoFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<OrgChartPm> PmCards(List<PM> list, int? columnSub)
        {
            return list.Select((p, i) =>
            {
                var t = _teams[p.Id];
                var pas = ShowPas
                    ? _members.Where(m => m.Role == TeamRoles.Pa && _paPm.GetValueOrDefault(m.Id) == p.Id)
                              .Select(m => new OrgChartPa { Id = m.Id, Name = m.IsPlaceholder ? "TBD" : m.FullName, IsMatch = Match(m.FullName), IsChanged = changedPas.Contains(m.Id),
                                                          SupportsText = (m.IsPlaceholder ? " to be hired" : "") + $" · supports {p.FullName}",
                                                          IsPlaceholder = m.IsPlaceholder, IsTeamOnSeveral = t.All.Count() > 1 }).ToList()
                    : new List<OrgChartPa>();
                bool isDefault = columnSub != null && t.Default == columnSub;
                string tag = columnSub == null ? ""
                    : isDefault ? (t.Others.Count > 0 ? "also " + string.Join(", ", t.Others.Select(o => subName.GetValueOrDefault(o, "?"))) : "")
                    : t.Default is { } d ? $"default: {subName.GetValueOrDefault(d, "?")}" : "";
                var sites = _siteCounts.GetValueOrDefault(p.Id);
                return new OrgChartPm
                {
                    PmId = p.Id, Name = p.FullName,
                    Detail = (sites == 1 ? "1 site" : $"{sites} sites") + (string.IsNullOrWhiteSpace(p.HomeOffice) ? "" : $" · {p.HomeOffice}"),
                    Pas = pas, IsLast = i == list.Count - 1,
                    IsPlaceholder = p.IsPlaceholder, HasSeveralSubRegions = t.All.Count() > 1,
                    Campuses = _campusesByPm.GetValueOrDefault(p.Id) ?? new List<string>(),
                    IsMatch = (isDefault || columnSub == null ? M(p.FullName) : Match(p.FullName)) | pas.Any(a => a.IsMatch),
                    SubRegionId = columnSub, IsDefault = isDefault, TagText = tag,
                    IsChanged = changedPms.Contains(p.Id) || pas.Any(a => changedPas.Contains(a.Id)),
                };
            }).ToList();
        }

        // Active PAs supporting the PMs on these cards (independent of the "PAs" display toggle).
        IEnumerable<TeamMember> PasFor(IEnumerable<OrgChartPm> cards)
        {
            var ids = cards.Select(c => c.PmId).ToHashSet();
            return _members.Where(m => m.Role == TeamRoles.Pa && _paPm.GetValueOrDefault(m.Id) is { } pid && ids.Contains(pid));
        }

        var regions = new List<OrgChartRegion>();
        foreach (var region in regionNames)
        {
            bool InRegion(string r) => string.Equals(r, region, StringComparison.OrdinalIgnoreCase);
            var directors = _members.Where(m => m.Role == TeamRoles.Director && InRegion(m.Region)).Select(m => m.FullName).ToList();

            var cols = new List<(int? Sub, string Title, List<string> Pes, string Clusters, List<OrgChartPm> Pms, bool NoTeam)>();
            foreach (var s in _subs.Where(s => InRegion(s.Region)))
            {
                var pes = _members.Where(m => m.Role == TeamRoles.Pe && m.SubRegionId == s.Id).Select(m => m.FullName).ToList();
                // Everyone on this sub-region: default members first, then those who also cover it.
                var pms = _pms.Where(p => _teams[p.Id].All.Contains(s.Id))
                              .OrderBy(p => _teams[p.Id].Default == s.Id ? 0 : 1).ThenBy(p => p.LastName).ToList();
                var cl  = _clustersBySub.GetValueOrDefault(s.Id) ?? new();
                var clusters = !ShowClusters || cl.Count == 0 ? ""
                    : cl.Count <= 8 ? string.Join(", ", cl) : $"{string.Join(", ", cl.Take(8))} +{cl.Count - 8}";
                cols.Add((s.Id, s.Name, pes, clusters, PmCards(pms, s.Id), false));
            }
            var noTeam = _pms.Where(p => _teams[p.Id].Default == null && InRegion(p.GeographicalLocation ?? "")).ToList();
            // In edit mode the No-team column always shows, as a drop target.
            if (noTeam.Count > 0 || IsEditMode) cols.Add((null, "No team yet", new(), "", PmCards(noTeam, null), true));

            var columns = cols.Select((c, i) => new OrgChartColumn
            {
                SubRegionId = c.Sub, Title = c.Title, Pes = c.Pes, PeMatch = c.Pes.Any(n => M(n)), Clusters = c.Clusters,
                Pms = c.Pms, IsNoTeam = c.NoTeam, IsFirst = i == 0, IsLast = i == cols.Count - 1,
                PaCount    = PasFor(c.Pms).Count(),
                TbdPaCount = PasFor(c.Pms).Count(m => m.IsPlaceholder),
            }).ToList();

            int pmCount = _pms.Count(p => InRegion(RegionOf(p)) || (_teams[p.Id].Default == null && InRegion(p.GeographicalLocation ?? "")));
            int paCount = _members.Count(m => m.Role == TeamRoles.Pa && _paPm.GetValueOrDefault(m.Id) is { } pid
                                              && _pms.FirstOrDefault(p => p.Id == pid) is { } pp && InRegion(RegionOf(pp)));
            int subCount = columns.Count(c => !c.IsNoTeam);
            regions.Add(new OrgChartRegion
            {
                Region = region, Directors = directors, DirectorMatch = directors.Any(n => M(n)), Columns = columns,
                Summary = $"{subCount} sub-region{(subCount == 1 ? "" : "s")} · {pmCount} PM{(pmCount == 1 ? "" : "s")}"
                          + (ShowPas ? $" · {paCount} PA{(paCount == 1 ? "" : "s")}" : ""),
            });
        }
        Regions = new(regions.Where(r => r.HasDirector || r.HasColumns));

        var notes = new List<string>();
        var loosePes = _members.Where(m => m.Role == TeamRoles.Pe && (m.SubRegionId == null || _subs.All(s => s.Id != m.SubRegionId))).ToList();
        if (loosePes.Count > 0) notes.Add($"PE(s) without a sub-region: {string.Join(", ", loosePes.Select(m => m.FullName))}");
        var loosePas = _members.Where(m => m.Role == TeamRoles.Pa && (_paPm.GetValueOrDefault(m.Id) is not { } pid || _pms.All(p => p.Id != pid))).ToList();
        if (loosePas.Count > 0) notes.Add($"PA(s) not supporting an active PM: {string.Join(", ", loosePas.Select(m => m.FullName))}");
        Notes = notes;

        int d = _members.Count(m => m.Role == TeamRoles.Director), pe = _members.Count(m => m.Role == TeamRoles.Pe);
        int pa = _members.Count(m => m.Role == TeamRoles.Pa), onTeam = _pms.Count(p => _teams[p.Id].Default != null);
        int multi = _pms.Count(p => _teams[p.Id].Others.Count > 0);
        Headline = $"{d} Director{(d == 1 ? "" : "s")} · {pe} PE{(pe == 1 ? "" : "s")} · {_pms.Count} PMs ({onTeam} on a team"
                   + (multi > 0 ? $", {multi} on several" : "") + $") · {pa} PA{(pa == 1 ? "" : "s")}";
        MatchInfo = string.IsNullOrWhiteSpace(SearchText) ? "" : matches == 0 ? "No match" : $"{matches} match{(matches == 1 ? "" : "es")}";

        int cp = changedPms.Count, ca = changedPas.Count;
        ChangeSummary = cp + ca == 0 ? "No changes yet"
            : string.Join(", ", new[] { cp > 0 ? $"{cp} PM{(cp == 1 ? "" : "s")}" : null, ca > 0 ? $"{ca} PA{(ca == 1 ? "" : "s")}" : null }.Where(x => x != null))
              + " changed — not saved yet";
        OnPropertyChanged(nameof(HasChanges));
    }
}
