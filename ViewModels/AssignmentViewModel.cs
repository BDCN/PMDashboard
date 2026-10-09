using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

/// <summary>Ranked PM candidate with score reasoning.</summary>
public class PmCandidate
{
    public PM     Pm           { get; init; } = null!;
    public int    Rank         { get; init; }
    public double Score        { get; init; }
    public string ScoreText    { get; init; } = "";
    public int    CurrentSites { get; init; }
    public string UtilText     { get; init; } = "";
    public string Reasons      { get; init; } = "";
}

/// <summary>
/// Assignment page. PMs are assigned per campus (CampusService.AssignCampus cascades to every site
/// in the campus that doesn't have its own PM). With exactly one campus selected, one of its sites
/// can be picked to give that site its own PM (a site override), and one of that site's ROMPs for
/// the ROMP-level override (see AssignmentResolver).
/// </summary>
public partial class AssignmentViewModel : ObservableObject
{
    // ── Target selectors ──
    [ObservableProperty] private ObservableCollection<Location> _locations   = new();
    [ObservableProperty] private ObservableCollection<Campus>   _campuses    = new();
    [ObservableProperty] private ObservableCollection<Site>     _campusSites = new();
    [ObservableProperty] private ObservableCollection<Romp>     _romps       = new();

    [ObservableProperty] private Location? _selectedLocation;
    [ObservableProperty] private Campus?   _selectedCampus;  // non-null only when exactly 1 campus selected
    [ObservableProperty] private Site?     _selectedSite;    // optional site within SelectedCampus
    [ObservableProperty] private Romp?     _selectedRomp;    // optional ROMP within SelectedSite

    // ── Filters ──
    [ObservableProperty] private bool _showUnassignedOnly;

    partial void OnShowUnassignedOnlyChanged(bool value)
    {
        // A cluster whose campuses are all assigned drops out of the dropdown entirely.
        var previouslySelectedId = SelectedLocation?.Id;
        LoadLocations();
        if (previouslySelectedId.HasValue)
            // Re-pointing (or clearing) triggers OnSelectedLocationChanged → campuses reload.
            SelectedLocation = Locations.FirstOrDefault(l => l.Id == previouslySelectedId.Value);
    }

    // ── Multi-select campus tracking ──
    private readonly HashSet<int> _selectedCampusIds = new();

    [ObservableProperty] private int    _selectedCampusCount;
    [ObservableProperty] private string _campusSelectionInfo = "No campuses selected";

    /// <summary>Called by code-behind whenever the campus ListBox selection changes.</summary>
    public void SetSelectedCampuses(IEnumerable<Campus> campuses)
    {
        _selectedCampusIds.Clear();
        var list = campuses.ToList();
        foreach (var c in list) _selectedCampusIds.Add(c.Id);

        SelectedCampus = list.Count == 1 ? list[0] : null;
        UpdateSelectionInfo();
        RefreshEligiblePms();
        ComputeCandidates();
    }

    private void UpdateSelectionInfo()
    {
        int count = _selectedCampusIds.Count;
        SelectedCampusCount = count;
        CampusSelectionInfo = count == 0 ? "No campuses selected"
                            : count == 1 ? "1 campus selected"
                            : $"{count} campuses selected";
        BulkSelectionInfo = count > 1 ? $"{count} campuses selected" : "";
        NotifyBulkProps();
    }

    // ── Override PM selector ──
    [ObservableProperty] private ObservableCollection<PM> _allPms = new();
    [ObservableProperty] private PM?                      _overridePm;

    /// <summary>The active PMs allowed on the selected cluster (TeamRules) — every PM dropdown on
    /// the page uses this, so an out-of-area PM can't even be picked.</summary>
    [ObservableProperty] private ObservableCollection<PM> _eligiblePms = new();

    /// <summary>"Cluster IAD is in East 1 — only East 1 team PMs can be assigned." (or region-only text).</summary>
    [ObservableProperty] private string _teamScopeInfo = "";

