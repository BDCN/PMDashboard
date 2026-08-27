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

public partial class AssignmentViewModel : ObservableObject
{
    // ── Target selectors ──
    [ObservableProperty] private ObservableCollection<Location> _locations = new();
    [ObservableProperty] private ObservableCollection<Site>     _sites     = new();
    [ObservableProperty] private ObservableCollection<Romp>     _romps     = new();

    [ObservableProperty] private Location? _selectedLocation;
    [ObservableProperty] private Site?     _selectedSite;    // non-null only when exactly 1 site selected
    [ObservableProperty] private Romp?     _selectedRomp;

    // ── Filters ──
    [ObservableProperty] private bool _showUnassignedOnly;

    partial void OnShowUnassignedOnlyChanged(bool value)
    {
        if (SelectedLocation != null) LoadSites();
    }

    // ── Multi-select site tracking ──
    private readonly HashSet<int> _selectedSiteIds = new();

    [ObservableProperty] private int    _selectedSiteCount;
    [ObservableProperty] private string _siteSelectionInfo = "No sites selected";

    // Called by code-behind whenever the ListBox selection changes
    public void SetSelectedSites(IEnumerable<Site> sites)
    {
        _selectedSiteIds.Clear();
        var list = sites.ToList();
        foreach (var s in list) _selectedSiteIds.Add(s.Id);

        // Keep SelectedSite for the single-site "current assignment" panel
        SelectedSite = list.Count == 1 ? list[0] : null;

        UpdateSelectionInfo();
        if (_selectedSiteIds.Count > 0) LoadRomps();
        else Romps = new();
        ComputeCandidates();
    }

    private void UpdateSelectionInfo()
    {
        int count = _selectedSiteIds.Count;
        SelectedSiteCount = count;
        SiteSelectionInfo = count == 0 ? "No sites selected"
                          : count == 1 ? "1 site selected"
                          : $"{count} sites selected";
        BulkSelectionInfo = count > 1 ? $"{count} sites selected" : "";
        NotifyBulkProps();
    }

    // ── Override PM selector ──
    [ObservableProperty] private ObservableCollection<PM> _allPms  = new();
    [ObservableProperty] private PM?                      _overridePm;

    // ── PM candidates ──
    [ObservableProperty] private ObservableCollection<PmCandidate> _candidates    = new();
    [ObservableProperty] private int                               _activeRuleCount;

    // ── Status ──
    [ObservableProperty] private string _statusMessage      = "";
    [ObservableProperty] private string _bulkSelectionInfo  = "";

    // Computed properties used by XAML DataTriggers
    public bool IsRegularSiteSelected      => _selectedSiteIds.Count == 1;
    public bool IsBulkSelected             => _selectedSiteIds.Count > 1;
    public bool HasSelection               => _selectedSiteIds.Count > 0;
    // Kept for XAML backward-compat (bulk info panel styling)
    public bool IsBulkAllSelected          => _selectedSiteIds.Count > 1
                                              && _selectedSiteIds.Count == Sites.Count(s => s.Id > 0);
    public bool IsBulkUnassignedSelected   => _selectedSiteIds.Count > 1
                                              && Sites.Where(s => s.Id > 0 && _selectedSiteIds.Contains(s.Id))
                                                      .All(s => s.AssignedPmId == null);

    /// <summary>Geographic region of the selected location ("East", "West", or "").</summary>
    public string SelectedLocationGeo => SelectedLocation?.GeographicalLocation ?? "";

    // Cascade: location change → reload sites + clear selection
    partial void OnSelectedLocationChanged(Location? value)
    {
        ClearSelectionInternal();
        LoadSites();
        Candidates = new();
        BulkSelectionInfo = "";
        OnPropertyChanged(nameof(SelectedLocationGeo));
    }

    // Cascade: single-site change → reload romps (only when set to 1 selected site)
    partial void OnSelectedSiteChanged(Site? value)
    {
        if (value != null) LoadRomps();
        else Romps = new();
    }

    private void NotifyBulkProps()
    {
        OnPropertyChanged(nameof(IsRegularSiteSelected));
        OnPropertyChanged(nameof(IsBulkSelected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsBulkAllSelected));
        OnPropertyChanged(nameof(IsBulkUnassignedSelected));
    }

    private void ClearSelectionInternal()
    {
        _selectedSiteIds.Clear();
        SelectedSite  = null;
        SelectedSiteCount = 0;
        SiteSelectionInfo = "No sites selected";
        NotifyBulkProps();
    }

    // ── Public load ──

