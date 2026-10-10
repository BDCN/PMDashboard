using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PMDashboard.Data;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

// ── Row types the page binds to ─────────────────────────────────────────────────────────────

public class PmScheduleRow
{
    public int    PmId        { get; init; }
    public string PmName      { get; init; } = "";
    public string Region      { get; init; } = "";
    public int    Campuses    { get; init; }
    public double LoadNow     { get; init; }
    public double PeakLoad    { get; init; }
    public double PeakPct     { get; init; }   // peak ÷ capacity, as a percentage
    public int    OverWeeks   { get; init; }
    public string NextOpening { get; init; } = "";
    public string Status      { get; init; } = "";   // Over capacity | Near capacity | Has room
    public bool   IsOver      => Status == "Over capacity";
    public bool   IsNear      => Status == "Near capacity";
}

public class PackageRow
{
    public string   Key        { get; init; } = "";
    public int?     SiteId     { get; init; }
    public string   Campus     { get; init; } = "";
    public string   Cluster    { get; init; } = "";
    public string   Region     { get; init; } = "";
    public string   RoomType   { get; init; } = "";
    public DateTime Nbd        { get; init; }
    public DateTime CxStart    { get; init; }
    public DateTime Pfho       { get; init; }
    public string   AssignedPm { get; init; } = "";
    public int      FitCount   { get; init; }
    public string   BestFit    { get; init; } = "";
    public bool     InSystem   => SiteId != null;
    public string   Window     => $"{Nbd:yyyy-MM-dd} → {Pfho:yyyy-MM-dd}";
}

public class OverlapRow
{
    public string   PmName   { get; init; } = "";
    public string   Region   { get; init; } = "";
    public DateTime From     { get; init; }
    public DateTime To       { get; init; }
    public int      Weeks    { get; init; }
    public double   Peak     { get; init; }
    public double   OverBy   { get; init; }
    public int      Count    { get; init; }
    public string   Campuses { get; init; } = "";
}

public class GapFitRow
{
    public int?     SiteId     { get; init; }
    public int      PmId       { get; init; }
    public string   PmName     { get; init; } = "";
    public string   Campus     { get; init; } = "";
    public string   Cluster    { get; init; } = "";
    public string   Region     { get; init; } = "";
    public string   RoomType   { get; init; } = "";
    public DateTime Nbd        { get; init; }
    public DateTime Pfho       { get; init; }
    public double   PeakAfter  { get; init; }
    public double   SpareAfter { get; init; }
    public bool     CanAssign  => SiteId != null;
}

public class HiringRow
{
    public string   Region     { get; init; } = "";
    public int      Count      { get; init; }
    public int      TotalShort { get; init; }
    public DateTime HireBy     { get; init; }
    public DateTime NeededBy   { get; init; }
    public bool     Overdue    { get; init; }
    public string   HireByText => Overdue ? $"{HireBy:yyyy-MM-dd} (overdue)" : $"{HireBy:yyyy-MM-dd}";
    public double   Load       { get; init; }
    public int      DriverCount { get; init; }
    public string   Drivers    { get; init; } = "";
    public string   OverloadedPms { get; init; } = "";
}

public class RegionSummary
{
    public string Region      { get; init; } = "";
    public int    PmsNow      { get; init; }
    public int    PeakNeeded  { get; init; }
    public int    MaxShort    { get; init; }
    public string NextHire    { get; init; } = "";
    public bool   NeedsHiring => MaxShort > 0;
}

public class MonthCell
{
    public string Label     { get; init; } = "";
    public int    Needed    { get; init; }
    public int    Available { get; init; }
    public int    Shortfall => Math.Max(0, Needed - Available);
    public string Text      => Shortfall > 0 ? $"{Needed} (+{Shortfall})" : Needed.ToString();
    public string Tooltip   { get; init; } = "";
    public string Background => Shortfall switch
    {
        0                => Needed > Available * 0.85 ? "#FEF3C7" : "#DCFCE7",
        1                => "#FECACA",
        2                => "#FCA5A5",
        _                => "#F87171",
    };
}