    /// <summary>Why nobody can be suggested/picked for the current selection (the team rule left
    /// no eligible PM), with what to do about it — or "" when someone is eligible.</summary>
    [ObservableProperty] private string _noEligibleMessage = "";
    public bool HasNoEligiblePms => NoEligibleMessage != "";
    partial void OnNoEligibleMessageChanged(string value) => OnPropertyChanged(nameof(HasNoEligiblePms));

    /// <summary>
    /// Who may be assigned here: with campuses selected, the PMs allowed on every one of them (a
    /// cluster can be split across sub-regions — see Campus.SubRegionId); with none, the cluster's
    /// own rule (used by "Assign PM to Cluster").
    /// </summary>
    private static string NoEligibleText(List<(string SubRegion, string Region)> areas)
    {
        if (areas.Count > 1)
            return $"No PM is on all of these teams ({string.Join(", ", areas.Select(a => a.SubRegion != "" ? a.SubRegion : a.Region))}). " +
                   "Select campuses from one sub-region at a time.";
        var (sub, region) = areas[0];
        if (sub != "")
        {
            using var db = new AppDbContext();
            var pes = db.TeamMembers.AsNoTracking()
                        .Where(t => t.Role == TeamRoles.Pe && t.IsActive && t.SubRegion != null && t.SubRegion.Name == sub)
                        .Select(t => (t.FirstName + " " + t.LastName).Trim()).ToList();
            var peText = pes.Count == 0 ? $" ({sub} has no PE yet either)" : $" (PE: {string.Join(", ", pes)})";
            return $"Nobody is on the {sub} team yet{peText}, so no PM can be assigned here. Add PMs to {sub}: " +
                   "Org Chart → Edit Org Chart and drag them in, or PM Maintenance → Sub-regions, or Teams → People → select PMs → Set. " +
                   "Or change this cluster's sub-region on Teams → Sub-regions & Clusters.";
        }
        return string.IsNullOrWhiteSpace(region)
            ? "No active PMs found. Add or activate PMs in PM Maintenance."
            : $"No active PM in {region} can take this (PMs on a team can only take their own sub-region's clusters plus unmapped ones in their region). " +
              "Check the PMs' regions in PM Maintenance and their teams on the Teams page.";
    }

    private void RefreshEligiblePms()
    {
        if (SelectedLocation is not { } loc) { EligiblePms = new(); TeamScopeInfo = ""; NoEligibleMessage = ""; return; }
        var rules = TeamRules.Current();
        var campusIds = _selectedCampusIds.ToList();
        EligiblePms = new ObservableCollection<PM>(rules.EligibleForAll(AllPms, loc.Id, campusIds));

        var areas = (campusIds.Count == 0 ? new List<int?> { null } : campusIds.Select(c => (int?)c).ToList())
            .Select(c => rules.EffectiveArea(loc.Id, c)).Distinct().ToList();
        var where = campusIds.Count switch
        {
            0 => $"Cluster {loc.Name}",
            1 => $"Campus {SelectedCampus?.Name ?? Campuses.FirstOrDefault(c => c.Id == campusIds[0])?.Name}",
            _ => $"The {campusIds.Count} selected campuses",
        };
        TeamScopeInfo =
            areas.Count > 1
                ? $"{where} span {string.Join(", ", areas.Select(a => a.SubRegion != "" ? a.SubRegion : $"{a.Region} (no sub-region)"))} — only PMs allowed on all of them are listed ({EligiblePms.Count}). Select campuses from one area to see more."
            : areas[0].SubRegion is { Length: > 0 } sub
                ? $"{where} {(campusIds.Count > 1 ? "are" : "is")} in {sub} — only PMs on the {sub} team can be assigned ({EligiblePms.Count} active)."
            : string.IsNullOrWhiteSpace(areas[0].Region)
                ? $"{where} {(campusIds.Count > 1 ? "have" : "has")} no region or sub-region — any PM can be assigned."
                : $"{where} {(campusIds.Count > 1 ? "have" : "has")} no sub-region yet — any {areas[0].Region} PM can be assigned ({EligiblePms.Count} active). Set it on the Teams page.";
        if (campusIds.Count == 0 && rules.IsSplit(loc.Id))
            TeamScopeInfo += " This cluster is split across sub-regions — select campuses to see who can take them.";

        NoEligibleMessage = EligiblePms.Count > 0 ? "" : NoEligibleText(areas);
        // Keep a picked PM only if they're still allowed here.
        if (OverridePm     is { } o && !EligiblePms.Any(p => p.Id == o.Id)) OverridePm     = null;
        if (SiteOverridePm is { } s && !EligiblePms.Any(p => p.Id == s.Id)) SiteOverridePm = null;
        if (RompOverridePm is { } r && !EligiblePms.Any(p => p.Id == r.Id)) RompOverridePm = null;
    }