    public void Load()
    {
        LoadLocations();
        LoadAllPms();
        using var db = new AppDbContext();
        ActiveRuleCount = db.AssignmentRules.Count(r => r.IsActive);
    }

    private void LoadLocations()
    {
        using var db = new AppDbContext();
        var list = db.Locations.AsNoTracking().OrderBy(l => l.Name).ToList();
        Locations = new ObservableCollection<Location>(list);
    }

    private void LoadSites()
    {
        if (SelectedLocation == null) { Sites = new(); return; }

        var locId = SelectedLocation.Id;
        using var db = new AppDbContext();
        IQueryable<Site> query = db.Sites.AsNoTracking()
                                         .Include(s => s.AssignedPm)
                                         .Include(s => s.Romps)
                                         .Where(s => s.LocationId == locId);

        if (ShowUnassignedOnly)
            query = query.Where(s => s.AssignedPmId == null);

        Sites = new ObservableCollection<Site>(query.OrderBy(s => s.Name).ToList());
    }

    private void LoadRomps()
    {
        if (SelectedSite == null || SelectedSite.Id <= 0) { Romps = new(); return; }
        using var db = new AppDbContext();
        var list = db.Romps.AsNoTracking()
                           .Where(r => r.SiteId == SelectedSite.Id)
                           .OrderBy(r => r.Name)
                           .ToList();
        Romps = new ObservableCollection<Romp>(list);
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
        // Need at least one site selected AND a location to compute candidates
        if (_selectedSiteIds.Count == 0 || SelectedLocation == null) { Candidates = new(); return; }

        using var db = new AppDbContext();

        var rules = db.AssignmentRules.AsNoTracking()
                                      .Where(r => r.IsActive)
                                      .OrderBy(r => r.SortOrder)
                                      .ToList();
        ActiveRuleCount = rules.Count;
        if (rules.Count == 0) { Candidates = new(); return; }

        var pms = db.PMs.AsNoTracking().Where(p => p.IsActive).ToList();

        // Include romps + equipment types so CapacityAvailability can compute score-based load
        var sitesByPm = db.Sites.AsNoTracking()
                                .Include(s => s.Romps)
                                    .ThenInclude(r => r.EquipmentTypes)
                                        .ThenInclude(et => et.Category)
                                .Where(s => s.AssignedPmId != null)
                                .AsEnumerable()
                                .GroupBy(s => s.AssignedPmId!.Value)
                                .ToDictionary(g => g.Key, g => g.ToList());

        // Use SelectedLocation for region matching (works correctly for any number of selected sites)
        var locationGeo = SelectedLocation.GeographicalLocation;
        var locationId  = SelectedLocation.Id;

        bool useScore   = new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Score";
        var  p          = ScoreConfig.WorkloadParams;
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
                            reasons.Add($"{inSameLocation} site(s) already in this location");
                        }
                        break;

                    case "CapacityAvailability":
                        if (useScore)
                        {
                            // Score mode: compare pm_load against this PM's effective capacity
                            var activeSiteList = pmSites.Where(s => s.SiteStatus != "Complete").ToList();
                            var rompScores = activeSiteList
                                .SelectMany(s => s.Romps.Where(r => !completeSet.Contains(r.Status)))
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

    // ── Assignment actions ──

    public bool AssignCandidate(PmCandidate candidate) => DoAssign(candidate.Pm.Id);
    public bool AssignOverride()  => OverridePm != null && DoAssign(OverridePm.Id);
    public bool Unassign()        => DoAssign(null);

    private bool DoAssign(int? pmId)
    {
        if (_selectedSiteIds.Count == 0) return false;

        using var db   = new AppDbContext();
        var ids        = _selectedSiteIds.ToList();
        var sites      = db.Sites.Where(s => ids.Contains(s.Id)).ToList();
        foreach (var s in sites) s.AssignedPmId = pmId;
        db.SaveChanges();

        int n = sites.Count;
        StatusMessage = pmId.HasValue
            ? $"PM assigned to {n} site{(n == 1 ? "" : "s")} successfully."
            : $"PM unassigned from {n} site{(n == 1 ? "" : "s")}.";

        // Refresh site list; caller (code-behind) will restore ListBox selection
        LoadSites();
        ComputeCandidates();
        return true;
    }

    // ── Public helpers for code-behind quick-select buttons ──

    /// <summary>Returns IDs the code-behind should mark selected in the ListBox after a quick-select.</summary>
    public IReadOnlySet<int> GetSelectedSiteIds() => _selectedSiteIds;
}