public class RegionMonthsRow
{
    public string          Region { get; init; } = "";
    public List<MonthCell> Cells  { get; init; } = new();
}

public class WeekBar
{
    public double Height  { get; init; }
    public string Fill    { get; init; } = "#3B82F6";
    public string Tooltip { get; init; } = "";
}

// ── ViewModel ───────────────────────────────────────────────────────────────────────────────

public partial class CoveragePlannerViewModel : ObservableObject
{
    private const double ChartHeight = 90;

    private CoveragePlan?               _plan;
    private CoverageDataService.Inputs? _inputs;
    private string _geoFilter = "All";

    [ObservableProperty] private string _settingsSummary = "";
    [ObservableProperty] private string _dataSummary     = "";
    [ObservableProperty] private string _statusMessage   = "";

    [ObservableProperty] private ObservableCollection<PmScheduleRow>   _schedules  = new();
    [ObservableProperty] private ObservableCollection<PackageRow>      _unassigned = new();
    [ObservableProperty] private ObservableCollection<CoverageDataService.UndatedSite> _undated = new();
    [ObservableProperty] private ObservableCollection<OverlapRow>      _overlaps   = new();
    [ObservableProperty] private ObservableCollection<GapFitRow>       _gapFits    = new();
    [ObservableProperty] private ObservableCollection<HiringRow>       _hiring     = new();
    [ObservableProperty] private ObservableCollection<RegionSummary>   _regionSummaries = new();
    [ObservableProperty] private ObservableCollection<RegionMonthsRow> _regionMonths    = new();
    [ObservableProperty] private List<string>                          _monthLabels     = new();

    // PM Schedules tab — detail for the selected PM
    [ObservableProperty] private PmScheduleRow? _selectedSchedule;
    [ObservableProperty] private ObservableCollection<PackageRow> _selectedPackages = new();
    [ObservableProperty] private ObservableCollection<WeekBar>    _selectedBars     = new();
    [ObservableProperty] private double _capacityLineY;   // from the chart's bottom
    [ObservableProperty] private string _chartScaleText = "";

    // Gap Fill tab — PM picker ("All PMs" = null)
    [ObservableProperty] private ObservableCollection<PmOption> _gapPmOptions = new();
    [ObservableProperty] private PmOption? _selectedGapPm;

    // Headline counts
    [ObservableProperty] private int _assignedCampusCount;
    [ObservableProperty] private int _unassignedCount;
    [ObservableProperty] private int _overloadedPmCount;
    [ObservableProperty] private int _hiresNeeded;

    public record PmOption(int? Id, string Label);

    /// <summary>Opens Configuration at the Coverage Planning card — set by MainViewModel.</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>The Configuration page is Super Admin only, so only they get the shortcut.</summary>
    public bool CanEditSettings => CurrentUser.IsSuperAdmin;

    /// <summary>Sets the region filter without rebuilding — for a caller about to Load() anyway.</summary>
    public void SetGeoFilter(string filter) => _geoFilter = filter;

    public void ApplyGeoFilter(string filter)
    {
        _geoFilter = filter;
        if (_plan == null) Load(); else Rebuild();
    }

    // ── Period filter (calendar quarters) ──

    /// <summary>A planning window: All = the configured horizon from today; the others are
    /// calendar-quarter windows. End is exclusive.</summary>
    public record CoveragePeriod(string Key, string Label, DateTime? Start, DateTime? End)
    {
        public override string ToString() => Label;
    }

    [ObservableProperty] private List<CoveragePeriod> _periods = new();
    [ObservableProperty] private CoveragePeriod? _selectedPeriod;
    [ObservableProperty] private string _currentQuarterText = "";
    [ObservableProperty] private string _periodText = "";

    partial void OnSelectedPeriodChanged(CoveragePeriod? value)
    {
        if (_inputs != null && value != null) Replan();
    }

    public static DateTime QuarterStart(DateTime d) => new(d.Year, (d.Month - 1) / 3 * 3 + 1, 1);
    public static string QuarterName(DateTime qStart) => $"Q{(qStart.Month - 1) / 3 + 1} {qStart.Year}";

