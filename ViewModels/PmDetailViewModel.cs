using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

public class PmStatusRow
{
    public string Status        { get; init; } = "";
    public int    Count         { get; init; }
    public double ScoreWeight   { get; init; }  // activity multiplier (e.g. 1.0 for Construction)
    public double StatusScore   { get; init; }  // actual total score contribution from ROMPs in this status
    public bool   IsComplete    { get; init; }
    public double BarWidth      { get; init; }
}

public class PmSiteRow
{
    public int    SiteId      { get; init; }
    public int    LocationId  { get; init; }
    public string SiteName    { get; init; } = "";
    public string Location    { get; init; } = "";
    public string Campus      { get; init; } = "";
    public string SiteStatus  { get; init; } = "";
    public int    TotalRomps  { get; init; }
    public int    ActiveRomps { get; init; }
    public double SiteScore   { get; init; }

    // Mutable — used by the checkbox column for bulk-unassign selection
    public bool IsSelected { get; set; }
}

public partial class PmDetailViewModel : ObservableObject
{
    [ObservableProperty] private string _pmName           = "";
    [ObservableProperty] private string _homeOffice       = "";
    [ObservableProperty] private string _region           = "";
    [ObservableProperty] private string _dataName         = "";
    [ObservableProperty] private double _capacity;
    [ObservableProperty] private double _idealUtilization;
    [ObservableProperty] private double _utilizationPct;
    [ObservableProperty] private int    _activeSiteCount;
    [ObservableProperty] private int    _completedSiteCount;
    [ObservableProperty] private int    _activeRompCount;
    [ObservableProperty] private int    _totalRompCount;
    [ObservableProperty] private int?   _pmMultiplier;
    [ObservableProperty] private double _effectiveCapacity;

    // New formula results
    [ObservableProperty] private double _pmLoad;           // pm_load = site_factor × Σromp_scores
    [ObservableProperty] private double _rompScoreSum;     // Σromp_scores (before site factor)
    [ObservableProperty] private double _siteFactorValue;  // 1 + site_overhead × sites
    [ObservableProperty] private double _availability;     // max(0, 1 − utilization)
    [ObservableProperty] private string _pmStatus         = "Has Capacity";

    // Kept for XAML compatibility — both wired to PmLoad
    [ObservableProperty] private double _difficultyScore;

    [ObservableProperty] private ObservableCollection<PmStatusRow> _rompBreakdown = new();
    [ObservableProperty] private ObservableCollection<PmSiteRow>   _sites         = new();

    /// <summary>Site Count Only mode — hides every capacity/score figure on this page (see Configuration).</summary>
    [ObservableProperty] private bool _isCountMode;

    // Clusters assigned to this PM directly (Location.AssignedPmId — zero-campus clusters, see
    // AssignmentViewModel.AssignPmToCluster). They have no campus rows, so without this they'd
    // be invisible on this page. Empty string hides the line.
    [ObservableProperty] private string _directClusters = "";

    public string UtilizationText =>
        Capacity > 0 ? $"{UtilizationPct:F1}%" : "No capacity set";

    public double ScoreCapacity { get; private set; }

    public string CapacityText =>
        Capacity > 0 ? Capacity.ToString("0") : "—";

    public string ScoreCapacityText =>
        ScoreCapacity > 0 ? ScoreCapacity.ToString("0") : "—";

    public string IdealText =>
        IdealUtilization > 0 ? $"{IdealUtilization:F0}%" : "—";

    public string AvailabilityText =>
        $"{Availability * 100.0:F1}%";

    public string PmLoadText =>
        $"{PmLoad:F2}";

    public string PmMultiplierText =>
        PmMultiplier.HasValue ? PmMultiplier.Value.ToString() : "—";

    public string EffCapacityText =>
        $"{EffectiveCapacity:F1}";

    public Action?        NavigateBack       { get; set; }

    /// <summary>Back button text — depends on where PM Detail was opened from.</summary>
    [ObservableProperty] private string _backLabel = "← Back to Workload";
    public Action<int>?   NavigateToSiteRomps { get; set; }   // called with SiteId when user clicks a site row

