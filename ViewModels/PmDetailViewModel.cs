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
    public Action<int>?   NavigateToSiteRomps { get; set; }   // called with SiteId when user clicks a site row

    private int _currentPmId;
    public  int CurrentPmId => _currentPmId;

    public void UnassignSite(int siteId)
    {
        using var db = new AppDbContext();
        var site = db.Sites.Find(siteId);
        if (site == null) return;
        site.AssignedPmId = null;
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
            if (site != null) site.AssignedPmId = null;
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
                            .Where(s => s.AssignedPmId == pmId)
                            .OrderBy(s => s.Name)
                            .ToList();

        var completeSet    = StatusConfig.Complete.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeSites    = sites.Where(s => s.SiteStatus != "Complete").ToList();
        var completedSites = sites.Where(s => s.SiteStatus == "Complete").ToList();

        ActiveSiteCount    = activeSites.Count;
        CompletedSiteCount = completedSites.Count;

        var allRomps    = sites.SelectMany(s => s.Romps).ToList();
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

        // Compute per-site scores for the sites table — active (non-complete) sites only
        Sites = new ObservableCollection<PmSiteRow>(
            activeSites.OrderBy(s => s.Name)
                 .Select(s =>
                 {
                     // Only count non-complete ROMPs; complete ROMs are excluded from this view
                     var activeRompsForSite = s.Romps
                         .Where(r => !completeSet.Contains(r.Status))
                         .ToList();
                     double siteScore = activeRompsForSite.Sum(r => RompScoreFor(r));
                     return new PmSiteRow
                     {
                         SiteId      = s.Id,
                         LocationId  = s.LocationId,
                         SiteName    = s.Name,
                         Location    = s.Location?.Name ?? "",
                         SiteStatus  = s.SiteStatus,
                         TotalRomps  = activeRompsForSite.Count,
                         ActiveRomps = activeRompsForSite.Count,
                         SiteScore   = siteScore,
                     };
                 }));

        // Overall PM load
        var rompScoresList = activeSites.SelectMany(s =>
            s.Romps.Where(r => !completeSet.Contains(r.Status)).Select(r => RompScoreFor(r))
        ).ToList();

        RompScoreSum      = rompScoresList.Sum();
        SiteFactorValue   = ScoringEngine.SiteFactor(activeSites.Count, p);
        PmLoad            = ScoringEngine.PmLoad(rompScoresList, activeSites.Count, p);
        DifficultyScore   = PmLoad;  // alias for legacy XAML binding
        EffectiveCapacity = ScoringEngine.EffectiveCapacity(p, pm.PmMultiplier);

        double util = ScoringEngine.Utilization(PmLoad, EffectiveCapacity);
        Availability = ScoringEngine.Availability(util);
        PmStatus     = ScoringEngine.PmStatus(util, p);

        bool useScore = new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Score";
        UtilizationPct = useScore
            ? util * 100.0
            : (Capacity > 0 ? ActiveSiteCount / Capacity * 100.0 : 0);

        // Per-status breakdown: show activity multiplier and actual total score per status group
        int maxCount = allRomps.Count > 0 ? allRomps.Count : 1;
        RompBreakdown = new ObservableCollection<PmStatusRow>(
            StatusConfig.All.Select(status =>
            {
                int    cnt        = allRomps.Count(r => r.Status == status);
                double actMult    = StatusConfig.ScoreWeights.TryGetValue(status, out var w) ? w : 0;
                // Actual score contribution: sum of romp_scores for active ROMPs in this status
                double statusScore = activeSites
                    .SelectMany(s => s.Romps.Where(r => r.Status == status
                                                     && !completeSet.Contains(r.Status)))
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
