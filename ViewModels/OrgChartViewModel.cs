using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

// ── Chart nodes (display-only; rebuilt on every load/search) ──

public class OrgChartPm
{
    public int          PmId      { get; init; }
    public string       Name      { get; init; } = "";
    public string       Detail    { get; init; } = "";   // "12 sites · Remote"
    public List<string> Pas       { get; init; } = new();
    public bool         HasPas    => Pas.Count > 0;
    public bool         IsLast    { get; init; }         // draws the end of the bracket line
    public bool         IsMatch   { get; init; }         // search highlight
}

/// <summary>One column under a region: a sub-region with its PE(s) and PMs, or the region's
/// "No team yet" column (PMs not under any PE).</summary>
public class OrgChartColumn
{
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
    public bool              IsNoTeam { get; init; }
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
    public string               Summary   { get; init; } = "";   // "3 sub-regions · 11 PMs · 2 PAs"
}

/// <summary>
/// Org Chart page: Director (region) › PE (sub-region) › PM › PA drawn as a tree, built from the
/// same team data as the Teams page (TeamMember, SubRegion, PM.PeId, Location/Campus.SubRegionId).
/// Active people only. Read-only — editing stays on the Teams page.
/// </summary>
public partial class OrgChartViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<OrgChartRegion> _regions = new();
    [ObservableProperty] private List<string> _notes = new();          // people who don't fit the tree yet
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private bool   _showClusters = true;
    [ObservableProperty] private bool   _showPas = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _matchInfo = "";

    public bool HasNotes => Notes.Count > 0;
    partial void OnNotesChanged(List<string> value) => OnPropertyChanged(nameof(HasNotes));
    public string ZoomText => $"{Zoom * 100:0}%";
    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnShowClustersChanged(bool value) => Rebuild();
    partial void OnShowPasChanged(bool value)      => Rebuild();

    /// <summary>Set by MainViewModel: clicking a PM card opens their PM Detail page.</summary>
    public Action<int>? OpenPm    { get; set; }
    /// <summary>Set by MainViewModel: "Edit teams" goes to the Teams page.</summary>
    public Action?      OpenTeams { get; set; }

    private string _geoFilter = "All";
    public void ApplyGeoFilter(string geo) { _geoFilter = geo; Rebuild(); }

    // ── Snapshot (loaded once per Load, rebuilt into nodes on filter/search) ──
    private List<SubRegion>  _subs     = new();
    private List<TeamMember> _members  = new();
    private List<PM>         _pms      = new();
    private Dictionary<int, int>          _siteCounts   = new();
    private Dictionary<int, List<string>> _clustersBySub = new();
    private TeamRules? _rules;

    public void Load()
    {
        using var db = new AppDbContext();
        _subs    = db.SubRegions.AsNoTracking().OrderBy(s => s.Name).ToList();
        _members = db.TeamMembers.AsNoTracking().Where(m => m.IsActive).OrderBy(m => m.LastName).ThenBy(m => m.FirstName).ToList();
        _pms     = db.PMs.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ToList();
        _siteCounts = db.Sites.AsNoTracking().Where(s => s.AssignedPmId != null && !s.IsClosed)
                        .GroupBy(s => s.AssignedPmId!.Value).Select(g => new { g.Key, N = g.Count() })
                        .ToDictionary(x => x.Key, x => x.N);

        // Clusters per sub-region: whole clusters mapped to it, plus campuses split off into it.
        var locs = db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name, l.SubRegionId }).ToList();
        var split = db.Campuses.AsNoTracking().Where(c => c.SubRegionId != null)
                      .Select(c => new { c.Name, c.LocationId, Sub = c.SubRegionId!.Value }).ToList();
        _clustersBySub = _subs.ToDictionary(s => s.Id, s =>
            locs.Where(l => l.SubRegionId == s.Id).Select(l => l.Name)
                .Concat(split.Where(c => c.Sub == s.Id && locs.FirstOrDefault(l => l.Id == c.LocationId)?.SubRegionId != s.Id)
                             .Select(c => c.Name))
                .OrderBy(n => n).ToList());
        _rules = TeamRules.For(db);
        Rebuild();
    }

    private bool Match(string name) =>
        !string.IsNullOrWhiteSpace(SearchText) && name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        if (_rules is not { } rules) return;
        int matches = 0;
        bool M(string n) { var m = Match(n); if (m) matches++; return m; }

        var regionNames = TeamsViewModel.DefaultRegions
            .Concat(_members.Where(m => m.Role == TeamRoles.Director).Select(m => m.Region))
            .Concat(_subs.Select(s => s.Region))
            .Concat(_pms.Select(p => rules.PmTeam(p.Id).Region))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => Array.IndexOf(TeamsViewModel.DefaultRegions, r) is var i && i >= 0 ? i : 99).ThenBy(r => r)
            .Where(r => _geoFilter == "All" || string.Equals(r, _geoFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<OrgChartPm> PmCards(IEnumerable<PM> pms)
        {
            var list = pms.ToList();
            return list.Select((p, i) =>
            {
                var pas = ShowPas
                    ? _members.Where(m => m.Role == TeamRoles.Pa && m.PmId == p.Id).Select(m => m.FullName).ToList()
                    : new List<string>();
                var sites = _siteCounts.GetValueOrDefault(p.Id);
                return new OrgChartPm
                {
                    PmId    = p.Id,
                    Name    = p.FullName,
                    Detail  = (sites == 1 ? "1 site" : $"{sites} sites") + (string.IsNullOrWhiteSpace(p.HomeOffice) ? "" : $" · {p.HomeOffice}"),
                    Pas     = pas,
                    IsLast  = i == list.Count - 1,
                    IsMatch = M(p.FullName) | pas.Any(Match),
                };
            }).ToList();
        }

        var regions = new List<OrgChartRegion>();
        foreach (var region in regionNames)
        {
            bool InRegion(string r) => string.Equals(r, region, StringComparison.OrdinalIgnoreCase);
            var directors = _members.Where(m => m.Role == TeamRoles.Director && InRegion(m.Region)).Select(m => m.FullName).ToList();

            var cols = new List<(string Title, List<string> Pes, string Clusters, List<OrgChartPm> Pms, bool NoTeam)>();
            foreach (var s in _subs.Where(s => InRegion(s.Region)))
            {
                var pes = _members.Where(m => m.Role == TeamRoles.Pe && m.SubRegionId == s.Id).Select(m => m.FullName).ToList();
                var pms = _pms.Where(p => rules.PmTeam(p.Id).SubRegion == s.Name);
                var cl  = _clustersBySub.GetValueOrDefault(s.Id) ?? new();
                var clusters = !ShowClusters || cl.Count == 0 ? ""
                    : cl.Count <= 8 ? string.Join(", ", cl) : $"{string.Join(", ", cl.Take(8))} +{cl.Count - 8}";
                cols.Add((s.Name, pes, clusters, PmCards(pms), false));
            }
            var noTeam = _pms.Where(p => rules.PmTeam(p.Id).SubRegion == "" && InRegion(rules.PmTeam(p.Id).Region)).ToList();
            if (noTeam.Count > 0) cols.Add(("No team yet", new(), "", PmCards(noTeam), true));

            var columns = cols.Select((c, i) => new OrgChartColumn
            {
                Title = c.Title, Pes = c.Pes, PeMatch = c.Pes.Any(n => M(n)), Clusters = c.Clusters, Pms = c.Pms,
                IsNoTeam = c.NoTeam, IsFirst = i == 0, IsLast = i == cols.Count - 1,
            }).ToList();

            int pmCount = columns.Sum(c => c.Pms.Count), paCount = columns.Sum(c => c.Pms.Sum(p => p.Pas.Count));
            int subCount = columns.Count(c => !c.IsNoTeam);
            regions.Add(new OrgChartRegion
            {
                Region = region, Directors = directors, DirectorMatch = directors.Any(n => M(n)), Columns = columns,
                Summary = $"{subCount} sub-region{(subCount == 1 ? "" : "s")} · {pmCount} PM{(pmCount == 1 ? "" : "s")}"
                          + (ShowPas ? $" · {paCount} PA{(paCount == 1 ? "" : "s")}" : ""),
            });
        }
        Regions = new(regions.Where(r => r.HasDirector || r.HasColumns));

        // People who can't be placed in the tree yet — said plainly rather than silently dropped.
        var notes = new List<string>();
        var loosePes = _members.Where(m => m.Role == TeamRoles.Pe && (m.SubRegionId == null || _subs.All(s => s.Id != m.SubRegionId))).ToList();
        if (loosePes.Count > 0) notes.Add($"PE(s) without a sub-region: {string.Join(", ", loosePes.Select(m => m.FullName))}");
        var loosePas = _members.Where(m => m.Role == TeamRoles.Pa && (m.PmId == null || _pms.All(p => p.Id != m.PmId))).ToList();
        if (loosePas.Count > 0) notes.Add($"PA(s) not supporting an active PM: {string.Join(", ", loosePas.Select(m => m.FullName))}");
        Notes = notes;

        int d = _members.Count(m => m.Role == TeamRoles.Director), pe = _members.Count(m => m.Role == TeamRoles.Pe);
        int pa = _members.Count(m => m.Role == TeamRoles.Pa), onTeam = _pms.Count(p => rules.PmTeam(p.Id).SubRegion != "");
        Headline = $"{d} Director{(d == 1 ? "" : "s")} · {pe} PE{(pe == 1 ? "" : "s")} · {_pms.Count} PMs ({onTeam} on a team) · {pa} PA{(pa == 1 ? "" : "s")}";
        MatchInfo = string.IsNullOrWhiteSpace(SearchText) ? "" : matches == 0 ? "No match" : $"{matches} match{(matches == 1 ? "" : "es")}";
    }
}
