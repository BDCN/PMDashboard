using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;
using PMDashboard; // App.CloudSync

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
    /// <summary>OSA campus assignments (campus-level, no sites) — their own green segment so the
    /// PM doesn't read as idle; sized to about half the longest bar.</summary>
    public double OsaWidth        { get; init; }
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

/// <summary>One Card-view tile — a PM's cluster/campus/ROMP counts plus the breakdown of which
/// clusters they're assigned into. Built from the same sitesByPm/rompsByPm data as the chart
/// rows, so the two views can never disagree on the numbers.</summary>
public class PmCardRow
{
    public string PmName       { get; init; } = "";
    public string Region       { get; init; } = "";   // PM's own GeographicalLocation, for the Region sort
    public int    ClusterCount { get; init; }
    public int    CampusCount  { get; init; }   // sites (named before the Campus level existed)
    public int    RealCampusCount { get; init; } // distinct campuses those sites belong to
    public int    RompCount    { get; init; }
    public List<PmCardClusterRow> Clusters { get; init; } = new();

    public int  TotalAssignments => ClusterCount + CampusCount + RompCount;
    public bool HasNoAssignments => TotalAssignments == 0;
}

public class PmCardClusterRow
{
    public int    LocationId  { get; init; }
    public string ClusterName { get; init; } = "";
    public string Region      { get; init; } = "";
    public int    CampusCount { get; init; }
    public List<PmCardCampusRow> Campuses { get; init; } = new();

    // True for a cluster assigned directly via Location.AssignedPmId (the Assignment page's
    // "Assign PM to Cluster" action for a cluster with no campuses) rather than derived from any
    // actual campus assignment — CampusCount is always 0 for these.
    public bool IsDirectAssignment { get; init; }
}

public class PmCardCampusRow
{
    public string       CampusName { get; init; } = "";
    public List<string> RompNames  { get; init; } = new();

    // Comma-joined for display — a campus's ROMPs are shown as wrapped text rather than one
    // row each, since a single campus can carry dozens of ROMPs.
    public string RompNamesText => RompNames.Count > 0 ? string.Join(", ", RompNames) : "No active ROMPs";
}