    private void BuildPeriods(DateTime today)
    {
        var cur  = QuarterStart(today);
        var next = cur.AddMonths(3);
        string Range(DateTime from, int quarters) =>
            quarters == 1 ? QuarterName(from)
            : from.Year == from.AddMonths(3 * (quarters - 1)).Year
                ? $"Q{(from.Month - 1) / 3 + 1}–{QuarterName(from.AddMonths(3 * (quarters - 1)))}"
                : $"{QuarterName(from)} – {QuarterName(from.AddMonths(3 * (quarters - 1)))}";
        var list = new List<CoveragePeriod>
        {
            new("all",  "All (full horizon)", null, null),
            new("cur",  $"Current quarter ({QuarterName(cur)})", today.Date, next),
            new("n1",   $"Next quarter ({QuarterName(next)})", next, next.AddMonths(3)),
        };
        for (int n = 2; n <= 4; n++)
            list.Add(new($"n{n}", $"Next {n} quarters ({Range(next, n)})", next, next.AddMonths(3 * n)));
        var prevKey = SelectedPeriod?.Key ?? "all";
        Periods = list;
        // Set the field directly — Load() replans once below rather than per property change.
#pragma warning disable MVVMTK0034
        _selectedPeriod = list.FirstOrDefault(p => p.Key == prevKey) ?? list[0];
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedPeriod));

        int daysLeft = (next - today.Date).Days;
        CurrentQuarterText = $"Current quarter: {QuarterName(cur)}  ·  {cur:MMM d} – {next.AddDays(-1):MMM d, yyyy}  ·  {daysLeft} day{(daysLeft == 1 ? "" : "s")} left";
    }

    /// <summary>The model settings narrowed to the selected period.</summary>
    private PlannerSettings SettingsForPeriod(PlannerSettings s)
    {
        if (SelectedPeriod is not { Start: { } start, End: { } end }) return s;
        var first = CoveragePlanner.WeekStart(start);
        int weeks = Math.Max(1, (int)Math.Ceiling((end - first).TotalDays / 7.0));
        return s with { StartDate = start, HorizonWeeks = weeks };
    }

    private void Replan()
    {
        if (_inputs is not { } inp) return;
        var s = SettingsForPeriod(inp.Settings);
        _plan = CoveragePlanner.Plan(inp.Packages, inp.Pms, s);
        PeriodText = SelectedPeriod is { Start: { } a, End: { } b }
            ? $"Showing {SelectedPeriod.Label}: {a:MMM d} – {b.AddDays(-1):MMM d, yyyy}"
            : $"Showing all: {inp.Settings.HorizonWeeks} weeks from {_plan.WeekStarts[0]:MMM d, yyyy}";
        Rebuild();
    }

    public void Load()
    {
        try
        {
            _inputs = CoverageDataService.Load();
            BuildPeriods(_inputs.Settings.Today);
            _plan   = CoveragePlanner.Plan(_inputs.Packages, _inputs.Pms, SettingsForPeriod(_inputs.Settings));
            var s   = _inputs.Settings;
            SettingsSummary =
                $"Capacity {s.CapacityPerPm:0.##} per PM  ·  build {s.BuildWeight:0.##}/day  ·  CX {s.CxWeight:0.##}/day  ·  " +
                $"CX window {s.CxWindowDays} days  ·  hire {s.HireLeadDays} days ahead  ·  horizon {s.HorizonWeeks} weeks";
            PeriodText = SelectedPeriod is { Start: { } a, End: { } b }
                ? $"Showing {SelectedPeriod.Label}: {a:MMM d} – {b.AddDays(-1):MMM d, yyyy}"
                : $"Showing all: {s.HorizonWeeks} weeks from {_plan.WeekStarts[0]:MMM d, yyyy}";
            DataSummary = _inputs.SkippedRows > 0
                ? $"{_inputs.Packages.Count} site windows from the forecast  ·  {_inputs.SkippedRows:N0} forecast rows skipped (no usable NBD/PFHO)"
                : $"{_inputs.Packages.Count} site windows from the forecast";
            StatusMessage = "";
            Rebuild();
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not build the coverage plan: " + ex.Message;
        }
    }

    private bool InRegion(string region) =>
        _geoFilter == "All" || string.Equals(region, _geoFilter, StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        if (_plan is not { } plan || _inputs is not { } inp) return;
        var s     = inp.Settings;
        var today = s.Today;
        var pmName = inp.Pms.ToDictionary(p => p.Id, p => p.Name);

        // Who could take each unassigned package (for the Unassigned tab's columns).
        var fitsByPkg = plan.GapFits.GroupBy(f => f.Package.Key)
                                    .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.SpareAfter).ToList());

        PackageRow ToRow(WorkPackage p) => new()
        {
            Key = p.Key, SiteId = p.SiteId, Campus = p.Campus, Cluster = p.Cluster, Region = p.Region,
            RoomType = p.RoomType, Nbd = p.Nbd, CxStart = CoveragePlanner.CxStart(p, s.CxWindowDays), Pfho = p.Pfho,
            AssignedPm = p.AssignedPmId is { } id && pmName.TryGetValue(id, out var n) ? n : "",
            FitCount = fitsByPkg.TryGetValue(p.Key, out var f) ? f.Count : 0,
            BestFit  = fitsByPkg.TryGetValue(p.Key, out var f2) && f2.Count > 0 ? f2[0].Pm.Name : "",
        };

        // ── PM schedules ──
        var prevSelected = SelectedSchedule?.PmId;
        Schedules = new(plan.Schedules
            .Where(x => InRegion(x.Pm.Region))
            .Select(x =>
            {
                int over = x.WeeklyLoad.Count(v => v > s.CapacityPerPm + 1e-9);
                int openIdx = Array.FindIndex(x.WeeklyLoad, v => v <= s.CapacityPerPm - 1.0 + 1e-9);
                return new PmScheduleRow
                {
                    PmId = x.Pm.Id, PmName = x.Pm.Name, Region = x.Pm.Region,
                    Campuses = x.Packages.Select(p => p.Campus).Distinct().Count(),
                    LoadNow = Math.Round(x.CurrentLoad, 2), PeakLoad = Math.Round(x.PeakLoad, 2),
                    PeakPct = Math.Round(100 * x.PeakLoad / s.CapacityPerPm),
                    OverWeeks = over,
                    NextOpening = openIdx < 0 ? "none in horizon" : openIdx == 0 ? "now" : $"{plan.WeekStarts[openIdx]:yyyy-MM-dd}",
                    Status = x.PeakLoad > s.CapacityPerPm + 1e-9 ? "Over capacity"
                           : x.PeakLoad >= 0.85 * s.CapacityPerPm ? "Near capacity" : "Has room",
                };
            })
            .OrderByDescending(r => r.PeakPct));
        SelectedSchedule = Schedules.FirstOrDefault(r => r.PmId == prevSelected) ?? Schedules.FirstOrDefault();

        // ── Unassigned ──
        Unassigned = new(plan.UnassignedPackages.Where(p => InRegion(p.Region)).Select(ToRow));
        Undated    = new(inp.UnassignedWithoutForecast.Where(u => InRegion(u.Region)));

        // ── Overlaps ──
        Overlaps = new(plan.Overloads
            .Where(o => InRegion(o.Pm.Region))
            .OrderBy(o => o.From).ThenByDescending(o => o.PeakLoad)
            .Select(o => new OverlapRow
            {
                PmName = o.Pm.Name, Region = o.Pm.Region, From = o.From, To = o.To.AddDays(-1),
                Weeks = (int)Math.Round((o.To - o.From).TotalDays / 7), Peak = Math.Round(o.PeakLoad, 2),
                OverBy = Math.Round(o.PeakLoad - s.CapacityPerPm, 2), Count = o.Packages.Count,
                Campuses = string.Join(", ", o.Packages.Select(p => p.Campus).Distinct().Take(12))
                           + (o.Packages.Select(p => p.Campus).Distinct().Count() > 12 ? ", …" : ""),
            }));

        // ── Gap fill ──
        var prevGap = SelectedGapPm?.Id;
        GapPmOptions = new(new[] { new PmOption(null, "All PMs") }
            .Concat(inp.Pms.Where(p => InRegion(p.Region)).Select(p => new PmOption(p.Id,
                $"{p.Name} ({(string.IsNullOrEmpty(p.SubRegion) ? p.Region : p.SubRegion)})"))));
        _suppressGapRebuild = true; // one rebuild below, not one per property change
        SelectedGapPm = GapPmOptions.FirstOrDefault(o => o.Id == prevGap) ?? GapPmOptions[0];
        _suppressGapRebuild = false;
        RebuildGapFits();

        // ── Hiring ──
        // Hiring is per area (a sub-region, or a region for clusters with no sub-region yet);
        // the sidebar region filter applies to the area's parent region.
        var areaOfPm = plan.PmAreas ?? new Dictionary<int, string>();
        var overloadsByRegion = plan.Overloads.ToLookup(o => areaOfPm.GetValueOrDefault(o.Pm.Id, o.Pm.Region), StringComparer.OrdinalIgnoreCase);
        Hiring = new(plan.Hiring
            .Where(h => InRegion(h.ParentRegion))
            .Select(h => new HiringRow
            {
                Region = h.Region, Count = h.Count, TotalShort = h.TotalShort,
                HireBy = h.HireBy, NeededBy = h.NeededBy, Overdue = h.HireBy < today,
                Load = Math.Round(h.PeakLoad, 1), DriverCount = h.UnassignedDrivers.Count,
                Drivers = string.Join(", ", h.UnassignedDrivers.Select(p => p.Campus).Distinct().Take(15))
                          + (h.UnassignedDrivers.Select(p => p.Campus).Distinct().Count() > 15 ? ", …" : ""),
                OverloadedPms = string.Join(", ", overloadsByRegion[h.Region]
                    .Where(o => o.From <= h.NeededBy && o.To > h.NeededBy)
                    .Select(o => o.Pm.Name).Distinct()),
            }));

        var monthsShown = plan.RegionMonths.Select(m => m.Month).Distinct().OrderBy(m => m).ToList();
        MonthLabels = monthsShown.Select(m => m.ToString("MMM yy")).ToList();
        var areaOrder = plan.RegionMonths.Select(m => m.Region).Distinct().ToList(); // planner's order: region, then its sub-regions
        RegionMonths = new(plan.RegionMonths
            .Where(m => InRegion(m.ParentRegion))
            .GroupBy(m => m.Region)
            .OrderBy(g => areaOrder.IndexOf(g.Key))
            .Select(g => new RegionMonthsRow
            {
                Region = g.Key,
                Cells = monthsShown.Select(month =>
                {
                    var m = g.FirstOrDefault(x => x.Month == month);
                    return m == null
                        ? new MonthCell { Label = month.ToString("MMM yy") }
                        : new MonthCell
                        {
                            Label = month.ToString("MMM yy"), Needed = m.PmsNeeded, Available = m.PmsAvailable,
                            Tooltip = $"{g.Key}, {month:MMMM yyyy}\nPeak load {m.PeakLoad:0.0} ÷ capacity {s.CapacityPerPm:0.##} = {m.PmsNeeded} PMs needed\n" +
                                      $"{m.PmsAvailable} PMs today" + (m.Shortfall > 0 ? $" → short {m.Shortfall}" : ""),
                        };
                }).ToList(),
            }));

        RegionSummaries = new(RegionMonths.Select(r =>
        {
            var next = Hiring.Where(h => h.Region == r.Region).OrderBy(h => h.HireBy).FirstOrDefault();
            int peakNeeded = r.Cells.Count == 0 ? 0 : r.Cells.Max(c => c.Needed);
            int available  = r.Cells.Select(c => c.Available).DefaultIfEmpty(0).Max();
            return new RegionSummary
            {
                Region = r.Region, PmsNow = available, PeakNeeded = peakNeeded,
                MaxShort = Math.Max(0, peakNeeded - available),
                NextHire = next == null ? "No hiring needed in horizon"
                         : $"Hire {next.Count} by {next.HireBy:MMM d, yyyy}" + (next.Overdue ? " — overdue" : ""),
            };
        }));

        AssignedCampusCount = Schedules.Sum(x => x.Campuses);
        UnassignedCount     = Unassigned.Count + Undated.Count;
        OverloadedPmCount   = Schedules.Count(x => x.IsOver);
        HiresNeeded         = RegionSummaries.Sum(r => r.MaxShort);
    }

    partial void OnSelectedScheduleChanged(PmScheduleRow? value) => RebuildSelectedDetail();

    private void RebuildSelectedDetail()
    {
        if (_plan is not { } plan || _inputs is not { } inp || SelectedSchedule is not { } row)
        {
            SelectedPackages = new();
            SelectedBars = new();
            return;
        }
        var s   = inp.Settings;
        var sch = plan.Schedules.First(x => x.Pm.Id == row.PmId);

        SelectedPackages = new(sch.Packages.OrderBy(p => p.Pfho).Select(p => new PackageRow
        {
            Key = p.Key, SiteId = p.SiteId, Campus = p.Campus, Cluster = p.Cluster, Region = p.Region,
            RoomType = p.RoomType, Nbd = p.Nbd, CxStart = CoveragePlanner.CxStart(p, s.CxWindowDays), Pfho = p.Pfho,
        }));

        double scale = Math.Max(s.CapacityPerPm * 1.5, sch.WeeklyLoad.DefaultIfEmpty(0).Max());
        SelectedBars = new(sch.WeeklyLoad.Select((v, i) => new WeekBar
        {
            Height  = Math.Max(1, ChartHeight * v / scale),
            Fill    = v > s.CapacityPerPm + 1e-9 ? "#EF4444" : v >= 0.85 * s.CapacityPerPm ? "#F59E0B" : "#3B82F6",
            Tooltip = $"Week of {plan.WeekStarts[i]:MMM d, yyyy}\nLoad {v:0.00} of {s.CapacityPerPm:0.##}" +
                      (v > s.CapacityPerPm + 1e-9 ? "  — over capacity" : $"  — {s.CapacityPerPm - v:0.00} spare"),
        }));
        CapacityLineY  = ChartHeight * s.CapacityPerPm / scale;
        ChartScaleText = $"{plan.WeekStarts[0]:MMM yyyy} → {plan.WeekStarts[^1]:MMM yyyy}  ·  red line = capacity {s.CapacityPerPm:0.##}";
    }

    private bool _suppressGapRebuild;
    partial void OnSelectedGapPmChanged(PmOption? value) { if (!_suppressGapRebuild) RebuildGapFits(); }

    private void RebuildGapFits()
    {
        if (_plan is not { } plan) return;
        var pmId = SelectedGapPm?.Id;
        GapFits = new(plan.GapFits
            .Where(f => InRegion(f.Pm.Region) && (pmId == null || f.Pm.Id == pmId))
            .Select(f => new GapFitRow
            {
                SiteId = f.Package.SiteId, PmId = f.Pm.Id, PmName = f.Pm.Name,
                Campus = f.Package.Campus, Cluster = f.Package.Cluster, Region = f.Package.Region,
                RoomType = f.Package.RoomType, Nbd = f.Package.Nbd, Pfho = f.Package.Pfho,
                PeakAfter = Math.Round(f.PeakLoadAfter, 2), SpareAfter = Math.Round(f.SpareAfter, 2),
            }));
    }

    /// <summary>Assigns the gap-fill row's campus to its PM (whole campus, like the Campuses
    /// page), with assignment history + audit, then recalculates the plan.</summary>
    public bool AssignFromGap(GapFitRow row)
    {
        if (row.SiteId is not { } siteId) return false;
        using (var db = new AppDbContext())
        {
            var site = db.Sites.Find(siteId);
            if (site == null) return false;
            AssignmentHistoryService.RecordChange(db, site, row.PmId);
            db.SaveChanges();
        }
        AuditService.Log("Assign", "Site", siteId, $"Assigned {row.PmName} to site \"{row.Campus}\" from Coverage Planner gap fill");
        Load();
        StatusMessage = $"{row.PmName} assigned to {row.Campus}.";
        return true;
    }
}