    // ── Site-level override (this site keeps its own PM when the campus is reassigned) ──
    [ObservableProperty] private PM? _siteOverridePm;

    public bool HasSiteSelected     => SelectedSite != null;
    public bool SiteHasOwnOverride  => SelectedSite?.PmOverride == true;

    // ── ROMP-level override (independent of the site's own PM — see AssignmentResolver) ──
    [ObservableProperty] private PM? _rompOverridePm;

    public bool HasRompSelected => SelectedRomp != null;

    /// <summary>The PM this ROMP's score currently counts toward — its own override if set, else its site's PM.</summary>
    public PM? RompEffectivePm => SelectedRomp?.AssignedPm ?? SelectedSite?.AssignedPm;

    public bool RompHasOwnOverride => SelectedRomp?.AssignedPmId != null;

    partial void OnSelectedRompChanged(Romp? value)
    {
        OnPropertyChanged(nameof(HasRompSelected));
        OnPropertyChanged(nameof(RompEffectivePm));
        OnPropertyChanged(nameof(RompHasOwnOverride));
    }

    // ── PM candidates ──
    [ObservableProperty] private ObservableCollection<PmCandidate> _candidates = new();
    [ObservableProperty] private int                               _activeRuleCount;

    // ── Status ──
    [ObservableProperty] private string _statusMessage     = "";
    [ObservableProperty] private string _bulkSelectionInfo = "";

    // Computed properties used by XAML DataTriggers
    public bool IsSingleCampusSelected => _selectedCampusIds.Count == 1;
    public bool IsBulkSelected         => _selectedCampusIds.Count > 1;
    public bool HasSelection           => _selectedCampusIds.Count > 0;

    /// <summary>Geographic region of the selected cluster ("East", "Central", "West", or "").</summary>
    public string SelectedLocationGeo => SelectedLocation?.GeographicalLocation ?? "";

    partial void OnSelectedLocationChanged(Location? value)
    {
        ClearSelectionInternal();
        LoadCampuses();
        Candidates = new();
        BulkSelectionInfo = "";
        OnPropertyChanged(nameof(SelectedLocationGeo));
        RefreshEligiblePms();
        OnPropertyChanged(nameof(ClusterHasNoCampuses));
        OnPropertyChanged(nameof(ClusterAssignedPmName));
    }

    // Unfiltered campus count for the selected cluster — "Unassigned Only" can empty the list
    // without the cluster actually having no campuses.
    private int _clusterCampusTotal;

    /// <summary>Drives the "Assign PM to Cluster" action — only for a cluster with zero campuses,
    /// where the per-campus flow has nothing to apply to.</summary>
    public bool ClusterHasNoCampuses => SelectedLocation != null && _clusterCampusTotal == 0;

    public string ClusterAssignedPmName => SelectedLocation?.AssignedPm?.FullName ?? "";

    partial void OnSelectedCampusChanged(Campus? value)
    {
        LoadCampusSites();
        OnPropertyChanged(nameof(SelectedCampusOverrideInfo));
    }

    partial void OnSelectedSiteChanged(Site? value)
    {
        LoadRomps();
        OnPropertyChanged(nameof(HasSiteSelected));
        OnPropertyChanged(nameof(SiteHasOwnOverride));
        OnPropertyChanged(nameof(RompEffectivePm));
    }

    /// <summary>"2 sites have their own PM" note under the single-campus assignment, or "".</summary>
    public string SelectedCampusOverrideInfo => SelectedCampus is { OverrideCount: > 0 } c
        ? $"{c.OverrideCount} of {c.SiteCount} site{(c.SiteCount == 1 ? "" : "s")} have their own PM and keep it when the campus is reassigned."
        : "";