    private int _currentPmId;
    public  int CurrentPmId => _currentPmId;

    public void UnassignSite(int siteId)
    {
        using var db = new AppDbContext();
        var site = db.Sites.Find(siteId);
        if (site == null) return;
        AssignmentHistoryService.RecordChange(db, site, null);
        db.SaveChanges();
        Load(_currentPmId);
    }

    public void BulkUnassignSites(IReadOnlyList<int> siteIds)
    {
        if (siteIds.Count == 0) return;
        using var db = new AppDbContext();
        foreach (var id in siteIds)
        {
            var site = db.Sites.Find(id);
            if (site != null) AssignmentHistoryService.RecordChange(db, site, null);
        }
        db.SaveChanges();
        Load(_currentPmId);
    }

    public void Load(int pmId)
    {
        _currentPmId = pmId;
        using var db = new AppDbContext();

        var pm = db.PMs.AsNoTracking().FirstOrDefault(p => p.Id == pmId);
        if (pm == null) return;

        PmName           = $"{pm.FirstName} {pm.LastName}".Trim();
        HomeOffice       = pm.HomeOffice;
        Region           = pm.GeographicalLocation;
        DataName         = pm.DataName;
        Capacity         = pm.Capacity;
        ScoreCapacity    = pm.ScoreCapacity;
        IdealUtilization = pm.IdealUtilization;
        PmMultiplier     = pm.PmMultiplier;

        var sites = db.Sites.AsNoTracking()
                            .Include(s => s.Romps)
                                .ThenInclude(r => r.EquipmentTypes)
                                    .ThenInclude(et => et.Category)
                            .Include(s => s.Location)
                            .Include(s => s.Campus)
                            .Where(s => s.AssignedPmId == pmId)
                            .OrderBy(s => s.Name)
                            .ToList();

        var directClusterNames = db.Locations.AsNoTracking()
                                   .Where(l => l.AssignedPmId == pmId)
                                   .OrderBy(l => l.Name)
                                   .Select(l => l.Name)
                                   .ToList();
        DirectClusters = directClusterNames.Count == 0 ? ""
            : $"Clusters assigned directly ({directClusterNames.Count}): {string.Join(", ", directClusterNames)}";

        var completeSet    = StatusConfig.Complete.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeSites    = sites.Where(s => s.SiteStatus != "Complete").ToList();
        var completedSites = sites.Where(s => s.SiteStatus == "Complete").ToList();

        ActiveSiteCount    = activeSites.Count;
        CompletedSiteCount = completedSites.Count;

        // ROMP-level override awareness (see AssignmentResolver): a ROMP under one of this PM's
        // own campuses but overridden to a DIFFERENT PM no longer counts here; conversely, a
        // ROMP under someone else's campus but overridden TO this PM does — even though its
        // campus never appears in the "Assigned Campuses" table below, since that table reflects
        // campus-level ownership, not individual ROMP credit.
        bool CountsHere(Romp r, int siteAssignedPmId) =>
            AssignmentResolver.EffectivePmId(r.AssignedPmId, siteAssignedPmId) == pmId;

        var orphanOverriddenRomps = db.Romps.AsNoTracking()
            .Include(r => r.EquipmentTypes).ThenInclude(et => et.Category)
            .Include(r => r.Site)
            .Where(r => r.AssignedPmId == pmId && r.Site.AssignedPmId != pmId)
            .ToList();

        var allRomps    = sites.SelectMany(s => s.Romps.Where(r => CountsHere(r, pmId)))
                                .Concat(orphanOverriddenRomps)
                                .ToList();
        var activeRomps = allRomps.Where(r => !completeSet.Contains(r.Status)).ToList();
        ActiveRompCount = activeRomps.Count;
        TotalRompCount  = allRomps.Count;

        var p = ScoreConfig.WorkloadParams;

        // Score each active ROMP: romp_score = activity_weight × equip_weight_sum
        double RompScoreFor(Romp r)
        {
            double activity = StatusConfig.ScoreWeights.TryGetValue(r.Status, out var aw) ? aw : 0;
            double equipSum = ScoreConfig.EquipmentWeightSum(r.EquipmentTypes.Select(et => et.Category.Name));
            return ScoringEngine.RompScore(activity, equipSum);
        }

        // Compute per-site scores for the sites table — active (non-complete) campuses only.
        // A ROMP overridden away to a different PM is excluded here too (CountsHere), so a
        // campus's displayed ROMP count/score only reflects what actually counts toward this PM.
        Sites = new ObservableCollection<PmSiteRow>(
            activeSites.OrderBy(s => s.Name)
                 .Select(s =>
                 {
                     var activeRompsForSite = s.Romps
                         .Where(r => !completeSet.Contains(r.Status) && CountsHere(r, pmId))
                         .ToList();
                     double siteScore = activeRompsForSite.Sum(r => RompScoreFor(r));
                     return new PmSiteRow
                     {
                         SiteId      = s.Id,
                         LocationId  = s.LocationId,
                         SiteName    = s.Name,
                         Location    = s.Location?.Name ?? "",
                         Campus      = s.Campus?.Name ?? "",
                         SiteStatus  = s.SiteStatus,
                         TotalRomps  = activeRompsForSite.Count,
                         ActiveRomps = activeRompsForSite.Count,
                         SiteScore   = siteScore,
                     };
                 }));

        // Overall PM load — activeRomps already reflects override attribution (built above,
        // includes orphanOverriddenRomps from campuses this PM doesn't otherwise own).
        var rompScoresList = activeRomps.Select(r => RompScoreFor(r)).ToList();

        RompScoreSum      = rompScoresList.Sum();
        SiteFactorValue   = ScoringEngine.SiteFactor(activeSites.Count, p);
        PmLoad            = ScoringEngine.PmLoad(rompScoresList, activeSites.Count, p);
        DifficultyScore   = PmLoad;  // alias for legacy XAML binding
        EffectiveCapacity = ScoringEngine.EffectiveCapacity(p, pm.PmMultiplier);

        double util = ScoringEngine.Utilization(PmLoad, EffectiveCapacity);
        Availability = ScoringEngine.Availability(util);
        PmStatus     = ScoringEngine.PmStatus(util, p);

        var pctMode = new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites");
        bool useScore = pctMode == "Score";
        IsCountMode   = pctMode == "Count";
        UtilizationPct = useScore
            ? util * 100.0
            : (Capacity > 0 ? ActiveSiteCount / Capacity * 100.0 : 0);

        // Per-status breakdown: show activity multiplier and actual total score per status group.
        // Selectable (not All) — a disabled status is meant to disappear from the app entirely
        // going forward, this breakdown included, not just from "assign a status" pickers.
        int maxCount = allRomps.Count > 0 ? allRomps.Count : 1;
        RompBreakdown = new ObservableCollection<PmStatusRow>(
            StatusConfig.Selectable.Select(status =>
            {
                int    cnt        = allRomps.Count(r => r.Status == status);
                double actMult    = StatusConfig.ScoreWeights.TryGetValue(status, out var w) ? w : 0;
                // Actual score contribution: sum of romp_scores for active ROMPs in this status
                // (activeRomps already reflects override attribution, orphans included)
                double statusScore = activeRomps
                    .Where(r => r.Status == status)
                    .Sum(r => RompScoreFor(r));
                return new PmStatusRow
                {
                    Status      = status,
                    Count       = cnt,
                    ScoreWeight = actMult,
                    StatusScore = statusScore,
                    IsComplete  = completeSet.Contains(status),
                    BarWidth    = cnt / (double)maxCount * 220.0,
                };
            }));

        OnPropertyChanged(nameof(UtilizationText));
        OnPropertyChanged(nameof(CapacityText));
        OnPropertyChanged(nameof(IdealText));
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(PmLoadText));
        OnPropertyChanged(nameof(PmMultiplierText));
        OnPropertyChanged(nameof(EffCapacityText));
    }

    public void GoBack() => NavigateBack?.Invoke();
}