public partial class WorkloadViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<PmWorkloadRow> _rows  = new();
    [ObservableProperty] private ObservableCollection<AxisTick>      _ticks = new();
    [ObservableProperty] private ObservableCollection<PmCardRow>     _cardRows = new();
    [ObservableProperty] private bool                                _isCardView;

    // Card view sort — re-sorts the already-built card list in place, no DB reload needed.
    public static readonly string[] CardSortOptions =
        ["Region", "Clusters", "Campuses", "Sites", "ROMPs", "Total Assignments"];
    [ObservableProperty] private string _cardSortBy = "Region";
    private List<PmCardRow> _unsortedCardRows = new();

    partial void OnCardSortByChanged(string value) => ApplyCardSort();

    private void ApplyCardSort()
    {
        IEnumerable<PmCardRow> sorted = CardSortBy switch
        {
            "Clusters"           => _unsortedCardRows.OrderByDescending(c => c.ClusterCount),
            "Campuses"           => _unsortedCardRows.OrderByDescending(c => c.RealCampusCount),
            "Sites"              => _unsortedCardRows.OrderByDescending(c => c.CampusCount),
            "ROMPs"              => _unsortedCardRows.OrderByDescending(c => c.RompCount),
            "Total Assignments"  => _unsortedCardRows.OrderByDescending(c => c.TotalAssignments),
            _                    => _unsortedCardRows
                                        .OrderBy(c => c.Region)
                                        .ThenBy(c => c.PmName),
        };
        CardRows = new ObservableCollection<PmCardRow>(sorted);
    }

    [ObservableProperty] private int    _pmCount;
    [ObservableProperty] private int    _totalClusters;
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
    [ObservableProperty] private bool   _isCountMode;

    private string _geoFilter = "All";

    /// <summary>Fired after a background-sync-triggered reload finishes, so the page can
    /// reapply the chart's actual current width (Load() always calls ApplyScale with a
    /// placeholder) and redraw the canvas background lines — neither of which happens from
    /// property-changed bindings alone. See the constructor subscription below.</summary>
    public event Action? DataRefreshed;

    public WorkloadViewModel()
    {
        // This VM is created once and held for the app's lifetime (MainViewModel field), so a
        // permanent subscription here — never unsubscribed — is the same convention MainWindow
        // uses for its own App.CloudSync.DataChanged subscription. Without this, a background
        // sync that pulls in someone else's new assignment (a cluster, a site, anything feeding
        // Load()'s queries) never reaches this page until the user navigates away and back, or
        // restarts the app — the page just goes stale and silently shows whatever it last loaded.
        App.CloudSync.DataChanged += OnCloudDataChanged;
    }

    private void OnCloudDataChanged(string _) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Load();
            DataRefreshed?.Invoke();
        });

    private record RawPmRow(
        PM Pm, int Sites, int Romps, int ActiveSites,
        double UtilPct, double IdealPct, double Capacity,
        IReadOnlyDictionary<string, int> RompsByStatus,
        double PmLoad,         // result of new formula: site_factor × Σromp_scores
        double Availability,   // max(0, 1 − utilization)
        string PmStatus,       // Overloaded / Near Full / Has Capacity
        double EffCapacity,    // target_capacity × skill_factor (score mode denominator)
        int Clusters,          // distinct clusters among this PM's assigned campuses
        IReadOnlyList<string> OsaCampuses);   // OSA-cluster campuses assigned to this PM (no sites)

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

        // Load non-complete sites with their ROMPs and each ROMP's equipment types — still the
        // source of campus-level stats (Active Campuses, Total Campuses, campus_factor), which
        // stay governed by Site.AssignedPmId regardless of any ROMP-level override below.
        var allSites = db.Sites.AsNoTracking()
                               .Include(s => s.Romps)
                                   .ThenInclude(r => r.EquipmentTypes)
                                       .ThenInclude(et => et.Category)
                               .Include(s => s.Location)
                               .Where(s => s.AssignedPmId != null && pmIdSet.Contains(s.AssignedPmId!.Value))
                               .AsEnumerable()
                               .Where(s => s.SiteStatus != "Complete")
                               .ToList();

        var sitesByPm = allSites.GroupBy(s => s.AssignedPmId!.Value)
                                .ToDictionary(g => g.Key, g => g.ToList());

        // Clusters assigned directly (Location.AssignedPmId — the Assignment page's "Assign PM
        // to Cluster" action, used for a cluster with no campuses for a per-Site assignment to
        // attach to). These contribute 0 campuses/ROMPs but must still count as a cluster —
        // without this, a PM whose only assignment is an empty cluster shows "0 clusters" here
        // despite having one on the Assignment page.
        // OSA: campuses in the OSA cluster are assigned at campus level and usually have no sites,
        // so site counts/scores show these PMs as idle. Collected separately and drawn as their
        // own green bar segment (see ApplyScale).
        var osaByPm = db.Campuses.AsNoTracking()
            .Where(c => c.AssignedPmId != null && c.Location.Name == "OSA")
            .Select(c => new { Pm = c.AssignedPmId!.Value, c.Name, c.LocationId })
            .AsEnumerable()
            .GroupBy(c => c.Pm)
            .ToDictionary(g => g.Key, g => (Names: g.Select(c => c.Name).OrderBy(n => n).ToList(), LocId: g.First().LocationId));

        var directClustersByPm = db.Locations.AsNoTracking()
            .Where(l => l.AssignedPmId != null && pmIdSet.Contains(l.AssignedPmId!.Value))
            .ToList()
            .GroupBy(l => l.AssignedPmId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // ROMP-level score/count attribution — respects a ROMP's own AssignedPmId override (see
        // AssignmentResolver). Most ROMPs are picked up via allSites above (their Campus's PM);
        // a second, narrower query catches ROMPs whose override points at a PM other than the
        // one their own Campus is assigned to (including a Campus with no PM assigned at all) —
        // allSites alone would never visit those ROMPs' Sites.
        var orphanOverriddenRomps = db.Romps.AsNoTracking()
            .Include(r => r.EquipmentTypes).ThenInclude(et => et.Category)
            .Include(r => r.Site)
            .Where(r => r.AssignedPmId != null && pmIdSet.Contains(r.AssignedPmId.Value)
                     && (r.Site.AssignedPmId == null || !pmIdSet.Contains(r.Site.AssignedPmId.Value)))
            .AsEnumerable()
            .Where(r => !completeSet.Contains(r.Status))
            .ToList();

        var rompsByPm = new Dictionary<int, List<Romp>>();
        void AddRomp(int pmId, Romp romp)
        {
            if (!rompsByPm.TryGetValue(pmId, out var list)) rompsByPm[pmId] = list = new List<Romp>();
            list.Add(romp);
        }
        foreach (var site in allSites)
            foreach (var romp in site.Romps.Where(r => !completeSet.Contains(r.Status)))
            {
                var effectivePmId = AssignmentResolver.EffectivePmId(romp.AssignedPmId, site.AssignedPmId);
                if (effectivePmId.HasValue && pmIdSet.Contains(effectivePmId.Value))
                    AddRomp(effectivePmId.Value, romp);
            }
        foreach (var romp in orphanOverriddenRomps)
            AddRomp(romp.AssignedPmId!.Value, romp);

        // ── Card view — same sitesByPm/rompsByPm data as the chart, just grouped by cluster
        // instead of flattened to a single count, so the two views can never disagree. ──
        _unsortedCardRows =
            pms.Select(pm =>
            {
                var pmSites = sitesByPm.TryGetValue(pm.Id, out var sl) ? sl : new List<Site>();
                var pmRomps = rompsByPm.TryGetValue(pm.Id, out var rl) ? rl : new List<Romp>();
                var clusters = pmSites
                    .GroupBy(s => s.LocationId)
                    .Select(g => new PmCardClusterRow
                    {
                        LocationId  = g.Key,
                        ClusterName = g.First().Location?.Name ?? "—",
                        Region      = g.First().Location?.GeographicalLocation ?? "",
                        CampusCount = g.Count(),
                        // Per-campus ROMPs, filtered to the ones that actually count toward
                        // THIS PM (a ROMP overridden to a different PM is excluded here the
                        // same way it's excluded from the top-level RompCount above).
                        Campuses = g.OrderBy(s => s.Name).Select(s => new PmCardCampusRow
                        {
                            CampusName = s.Name,
                            RompNames = s.Romps
                                .Where(r => !completeSet.Contains(r.Status)
                                         && AssignmentResolver.EffectivePmId(r.AssignedPmId, s.AssignedPmId) == pm.Id)
                                .OrderBy(r => r.Name)
                                .Select(r => r.Name)
                                .ToList(),
                        }).ToList(),
                    })
                    .ToList();

                // Clusters assigned directly (Location.AssignedPmId) with no campuses of their
                // own yet — add any not already covered by the campus-grouped list above, so a
                // PM whose only assignment is an empty cluster still shows it instead of nothing.
                var coveredLocationIds = clusters.Select(c => c.LocationId).ToHashSet();
                if (directClustersByPm.TryGetValue(pm.Id, out var directLocs))
                    foreach (var loc in directLocs.Where(l => !coveredLocationIds.Contains(l.Id)))
                        clusters.Add(new PmCardClusterRow
                        {
                            LocationId         = loc.Id,
                            ClusterName        = loc.Name,
                            Region             = loc.GeographicalLocation,
                            CampusCount        = 0,
                            IsDirectAssignment = true,
                        });

                clusters = clusters.OrderBy(c => c.ClusterName).ToList();

                return new PmCardRow
                {
                    PmName       = $"{pm.FirstName} {pm.LastName}".Trim(),
                    Region       = pm.GeographicalLocation ?? "",
                    ClusterCount = clusters.Count,
                    CampusCount  = pmSites.Count,
                    RealCampusCount = pmSites.Select(s => s.CampusId ?? -s.Id).Distinct().Count(),
                    RompCount    = pmRomps.Count,
                    Clusters     = clusters,
                };
            }).ToList();
        ApplyCardSort();

        var  pctMode  = new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites");
        bool useScore = pctMode == "Score";
        bool useCount = pctMode == "Count";
        IsScoreMode   = useScore;
        IsCountMode   = useCount;

        var p = ScoreConfig.WorkloadParams;

        _rawData = pms.Select(pm =>
        {
            var pmSites  = sitesByPm.TryGetValue(pm.Id, out var sl) ? sl : new List<Site>();
            var romps    = rompsByPm.TryGetValue(pm.Id, out var rl) ? rl : new List<Romp>();
            int active   = pmSites.Count(s => s.SiteStatus == "Active");
            var byStatus = romps.GroupBy(r => r.Status)
                                .ToDictionary(rg => rg.Key, rg => rg.Count())
                                as IReadOnlyDictionary<string, int>;

            // New scoring formula: romp_score = activity_weight × equip_weight_sum
            var rompScores = romps.Select(r =>
            {
                double activity  = StatusConfig.ScoreWeights.TryGetValue(r.Status, out var aw) ? aw : 0;
                double equipSum  = ScoreConfig.EquipmentWeightSum(
                                       r.EquipmentTypes.Select(et => et.Category.Name));
                return ScoringEngine.RompScore(activity, equipSum);
            }).ToList();

            // pm_load = campus_factor × Σromp_scores  (distinct non-complete campus count drives campus_factor)
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

            // Union with any cluster assigned directly (Location.AssignedPmId) and not already
            // covered by a real campus — see directClustersByPm's doc above.
            var clusterIds = pmSites.Select(s => s.LocationId).ToHashSet();
            if (directClustersByPm.TryGetValue(pm.Id, out var directLocs))
                foreach (var loc in directLocs) clusterIds.Add(loc.Id);
            var osa = osaByPm.TryGetValue(pm.Id, out var o) ? o.Names : new List<string>();
            if (osa.Count > 0) clusterIds.Add(o.LocId);
            int clusterCount = clusterIds.Count;

            return new RawPmRow(pm, pmSites.Count, romps.Count, active, util, ideal, cap,
                                byStatus, pmLoad, avail, pmStatus, effCap, clusterCount, osa);
        // Site Count Only mode: no capacity/utilization math exists to sort by, so order by
        // raw assigned-site count instead — fewest sites first, same "least loaded at top"
        // convention the other two modes already use.
        }).OrderBy(x => useCount ? (double)x.Sites : x.UtilPct).ToList();

        // ── KPI summary ──
        PmCount   = pms.Count;
        TotalClusters = sitesByPm.Values.SelectMany(sl => sl).Select(s => s.LocationId)
            .Concat(directClustersByPm.Values.SelectMany(ls => ls).Select(l => l.Id))
            .Distinct().Count();
        TotalSites = sitesByPm.Values.Sum(sl => sl.Count);
        TotalRomps = _rawData.Sum(r => r.Romps);

        if (useCount)
        {
            // The entire KPI panel is hidden in this mode (WorkloadPage.xaml) since none of
            // these numbers mean anything without a capacity figure to compare against.
            NoCapacityCount = 0;
            AvgUtilizationText = "–";
            OverloadedCount = NearFullCount = HasCapacityCount = 0;
            OverCapacityCount = OnTargetCount = UnderTargetCount = 0;
        }
        else
        {
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
        }

        ApplyScale(700);
    }

    public void ApplyScale(double availableWidth)
    {
        // Returning here without clearing Rows/Ticks left whatever was rendered for the
        // PREVIOUS filter still on screen — e.g. switching the sidebar's geo filter to a region
        // with zero matching PMs kept showing the last non-empty region's bars, since nothing
        // ever told the chart the list was now empty. PmCount/TotalSites/TotalRomps (set earlier
        // in Load(), independent of this method) correctly showed 0, which is what made it look
        // like a data bug rather than a stale render.
        if (_rawData.Count == 0)
        {
            Rows  = new ObservableCollection<PmWorkloadRow>();
            Ticks = new ObservableCollection<AxisTick>();
            HundredPctX = -1;
            return;
        }
        if (availableWidth <= 0) return;

        bool   countMode = IsCountMode;
        double scale;

        if (countMode)
        {
            // Scale by raw assigned-site count — there's no capacity/utilization figure in
            // this mode to compare bars against, so the "100%" reference concept doesn't apply.
            double maxCount = Math.Max(1, _rawData.Max(r => r.Sites));
            scale       = availableWidth / maxCount;
            HundredPctX = -1; // off-screen sentinel; WorkloadPage skips the reference line in this mode

            int tickMax  = (int)Math.Ceiling(maxCount);
            int tickStep = tickMax <= 10 ? 1 : (int)Math.Ceiling(tickMax / 10.0);
            var countTicks = new List<AxisTick>();
            for (int t = 0; t <= tickMax; t += tickStep)
                countTicks.Add(new AxisTick { Label = $"{t}", X = t * scale, IsMajor = true });
            Ticks = new ObservableCollection<AxisTick>(countTicks);
        }
        else
        {
            double maxUtil = Math.Max(100.0, _rawData.Max(r => r.UtilPct));
            scale       = availableWidth / maxUtil;
            HundredPctX = 100.0 * scale;

            double maxTick = Math.Ceiling(maxUtil / 10.0) * 10.0;
            var tickList   = new List<AxisTick>();
            for (double t = 0; t <= maxTick; t += 10)
            {
                bool major = t % 50 == 0;
                tickList.Add(new AxisTick { Label = major ? $"{(int)t}" : "", X = t * scale, IsMajor = major });
            }
            Ticks = new ObservableCollection<AxisTick>(tickList);
        }

        var workloadStatuses = StatusConfig.Workload;
        bool scoreMode     = IsScoreMode;
        double maxSiteCount = countMode ? Math.Max(1, _rawData.Max(r => r.Sites)) : 1;

        // Longest regular bar, for sizing OSA segments to about half of it.
        double BaseWidth(RawPmRow r) => countMode ? r.Sites * scale : Math.Max(0, r.UtilPct) * scale;
        double longest = Math.Max(1, _rawData.Max(BaseWidth));

        Rows = new ObservableCollection<PmWorkloadRow>(
            _rawData.Select(raw =>
            {
                bool   over      = raw.UtilPct > 100;
                double activePct = raw.Sites > 0 ? raw.ActiveSites / (double)raw.Sites * 100.0 : 0;
                string label;
                double barWidth    = 0;
                double fillFracPct;  // drives IsHighUtilization → inside-vs-outside label placement

                if (countMode)
                {
                    label = $"{raw.Clusters} cluster{(raw.Clusters == 1 ? "" : "s")} · {raw.Sites} site{(raw.Sites == 1 ? "" : "s")} · {raw.Romps} ROMP{(raw.Romps == 1 ? "" : "s")}";
                    barWidth    = raw.Sites * scale;
                    fillFracPct = raw.Sites / maxSiteCount * 100.0;
                }
                else if (scoreMode)
                {
                    // Lead with Score so it's visible even on short bars; avail & context follow
                    string warnFlag = over ? " ⚠" : "";
                    string availPct = $"{raw.Availability * 100.0:F1}%";
                    label = raw.Capacity > 0
                        ? $"Score: {raw.PmLoad:F2}{warnFlag}  ·  {raw.UtilPct:F1}% util  ·  {availPct} avail  ·  {raw.Clusters} clusters / {raw.Sites} sites / {raw.Romps} ROMPs"
                        : $"Score: {raw.PmLoad:F2}  ·  No capacity  ·  {raw.Clusters} clusters / {raw.Sites} sites / {raw.Romps} ROMPs";
                    fillFracPct = raw.UtilPct;
                }
                else
                {
                    string activeTxt = raw.Sites > 0 ? $" / {activePct:F0}% active" : "";
                    label = raw.Capacity > 0
                        ? $"{raw.UtilPct:F1}%{(over ? " ⚠" : "")} / {raw.Clusters} clusters / {raw.Sites} sites{activeTxt} / {raw.Romps} ROMPs"
                        : $"No capacity · {raw.Clusters} clusters · {raw.Sites} sites";
                    fillFracPct = raw.UtilPct;
                }

                // OSA campuses: green segment bringing the bar to ~50% of the longest (at least a
                // visible sliver when the PM's regular bar is already longer than that).
                bool hasOsa = raw.OsaCampuses.Count > 0;
                double osaWidth = hasOsa ? Math.Max(longest * 0.5 - BaseWidth(raw), longest * 0.08) : 0;
                if (hasOsa)
                {
                    label += $"  ·  OSA: {string.Join(", ", raw.OsaCampuses.Take(4))}" + (raw.OsaCampuses.Count > 4 ? $" +{raw.OsaCampuses.Count - 4}" : "");
                    fillFracPct = Math.Max(fillFracPct, (BaseWidth(raw) + osaWidth) / longest * 100.0);
                }

                var statusLines = workloadStatuses
                    .Select(s => $"{s}: {(raw.RompsByStatus.TryGetValue(s, out var c) ? c : 0)} ROMPs")
                    .ToList();
                string multText = raw.Pm.PmMultiplier.HasValue
                    ? $"×{raw.Pm.PmMultiplier} (skill factor {ScoringEngine.SkillFactor(raw.Pm.PmMultiplier, ScoreConfig.WorkloadParams):F2})"
                    : "not set (factor 1.00)";
                string tooltip = string.Join(Environment.NewLine, statusLines)
                               + Environment.NewLine + "─────────────────"
                               + Environment.NewLine + $"Clusters: {raw.Clusters}"
                               + (hasOsa ? Environment.NewLine + $"OSA campuses ({raw.OsaCampuses.Count}): {string.Join(", ", raw.OsaCampuses)}" : "")
                               + (countMode
                                   ? Environment.NewLine + $"Assigned sites: {raw.Sites}"
                                   + Environment.NewLine + $"Total ROMPs: {raw.Romps}"
                                   : scoreMode
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
                    UtilizationPct = fillFracPct,
                    IdealPct       = raw.IdealPct,
                    Capacity       = raw.Capacity,
                    PmLoad         = raw.PmLoad,
                    Availability   = raw.Availability,
                    PmStatus       = raw.PmStatus,
                    BlueWidth      = countMode ? barWidth : Math.Max(0, Math.Min(raw.UtilPct, raw.IdealPct)) * scale,
                    GreenWidth     = countMode ? 0 : (raw.UtilPct > raw.IdealPct
                                        ? Math.Max(0, Math.Min(raw.UtilPct, 100.0) - raw.IdealPct) * scale : 0),
                    RedWidth       = countMode ? 0 : Math.Max(0, raw.UtilPct - 100.0) * scale,
                    OsaWidth       = osaWidth,
                    Label          = label,
                    TooltipText    = tooltip,
                };
            }));
    }
}