    private void NotifyBulkProps()
    {
        OnPropertyChanged(nameof(IsSingleCampusSelected));
        OnPropertyChanged(nameof(IsBulkSelected));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void ClearSelectionInternal()
    {
        _selectedCampusIds.Clear();
        SelectedCampus      = null;
        SelectedCampusCount = 0;
        CampusSelectionInfo = "No campuses selected";
        NotifyBulkProps();
    }

    // ── Public load ──

    public void Load()
    {
        LoadLocations();
        LoadAllPms();
        RefreshEligiblePms();
        using var db = new AppDbContext();
        ActiveRuleCount = db.AssignmentRules.Count(r => r.IsActive);
    }

    /// <summary>Raised after <see cref="RefreshFromSync"/> reloads, carrying the campus Ids that
    /// were selected beforehand — the multi-selection lives in the page's ListBox, so only the page
    /// can put it back.</summary>
    public event Action<IReadOnlySet<int>>? ReloadedFromSync;

    /// <summary>
    /// Background-sync refresh: reloads PMs, clusters and the current cluster's campuses while keeping
    /// what the user had picked. Every reload replaces the collections with fresh instances, so each
    /// selection is re-pointed by Id.
    /// </summary>
    public void RefreshFromSync()
    {
        var prevCampusIds    = _selectedCampusIds.ToHashSet();
        var prevLocId        = SelectedLocation?.Id;
        var prevOverride     = OverridePm?.Id;
        var prevSiteOverride = SiteOverridePm?.Id;
        var prevRompOverride = RompOverridePm?.Id;

        LoadAllPms();
        OverridePm     = prevOverride     is { } o ? AllPms.FirstOrDefault(p => p.Id == o) : null;
        SiteOverridePm = prevSiteOverride is { } s ? AllPms.FirstOrDefault(p => p.Id == s) : null;
        RompOverridePm = prevRompOverride is { } r ? AllPms.FirstOrDefault(p => p.Id == r) : null;

        LoadLocations();
        SelectedLocation = prevLocId is { } id ? Locations.FirstOrDefault(l => l.Id == id) : null;

        using (var db = new AppDbContext())
            ActiveRuleCount = db.AssignmentRules.Count(x => x.IsActive);

        ReloadedFromSync?.Invoke(prevCampusIds);
    }

    /// <summary>Every Site with its Location + current PM assignment — for the Site Assignments import template.</summary>
    public List<Site> GetAllSitesForExport()
    {
        using var db = new AppDbContext();
        return db.Sites.AsNoTracking()
                       .Include(s => s.Location)
                       .Include(s => s.AssignedPm)
                       .OrderBy(s => s.Location!.Name).ThenBy(s => s.Name)
                       .ToList();
    }

    /// <summary>Refreshes clusters/PMs/rule count and whatever's currently on screen after a bulk import.</summary>
    public void RefreshAfterImport()
    {
        var locId = SelectedLocation?.Id;
        Load();
        SelectedLocation = locId is { } id ? Locations.FirstOrDefault(l => l.Id == id) : null;
    }

    private void LoadLocations()
    {
        using var db = new AppDbContext();
        IQueryable<Location> query = db.Locations.AsNoTracking().Include(l => l.AssignedPm);
        if (ShowUnassignedOnly)
            // A zero-campus cluster has no campus to be unassigned — use its own direct PM instead,
            // so it doesn't disappear from this filter entirely.
            query = query.Where(l => l.Campuses.Any(c => c.AssignedPmId == null)
                                   || (!l.Campuses.Any() && l.AssignedPmId == null));
        Locations = new ObservableCollection<Location>(query.OrderBy(l => l.Name).ToList());
    }

    private void LoadCampuses()
    {
        if (SelectedLocation == null) { _clusterCampusTotal = 0; Campuses = new(); return; }

        var locId = SelectedLocation.Id;
        using var db = new AppDbContext();
        _clusterCampusTotal = db.Campuses.Count(c => c.LocationId == locId);

        IQueryable<Campus> query = db.Campuses.AsNoTracking()
                                              .Include(c => c.AssignedPm)
                                              .Include(c => c.SubRegion)
                                              .Include(c => c.Location).ThenInclude(l => l.SubRegion)
                                              .Include(c => c.Sites)
                                              .Where(c => c.LocationId == locId);
        if (ShowUnassignedOnly)
            query = query.Where(c => c.AssignedPmId == null);

        Campuses = new ObservableCollection<Campus>(query.OrderBy(c => c.Name).ToList());
        OnPropertyChanged(nameof(ClusterHasNoCampuses));
    }

    private void LoadCampusSites()
    {
        var previousSiteId = SelectedSite?.Id;
        if (SelectedCampus == null) { CampusSites = new(); SelectedSite = null; return; }

        var campusId = SelectedCampus.Id;
        using var db = new AppDbContext();
        var list = db.Sites.AsNoTracking()
                           .Include(s => s.AssignedPm)
                           .Where(s => s.CampusId == campusId)
                           .OrderBy(s => s.Name)
                           .ToList();
        CampusSites  = new ObservableCollection<Site>(list);
        SelectedSite = previousSiteId is { } sid ? CampusSites.FirstOrDefault(s => s.Id == sid) : null;
    }

    private void LoadRomps()
    {
        var previousRompId = SelectedRomp?.Id;
        if (SelectedSite == null) { Romps = new(); SelectedRomp = null; return; }
        var siteId = SelectedSite.Id;
        using var db = new AppDbContext();
        var list = db.Romps.AsNoTracking()
                           .Include(r => r.AssignedPm)
                           .Where(r => r.SiteId == siteId)
                           .OrderBy(r => r.Name)
                           .ToList();
        Romps = new ObservableCollection<Romp>(list);
        // Re-point to the same ROMP by Id so a just-made ROMP-level assignment shows immediately.
        SelectedRomp = previousRompId is { } rid ? Romps.FirstOrDefault(r => r.Id == rid) : null;
    }

    private void LoadAllPms()
    {
        using var db = new AppDbContext();
        var pms = db.PMs.AsNoTracking()
                        .Where(p => p.IsActive)
                        .OrderBy(p => p.GeographicalLocation).ThenBy(p => p.LastName)
                        .ToList();
        AllPms = new ObservableCollection<PM>(pms);
    }

    // ── Scoring engine ──

    public void ComputeCandidates()
    {
        if (_selectedCampusIds.Count == 0 || SelectedLocation == null) { Candidates = new(); return; }

        using var db = new AppDbContext();

        var rules = db.AssignmentRules.AsNoTracking()
                                      .Where(r => r.IsActive)
                                      .OrderBy(r => r.SortOrder)
                                      .ToList();
        ActiveRuleCount = rules.Count;
        if (rules.Count == 0) { Candidates = new(); return; }

        // Team boundary: only PMs allowed on this cluster are candidates at all.
        var teamRules = TeamRules.For(db);
        var selectedCampuses = _selectedCampusIds.ToList();
        var pms = teamRules.EligibleForAll(db.PMs.AsNoTracking().Where(p => p.IsActive).ToList(),
                                           SelectedLocation.Id, selectedCampuses);

        // Include romps + equipment types so CapacityAvailability can compute score-based load
        var sitesByPm = db.Sites.AsNoTracking()
                                .Include(s => s.Romps)
                                    .ThenInclude(r => r.EquipmentTypes)
                                        .ThenInclude(et => et.Category)
                                .Where(s => s.AssignedPmId != null)
                                .AsEnumerable()
                                .GroupBy(s => s.AssignedPmId!.Value)
                                .ToDictionary(g => g.Key, g => g.ToList());

        var locationGeo = SelectedLocation.GeographicalLocation;
        var locationId  = SelectedLocation.Id;

        var  pctMode     = new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites");
        bool useScore    = pctMode == "Score";
        bool useCount    = pctMode == "Count";
        var  p           = ScoreConfig.WorkloadParams;
        var  completeSet = StatusConfig.Complete.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var raw = new List<(PM pm, double score, int siteCnt, double utilPct, List<string> reasons)>();

        foreach (var pm in pms)
        {
            double score   = 0;
            var    reasons = new List<string>();
            var    pmSites = sitesByPm.GetValueOrDefault(pm.Id, new());
            var    siteCnt = pmSites.Count;
            double utilPct = pm.Capacity > 0 ? siteCnt / pm.Capacity * 100.0 : 0;

            foreach (var rule in rules)
            {
                switch (rule.RuleKey)
                {
                    case "RegionMatch":
                        if (!string.IsNullOrEmpty(locationGeo) && pm.GeographicalLocation == locationGeo)
                        {
                            score += rule.Weight * 3.0;
                            reasons.Add($"Region match ({locationGeo})");
                        }
                        break;

                    case "LocationFamiliarity":
                        var inSameLocation = pmSites.Count(s => s.LocationId == locationId);
                        if (inSameLocation > 0)
                        {
                            score += rule.Weight * Math.Min(inSameLocation, 5);
                            reasons.Add($"{inSameLocation} site(s) already in this cluster");
                        }
                        break;

                    case "CapacityAvailability":
                        if (useCount)
                        {
                            // No capacity/utilization data in this mode — rank purely by how few
                            // active sites this PM already carries, so work spreads evenly.
                            var activeSiteCount = pmSites.Count(s => s.SiteStatus != "Complete");
                            var avail = 1.0 / (1.0 + activeSiteCount);
                            score += rule.Weight * avail * 5.0;
                            reasons.Add($"{activeSiteCount} active site(s) currently assigned");
                        }
                        else if (useScore)
                        {
                            var activeSiteList = pmSites.Where(s => s.SiteStatus != "Complete").ToList();
                            // Exclude ROMPs overridden away to a different PM (AssignmentResolver).
                            var rompScores = activeSiteList
                                .SelectMany(s => s.Romps.Where(r => !completeSet.Contains(r.Status)
                                    && AssignmentResolver.EffectivePmId(r.AssignedPmId, s.AssignedPmId) == pm.Id))
                                .Select(r =>
                                {
                                    double act = StatusConfig.ScoreWeights.TryGetValue(r.Status, out var aw) ? aw : 0;
                                    double eq  = ScoreConfig.EquipmentWeightSum(
                                                     r.EquipmentTypes.Select(et => et.Category?.Name ?? ""));
                                    return ScoringEngine.RompScore(act, eq);
                                })
                                .ToList();
                            double pmLoad = ScoringEngine.PmLoad(rompScores, activeSiteList.Count, p);
                            double effCap = ScoringEngine.EffectiveCapacity(p, pm.PmMultiplier);
                            double avail  = ScoringEngine.Availability(ScoringEngine.Utilization(pmLoad, effCap));
                            score += rule.Weight * avail * 5.0;
                            if (avail > 0)
                                reasons.Add($"{avail * 100:F0}% score capacity remaining");
                        }
                        else if (pm.Capacity > 0)
                        {
                            var avail = Math.Max(0, 1.0 - siteCnt / pm.Capacity);
                            score += rule.Weight * avail * 5.0;
                            if (avail > 0)
                                reasons.Add($"{avail * 100:F0}% capacity remaining");
                        }
                        break;
                }
            }

            raw.Add((pm, score, siteCnt, utilPct, reasons));
        }

        var ranked = raw.OrderByDescending(x => x.score).ThenBy(x => x.pm.LastName).ToList();

        Candidates = new ObservableCollection<PmCandidate>(
            ranked.Select((x, i) => new PmCandidate
            {
                Pm           = x.pm,
                Rank         = i + 1,
                Score        = x.score,
                ScoreText    = $"{x.score:F1}",
                CurrentSites = x.siteCnt,
                UtilText     = x.pm.Capacity > 0 ? $"{x.utilPct:F0}%" : "—",
                Reasons      = x.reasons.Count > 0 ? string.Join(" · ", x.reasons) : "No matching rules",
            }));
    }

    // ── Campus assignment actions ──

    public bool AssignCandidate(PmCandidate candidate) => DoAssign(candidate.Pm.Id);
    public bool AssignOverride()  => OverridePm != null && DoAssign(OverridePm.Id);
    public bool Unassign()        => DoAssign(null);

    /// <summary>
    /// Assigns (or clears) the selected cluster's own direct PM — see Location.AssignedPmId. Only
    /// shown when the cluster has no campuses for the per-campus assignment to apply to.
    /// </summary>
    public bool AssignPmToCluster(int? pmId)
    {
        if (SelectedLocation is not { } loc) return false;

        using var db = new AppDbContext();
        var tracked = db.Locations.Find(loc.Id);
        if (tracked == null) return false;
        if (pmId is { } checkPm) TeamRules.For(db).Ensure(checkPm, loc.Id);

        tracked.AssignedPmId = pmId;
        db.SaveChanges();

        var pmName = pmId.HasValue ? db.PMs.Find(pmId.Value)?.FullName ?? "PM" : null;
        AuditService.Log(pmId.HasValue ? "Assign" : "Unassign", "Location", tracked.Id,
            pmId.HasValue
                ? $"Assigned {pmName} directly to cluster \"{tracked.Name}\" (no campuses)"
                : $"Removed direct PM assignment from cluster \"{tracked.Name}\"");

        StatusMessage = pmId.HasValue
            ? $"{pmName} assigned directly to cluster \"{tracked.Name}\"."
            : $"PM unassigned from cluster \"{tracked.Name}\".";

        LoadLocations();
        SelectedLocation = Locations.FirstOrDefault(l => l.Id == loc.Id);
        return true;
    }

    private bool DoAssign(int? pmId)
    {
        if (_selectedCampusIds.Count == 0) return false;

        int campusCount = 0, sitesChanged = 0;
        foreach (var id in _selectedCampusIds.ToList())
        {
            sitesChanged += CampusService.AssignCampus(id, pmId);
            campusCount++;
        }

        var what = $"{campusCount} campus{(campusCount == 1 ? "" : "es")} ({sitesChanged} site{(sitesChanged == 1 ? "" : "s")} changed)";
        StatusMessage = pmId.HasValue ? $"PM assigned to {what}." : $"PM unassigned from {what}.";

        RefreshCampusAfterSiteChange();
        return true;
    }

    // ── Site-level override ──

    /// <summary>Gives the selected site its own PM (or, with null, no PM) regardless of its campus's PM.</summary>
    public bool AssignSiteOverride(int? pmId)
    {
        if (SelectedSite is not { } site) return false;

        using var db = new AppDbContext();
        var tracked = db.Sites.Include(s => s.Campus).FirstOrDefault(s => s.Id == site.Id);
        if (tracked == null) return false;
        AssignmentHistoryService.RecordChange(db, tracked, pmId);
        db.SaveChanges();

        var pmName = pmId is { } id ? db.PMs.Find(id)?.FullName ?? "PM" : "no PM";
        AuditService.Log(pmId == null ? "Unassign" : "Assign", "Site", tracked.Id,
            $"Site \"{tracked.Name}\" set to {pmName}{(tracked.PmOverride ? " (own PM, overrides campus)" : "")}");
        StatusMessage = tracked.PmOverride
            ? $"Site \"{tracked.Name}\" now has its own PM ({pmName})."
            : $"Site \"{tracked.Name}\" set to {pmName} — same as its campus.";

        RefreshCampusAfterSiteChange();
        return true;
    }

    /// <summary>Puts the selected site back on its campus's PM.</summary>
    public bool ClearSiteOverride()
    {
        if (SelectedSite is not { } site) return false;
        CampusService.ClearOverride(site.Id);
        StatusMessage = $"Site \"{site.Name}\" now follows its campus's PM.";
        RefreshCampusAfterSiteChange();
        return true;
    }

    /// <summary>Raised after an assignment reloads the campus list, carrying the campus Ids to
    /// re-select — the multi-selection lives in the page's ListBox.</summary>
    public event Action<IReadOnlySet<int>>? SelectionNeedsRestore;

    // Site PMs changed — reload the campus list (PMs, override counts), have the page re-select the
    // same campuses, then re-pick the same site and ROMP. Reloading the list clears the ListBox
    // selection (and with it SelectedCampus/SelectedSite), so the Ids are captured first.
    private void RefreshCampusAfterSiteChange()
    {
        var prevIds    = _selectedCampusIds.ToHashSet();
        var prevSiteId = SelectedSite?.Id;
        var prevRompId = SelectedRomp?.Id;
        LoadCampuses();
        SelectionNeedsRestore?.Invoke(prevIds);
        if (prevSiteId is { } sid && SelectedCampus != null)
        {
            SelectedSite = CampusSites.FirstOrDefault(s => s.Id == sid);
            if (prevRompId is { } rid) SelectedRomp = Romps.FirstOrDefault(r => r.Id == rid);
        }
        ComputeCandidates();
    }

    // ── Creating campuses / sites / ROMPs inline ──

    /// <summary>Creates (or finds) a campus by exact name in the selected cluster; returns its Id.</summary>
    public int? AddCampus(string name)
    {
        if (SelectedLocation is not { } loc || string.IsNullOrWhiteSpace(name)) return null;
        using var db = new AppDbContext();
        var campus = CampusService.GetOrCreate(db, loc.Id, name);
        var isNew = campus.Id == 0;
        db.SaveChanges();
        if (isNew) AuditService.Log("Create", "Campus", campus.Id, $"Created campus \"{campus.Name}\" in cluster \"{loc.Name}\"");
        LoadCampuses();
        return campus.Id;
    }

    /// <summary>Adds a site to the selected campus, inheriting the campus's PM.</summary>
    public void AddSiteToCampus(Site site)
    {
        if (SelectedCampus is not { } campus) return;
        using var db = new AppDbContext();
        site.CampusId     = campus.Id;
        site.CampusManual = true;
        site.LocationId   = campus.LocationId;
        site.AssignedPmId = null;
        db.Sites.Add(site);
        db.SaveChanges();

        var campusPm = db.Campuses.Where(c => c.Id == campus.Id).Select(c => c.AssignedPmId).FirstOrDefault();
        if (campusPm != null)
        {
            var tracked = db.Sites.Find(site.Id)!;
            AssignmentHistoryService.RecordChange(db, tracked, campusPm); // opens a history row
            db.SaveChanges();
        }
        AuditService.Log("Create", "Site", site.Id, $"Created site \"{site.Name}\" in campus \"{campus.Name}\"");

        RefreshCampusAfterSiteChange();
        SelectedSite = CampusSites.FirstOrDefault(s => s.Id == site.Id);
    }

    /// <summary>After a ROMP is added: show it selected under its site.</summary>
    public void SelectRomp(int siteId, int rompId)
    {
        if (SelectedSite?.Id == siteId) LoadRomps();
        else SelectedSite = CampusSites.FirstOrDefault(s => s.Id == siteId);
        SelectedRomp = Romps.FirstOrDefault(r => r.Id == rompId);
    }

    // ── ROMP-level assignment actions (independent override — see AssignmentResolver) ──

    public bool AssignRompCandidate(PM pm) => DoAssignRomp(pm.Id);
    public bool AssignRompOverride()       => RompOverridePm != null && DoAssignRomp(RompOverridePm.Id);
    public bool UnassignRomp()             => DoAssignRomp(null);

    private bool DoAssignRomp(int? pmId)
    {
        if (SelectedRomp is not { } romp) return false;

        using var db      = new AppDbContext();
        var       tracked = db.Romps.Find(romp.Id);
        if (tracked == null) return false;
        AssignmentHistoryService.RecordRompChange(db, tracked, pmId);
        db.SaveChanges();

        StatusMessage = pmId.HasValue
            ? $"PM assigned to ROMP \"{romp.Name}\" successfully."
            : $"PM override removed from ROMP \"{romp.Name}\" — it now follows its site's PM again.";

        LoadRomps();
        ComputeCandidates();
        return true;
    }

    /// <summary>Campus IDs the code-behind should mark selected in the ListBox.</summary>
    public IReadOnlySet<int> GetSelectedCampusIds() => _selectedCampusIds;
}
