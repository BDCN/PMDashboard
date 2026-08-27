using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

public class PmWorkloadRow
{
    public int    PmId            { get; init; }
    public string PmName          { get; init; } = "";
    public int    SiteCount       { get; init; }
    public int    RompCount       { get; init; }
    public double UtilizationPct  { get; init; }   // 0–100+ (used for bar width)
    public double IdealPct        { get; init; }
    public double Capacity        { get; init; }
    public double PmLoad          { get; init; }   // pm_load from new formula
    public double Availability    { get; init; }   // max(0, 1 − utilization), 0..1
    public string PmStatus        { get; init; } = "Has Capacity"; // Overloaded / Near Full / Has Capacity
    public double BlueWidth       { get; init; }
    public double GreenWidth      { get; init; }
    public double RedWidth        { get; init; }
    public string Label           { get; init; } = "";
    public string TooltipText     { get; init; } = "";
    public bool   IsHighUtilization => UtilizationPct > 80;
}

public class AxisTick
{
    public string Label   { get; init; } = "";
    public double X       { get; init; }
    public bool   IsMajor { get; init; }
}

public partial class WorkloadViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<PmWorkloadRow> _rows  = new();
    [ObservableProperty] private ObservableCollection<AxisTick>      _ticks = new();

    [ObservableProperty] private int    _pmCount;
    [ObservableProperty] private int    _totalSites;
    [ObservableProperty] private int    _totalRomps;

    // Score-mode KPI counts (Overloaded / Near Full / Has Capacity)
    [ObservableProperty] private int    _overloadedCount;
    [ObservableProperty] private int    _nearFullCount;
    [ObservableProperty] private int    _hasCapacityCount;

    // Sites-mode KPI counts (Over Capacity / On Target / Under Target)
    [ObservableProperty] private int    _overCapacityCount;
    [ObservableProperty] private int    _onTargetCount;
    [ObservableProperty] private int    _underTargetCount;

    [ObservableProperty] private int    _noCapacityCount;
    [ObservableProperty] private string _avgUtilizationText = "–";
    [ObservableProperty] private double _hundredPctX;
    [ObservableProperty] private bool   _isScoreMode;

    private string _geoFilter = "All";

    private record RawPmRow(
        PM Pm, int Sites, int Romps, int ActiveSites,
        double UtilPct, double IdealPct, double Capacity,
        IReadOnlyDictionary<string, int> RompsByStatus,
        double PmLoad,         // result of new formula: site_factor × Σromp_scores
        double Availability,   // max(0, 1 − utilization)
        string PmStatus,       // Overloaded / Near Full / Has Capacity
        double EffCapacity);   // target_capacity × skill_factor (score mode denominator)

    private List<RawPmRow> _rawData = new();

    public void ApplyGeoFilter(string filter) { _geoFilter = filter; Load(); }

    public void Load()
    {
        using var db = new AppDbContext();

        IQueryable<PM> pmQuery = db.PMs.AsNoTracking().Where(p => p.IsActive);
        if (_geoFilter != "All")
            pmQuery = pmQuery.Where(p => p.GeographicalLocation == _geoFilter);
        var pms = pmQuery.OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ToList();

        var completeSet = StatusConfig.Complete.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pmIdSet     = pms.Select(p => p.Id).ToHashSet();

        // Load non-complete sites with their ROMPs and each ROMP's equipment types
        var allSites = db.Sites.AsNoTracking()
                               .Include(s => s.Romps)
                                   .ThenInclude(r => r.EquipmentTypes)
                                       .ThenInclude(et => et.Category)
                               .Where(s => s.AssignedPmId != null && pmIdSet.Contains(s.AssignedPmId!.Value))
                               .AsEnumerable()
                               .Where(s => s.SiteStatus != "Complete")
                               .ToList();

        var sitesByPm = allSites.GroupBy(s => s.AssignedPmId!.Value)
                                .ToDictionary(g => g.Key, g => g.ToList());

        bool useScore = new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Score";
        IsScoreMode   = useScore;

        var p = ScoreConfig.WorkloadParams;

        _rawData = pms.Select(pm =>
        {
            var pmSites  = sitesByPm.TryGetValue(pm.Id, out var sl) ? sl : new List<Site>();
            var romps    = pmSites.SelectMany(s => s.Romps.Where(r => !completeSet.Contains(r.Status))).ToList();
            int active   = pmSites.Count(s => s.SiteStatus == "Active");
            var byStatus = romps.GroupBy(r => r.Status)
                                .ToDictionary(rg => rg.Key, rg => rg.Count())
                                as IReadOnlyDictionary<string, int>;

            // New scoring formula: romp_score = activity_weight × equip_weight_sum
            var rompScores = pmSites.SelectMany(site =>
                site.Romps.Where(r => !completeSet.Contains(r.Status)).Select(r =>
                {
                    double activity  = StatusConfig.ScoreWeights.TryGetValue(r.Status, out var aw) ? aw : 0;
                    double equipSum  = ScoreConfig.EquipmentWeightSum(
                                           r.EquipmentTypes.Select(et => et.Category.Name));
                    return ScoringEngine.RompScore(activity, equipSum);
                })
            ).ToList();

            // pm_load = site_factor × Σromp_scores  (distinct non-complete site count drives site_factor)
            double pmLoad   = ScoringEngine.PmLoad(rompScores, pmSites.Count, p);
            // eff_capacity = target_capacity × skill_factor — scales denominator per PM experience
            double effCap   = ScoringEngine.EffectiveCapacity(p, pm.PmMultiplier);
            double util     = useScore
                                  ? ScoringEngine.Utilization(pmLoad, effCap) * 100.0
                                  : (pm.Capacity > 0 ? pmSites.Count / pm.Capacity * 100.0 : 0);
            double avail    = ScoringEngine.Availability(util / 100.0);
            string pmStatus = ScoringEngine.PmStatus(util / 100.0, p);
            double cap      = useScore ? effCap : pm.Capacity;
            double ideal    = pm.IdealUtilization > 0 ? pm.IdealUtilization : 80.0;

            return new RawPmRow(pm, pmSites.Count, romps.Count, active, util, ideal, cap,
                                byStatus, pmLoad, avail, pmStatus, effCap);
        }).OrderBy(x => x.UtilPct).ToList();

        // ── KPI summary ──
        PmCount   = pms.Count;
        TotalSites = sitesByPm.Values.Sum(sl => sl.Count);
        TotalRomps = _rawData.Sum(r => r.Romps);

        var withCap = _rawData.Where(r => r.Capacity > 0).ToList();
        NoCapacityCount    = _rawData.Count(r => r.Capacity == 0);
        AvgUtilizationText = withCap.Count > 0 ? $"{withCap.Average(r => r.UtilPct):F1}%" : "–";

        if (useScore)
        {
            // Threshold-based KPI counts for Score mode
            OverloadedCount   = _rawData.Count(r => r.Capacity > 0 && r.UtilPct / 100.0 > p.OverloadThreshold);
            NearFullCount     = _rawData.Count(r => r.Capacity > 0 && r.UtilPct / 100.0 >= p.NearFullThreshold
                                                                    && r.UtilPct / 100.0 <= p.OverloadThreshold);
            HasCapacityCount  = _rawData.Count(r => r.Capacity > 0 && r.UtilPct / 100.0 < p.NearFullThreshold);
            // Keep sites-mode counts at 0 so XAML DataTriggers collapse those cards
            OverCapacityCount = OnTargetCount = UnderTargetCount = 0;
        }
        else
        {
            // Classic sites-mode KPI counts
            OverCapacityCount = _rawData.Count(r => r.Capacity > 0 && r.UtilPct > 100);
            OnTargetCount     = _rawData.Count(r => r.Capacity > 0 && r.UtilPct >= r.IdealPct && r.UtilPct <= 100);
            UnderTargetCount  = _rawData.Count(r => r.Capacity > 0 && r.UtilPct < r.IdealPct);
            OverloadedCount = NearFullCount = HasCapacityCount = 0;
        }

        ApplyScale(700);
    }

    public void ApplyScale(double availableWidth)
    {
        if (_rawData.Count == 0 || availableWidth <= 0) return;

        double maxUtil = Math.Max(100.0, _rawData.Max(r => r.UtilPct));
        double scale   = availableWidth / maxUtil;
        HundredPctX    = 100.0 * scale;

        double maxTick = Math.Ceiling(maxUtil / 10.0) * 10.0;
        var tickList   = new List<AxisTick>();
        for (double t = 0; t <= maxTick; t += 10)
        {
            bool major = t % 50 == 0;
            tickList.Add(new AxisTick { Label = major ? $"{(int)t}" : "", X = t * scale, IsMajor = major });
        }
        Ticks = new ObservableCollection<AxisTick>(tickList);

        var workloadStatuses = StatusConfig.Workload;
        bool scoreMode       = IsScoreMode;

        Rows = new ObservableCollection<PmWorkloadRow>(
            _rawData.Select(raw =>
            {
                bool   over      = raw.UtilPct > 100;
                double activePct = raw.Sites > 0 ? raw.ActiveSites / (double)raw.Sites * 100.0 : 0;
                string label;

                if (scoreMode)
                {
                    // Lead with Score so it's visible even on short bars; avail & context follow
                    string warnFlag = over ? " ⚠" : "";
                    string availPct = $"{raw.Availability * 100.0:F1}%";
                    label = raw.Capacity > 0
                        ? $"Score: {raw.PmLoad:F2}{warnFlag}  ·  {raw.UtilPct:F1}% util  ·  {availPct} avail  ·  {raw.Sites} sites / {raw.Romps} ROMPs"
                        : $"Score: {raw.PmLoad:F2}  ·  No capacity  ·  {raw.Sites} sites / {raw.Romps} ROMPs";
                }
                else
                {
                    string activeTxt = raw.Sites > 0 ? $" / {activePct:F0}% active" : "";
                    label = raw.Capacity > 0
                        ? $"{raw.UtilPct:F1}%{(over ? " ⚠" : "")} / {raw.Sites} sites{activeTxt} / {raw.Romps} ROMPs"
                        : $"No capacity · {raw.Sites} sites";
                }

                var statusLines = workloadStatuses
                    .Select(s => $"{s}: {(raw.RompsByStatus.TryGetValue(s, out var c) ? c : 0)} ROMPs")
                    .ToList();
                string multText = raw.Pm.PmMultiplier.HasValue
                    ? $"×{raw.Pm.PmMultiplier} (skill factor {ScoringEngine.SkillFactor(raw.Pm.PmMultiplier, ScoreConfig.WorkloadParams):F2})"
                    : "not set (factor 1.00)";
                string tooltip = string.Join(Environment.NewLine, statusLines)
                               + Environment.NewLine + "─────────────────"
                               + (scoreMode
                                   ? Environment.NewLine + $"Score (PM Load): {raw.PmLoad:F2}"
                                   + Environment.NewLine + $"Eff. Capacity: {raw.EffCapacity:F1}"
                                   + Environment.NewLine + $"PM Multiplier: {multText}"
                                   + Environment.NewLine + $"Utilization: {raw.UtilPct:F1}%"
                                   + Environment.NewLine + $"Availability: {raw.Availability * 100.0:F1}%"
                                   + Environment.NewLine + $"Status: {raw.PmStatus}"
                                   : "");

                return new PmWorkloadRow
                {
                    PmId           = raw.Pm.Id,
                    PmName         = $"{raw.Pm.FirstName} {raw.Pm.LastName}".Trim(),
                    SiteCount      = raw.Sites,
                    RompCount      = raw.Romps,
                    UtilizationPct = raw.UtilPct,
                    IdealPct       = raw.IdealPct,
                    Capacity       = raw.Capacity,
                    PmLoad         = raw.PmLoad,
                    Availability   = raw.Availability,
                    PmStatus       = raw.PmStatus,
                    BlueWidth      = Math.Max(0, Math.Min(raw.UtilPct, raw.IdealPct)) * scale,
                    GreenWidth     = raw.UtilPct > raw.IdealPct
                                        ? Math.Max(0, Math.Min(raw.UtilPct, 100.0) - raw.IdealPct) * scale : 0,
                    RedWidth       = Math.Max(0, raw.UtilPct - 100.0) * scale,
                    Label          = label,
                    TooltipText    = tooltip,
                };
            }));
    }
}
