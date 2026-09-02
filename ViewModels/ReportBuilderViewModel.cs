using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

public enum ReportMode { Orders, SitesRomps }

public class ChartBar
{
    public string Label     { get; init; } = "";
    public double Value     { get; init; }
    public string ValueText { get; init; } = "";
    public double BarWidth  { get; set; }
}

public partial class ReportBuilderViewModel : ObservableObject
{
    [ObservableProperty] private ReportMode _mode = ReportMode.Orders;

    [ObservableProperty] private ObservableCollection<ReportField> _availableFields    = new();
    [ObservableProperty] private ObservableCollection<string>      _selectedColumnKeys = new();

    // ── Filter source lists ──
    [ObservableProperty] private ObservableCollection<Location>    _allLocations     = new();
    [ObservableProperty] private ObservableCollection<Site>        _allSites         = new();
    [ObservableProperty] private ObservableCollection<PM>          _allPms           = new();
    [ObservableProperty] private ObservableCollection<string>      _allRompStatuses  = new();
    [ObservableProperty] private ObservableCollection<string>      _allOrderStatuses = new();
    [ObservableProperty] private ObservableCollection<ReportField> _dateFieldOptions = new();

    // ── Filter values ──
    [ObservableProperty] private DateTime?   _dateFrom;
    [ObservableProperty] private DateTime?   _dateTo;
    [ObservableProperty] private ReportField? _selectedDateField;
    [ObservableProperty] private string      _textSearch = "";

    private readonly HashSet<int>    _selectedLocationIds   = new();
    private readonly HashSet<int>    _selectedSiteIds       = new();
    private readonly HashSet<int>    _selectedPmIds         = new();
    private readonly HashSet<string> _selectedRompStatuses  = new();
    private readonly HashSet<string> _selectedOrderStatuses = new();

    [ObservableProperty] private string _locationSelectionInfo    = "All locations";
    [ObservableProperty] private string _siteSelectionInfo        = "All sites";
    [ObservableProperty] private string _pmSelectionInfo          = "All PMs";
    [ObservableProperty] private string _rompStatusSelectionInfo  = "All statuses";
    [ObservableProperty] private string _orderStatusSelectionInfo = "All statuses";

    // ── Results ──
    [ObservableProperty] private ObservableCollection<ReportRow> _results = new();
    [ObservableProperty] private int    _resultCount;
    [ObservableProperty] private string _statusMessage = "";

    // ── Saved reports ──
    [ObservableProperty] private ObservableCollection<SavedReport> _savedReports = new();
    [ObservableProperty] private SavedReport? _selectedSavedReport;

    // ── Chart ──
    [ObservableProperty] private ObservableCollection<ReportField> _chartAggregateOptions = new();
    [ObservableProperty] private ReportField? _chartGroupByField;
    [ObservableProperty] private ReportField? _chartAggregateField;   // null → Count
    [ObservableProperty] private string       _chartAggregateOp = "Count"; // "Count" | "Sum"
    [ObservableProperty] private ObservableCollection<ChartBar> _chartData = new();

    private double _lastChartWidth = 500;

    public bool IsOrdersMode    => Mode == ReportMode.Orders;
    public bool IsSitesRompMode => Mode == ReportMode.SitesRomps;

    partial void OnModeChanged(ReportMode value)
    {
        OnPropertyChanged(nameof(IsOrdersMode));
        OnPropertyChanged(nameof(IsSitesRompMode));
        ClearFiltersInternal();
        InitializeCatalogForMode();
    }

    partial void OnChartGroupByFieldChanged(ReportField? value)     => RebuildChart();
    partial void OnChartAggregateFieldChanged(ReportField? value)   => RebuildChart();
    partial void OnChartAggregateOpChanged(string value)            => RebuildChart();

    // ── Load ──

    public void Load()
    {
        LoadFilterSources();
        LoadSavedReports();
        InitializeCatalogForMode();
    }

    private void InitializeCatalogForMode()
    {
        AvailableFields = new ObservableCollection<ReportField>(
            Mode == ReportMode.Orders ? ReportFieldCatalog.OrderFields : ReportFieldCatalog.SiteRompFields);
        SelectedColumnKeys = new ObservableCollection<string>(AvailableFields.Take(6).Select(f => f.Key));

        DateFieldOptions = new ObservableCollection<ReportField>(
            Mode == ReportMode.Orders
                ? ReportFieldCatalog.OrderDateFields
                : ReportFieldCatalog.SiteRompFields.Where(f => f.Key == "LastModified"));
        SelectedDateField = DateFieldOptions.FirstOrDefault();

        ChartAggregateOptions = new ObservableCollection<ReportField>(
            AvailableFields.Where(f => f.FieldType == ReportFieldType.Number));
        ChartGroupByField   = AvailableFields.FirstOrDefault(f => f.FieldType == ReportFieldType.Text);
        ChartAggregateField = null;
        ChartAggregateOp    = "Count";

        Results    = new ObservableCollection<ReportRow>();
        ChartData  = new ObservableCollection<ChartBar>();
        ResultCount = 0;
    }

    private void LoadFilterSources()
    {
        using var db = new AppDbContext();
        AllLocations = new ObservableCollection<Location>(
            db.Locations.AsNoTracking().OrderBy(l => l.Name).ToList());
        AllSites = new ObservableCollection<Site>(
            db.Sites.AsNoTracking().Include(s => s.Location).OrderBy(s => s.Name).ToList());
        AllPms = new ObservableCollection<PM>(
            db.PMs.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.LastName).ToList());
        AllRompStatuses = new ObservableCollection<string>(StatusConfig.All);

        var orderStatuses = db.Orders.AsNoTracking()
            .Select(o => o.OrderStatus)
            .Where(s => s != null)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        AllOrderStatuses = new ObservableCollection<string>(orderStatuses!);
    }

    private void LoadSavedReports()
    {
        using var db = new AppDbContext();
        SavedReports = new ObservableCollection<SavedReport>(
            db.SavedReports.AsNoTracking().OrderBy(r => r.Name).ToList());
    }

    // ── Multi-select filter setters (called by code-behind on ListBox SelectionChanged) ──

    public void SetSelectedLocations(IEnumerable<Location> items)
    {
        _selectedLocationIds.Clear();
        foreach (var l in items) _selectedLocationIds.Add(l.Id);
        UpdateSelectionInfoLabels();
    }

    public void SetSelectedSites(IEnumerable<Site> items)
    {
        _selectedSiteIds.Clear();
        foreach (var s in items) _selectedSiteIds.Add(s.Id);
        UpdateSelectionInfoLabels();
    }

    public void SetSelectedPms(IEnumerable<PM> items)
    {
        _selectedPmIds.Clear();
        foreach (var p in items) _selectedPmIds.Add(p.Id);
        UpdateSelectionInfoLabels();
    }

    public void SetSelectedRompStatuses(IEnumerable<string> items)
    {
        _selectedRompStatuses.Clear();
        foreach (var s in items) _selectedRompStatuses.Add(s);
        UpdateSelectionInfoLabels();
    }

    public void SetSelectedOrderStatuses(IEnumerable<string> items)
    {
        _selectedOrderStatuses.Clear();
        foreach (var s in items) _selectedOrderStatuses.Add(s);
        UpdateSelectionInfoLabels();
    }

    public IReadOnlySet<int>    GetSelectedLocationIds()   => _selectedLocationIds;
    public IReadOnlySet<int>    GetSelectedSiteIds()       => _selectedSiteIds;
    public IReadOnlySet<int>    GetSelectedPmIds()         => _selectedPmIds;
    public IReadOnlySet<string> GetSelectedRompStatuses()  => _selectedRompStatuses;
    public IReadOnlySet<string> GetSelectedOrderStatuses() => _selectedOrderStatuses;

    private void UpdateSelectionInfoLabels()
    {
        LocationSelectionInfo    = _selectedLocationIds.Count   == 0 ? "All locations" : $"{_selectedLocationIds.Count} location(s) selected";
        SiteSelectionInfo        = _selectedSiteIds.Count       == 0 ? "All sites"     : $"{_selectedSiteIds.Count} site(s) selected";
        PmSelectionInfo          = _selectedPmIds.Count         == 0 ? "All PMs"       : $"{_selectedPmIds.Count} PM(s) selected";
        RompStatusSelectionInfo  = _selectedRompStatuses.Count  == 0 ? "All statuses"  : $"{_selectedRompStatuses.Count} status(es) selected";
        OrderStatusSelectionInfo = _selectedOrderStatuses.Count == 0 ? "All statuses"  : $"{_selectedOrderStatuses.Count} status(es) selected";
    }

    private void ClearFiltersInternal()
    {
        _selectedLocationIds.Clear();
        _selectedSiteIds.Clear();
        _selectedPmIds.Clear();
        _selectedRompStatuses.Clear();
        _selectedOrderStatuses.Clear();
        DateFrom   = null;
        DateTo     = null;
        TextSearch = "";
        UpdateSelectionInfoLabels();
    }

    // ── Column picker (called by code-behind on checkbox toggle) ──

    public void ToggleColumn(string key, bool selected)
    {
        if (selected)
        {
            if (!SelectedColumnKeys.Contains(key)) SelectedColumnKeys.Add(key);
        }
        else
        {
            SelectedColumnKeys.Remove(key);
        }
    }

    // ── Run query ──

    public void RunQuery()
    {
        var filter = BuildFilterState();
        using var db = AppDbContext.ForRead();

        if (Mode == ReportMode.Orders)
        {
            var page   = ReportQueryService.RunOrdersQuery(db, filter, PageIndex, PageSize);
            TotalCount = page.TotalCount;
            Results = new ObservableCollection<ReportRow>(page.Rows.Select(ReportFieldCatalog.BuildOrderRow));
        }
        else
        {
            var page   = ReportQueryService.RunSiteRompQuery(db, filter, PageIndex, PageSize);
            TotalCount = page.TotalCount;
            Results = new ObservableCollection<ReportRow>(page.Rows.Select(ReportFieldCatalog.BuildSiteRompRow));
        }

        // A filter change can shrink the set below the current page; land on the last real
        // page instead of showing an empty grid. PageIndex is now valid, so this recurses once.
        if (PageIndex > 0 && PageIndex >= TotalPages)
        {
            PageIndex = TotalPages - 1;
            RunQuery();
            return;
        }

        ResultCount   = Results.Count;
        StatusMessage = PageStatus;
        NotifyPagingChanged();
        RebuildChart();
    }

    /// <summary>Runs the report from the first page — use for a fresh run, not page navigation.</summary>
    public void RunReport()
    {
        PageIndex = 0;
        RunQuery();
    }

    /// <summary>
    /// Every row the current filter matches, ignoring paging.
    ///
    /// The chart and the Excel export must describe the whole result set, not whichever page
    /// happens to be on screen. Both are opt-in — the chart only when a group-by field is
    /// chosen, the export only when the user asks — so the unpaged cost is never paid by the
    /// plain browse path.
    /// </summary>
    private List<ReportRow> LoadAllRows()
    {
        var filter = BuildFilterState();
        using var db = AppDbContext.ForRead();
        return Mode == ReportMode.Orders
            ? ReportQueryService.RunOrdersQuery(db, filter).Select(ReportFieldCatalog.BuildOrderRow).ToList()
            : ReportQueryService.RunSiteRompQuery(db, filter).Select(ReportFieldCatalog.BuildSiteRompRow).ToList();
    }

    // ── Paging ───────────────────────────────────────────────────────────────

    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private int _pageSize = 250;
    [ObservableProperty] private int _totalCount;

    public IReadOnlyList<int> PageSizeOptions { get; } = new[] { 100, 250, 500, 1000 };

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool CanPrevPage => PageIndex > 0;
    public bool CanNextPage => PageIndex + 1 < TotalPages;

    public string PageStatus => TotalCount == 0
        ? "No rows returned"
        : $"Rows {PageIndex * PageSize + 1:N0}-{Math.Min(TotalCount, (PageIndex + 1) * PageSize):N0} of {TotalCount:N0}"
          + $"   (page {PageIndex + 1:N0} of {TotalPages:N0})";

    partial void OnPageSizeChanged(int value) { PageIndex = 0; RunQuery(); }

    private void NotifyPagingChanged()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanPrevPage));
        OnPropertyChanged(nameof(CanNextPage));
        OnPropertyChanged(nameof(PageStatus));
    }

    public void FirstPage() { if (PageIndex != 0) { PageIndex = 0; RunQuery(); } }
    public void PrevPage()  { if (CanPrevPage)   { PageIndex--; RunQuery(); } }
    public void NextPage()  { if (CanNextPage)   { PageIndex++; RunQuery(); } }
    public void LastPage()  { if (PageIndex != TotalPages - 1) { PageIndex = TotalPages - 1; RunQuery(); } }

    private ReportFilterState BuildFilterState() => new()
    {
        DateField     = SelectedDateField?.Key,
        DateFrom      = DateFrom,
        DateTo        = DateTo,
        LocationIds   = _selectedLocationIds.Count   > 0 ? _selectedLocationIds.ToList()   : null,
        SiteIds       = _selectedSiteIds.Count       > 0 ? _selectedSiteIds.ToList()       : null,
        PmIds         = _selectedPmIds.Count         > 0 ? _selectedPmIds.ToList()         : null,
        RompStatuses  = _selectedRompStatuses.Count  > 0 ? _selectedRompStatuses.ToList()  : null,
        OrderStatuses = _selectedOrderStatuses.Count > 0 ? _selectedOrderStatuses.ToList() : null,
        TextSearch    = string.IsNullOrWhiteSpace(TextSearch) ? null : TextSearch,
    };

    private void ApplyFilterState(ReportFilterState f)
    {
        DateFrom = f.DateFrom;
        DateTo   = f.DateTo;
        SelectedDateField = DateFieldOptions.FirstOrDefault(d => d.Key == f.DateField) ?? DateFieldOptions.FirstOrDefault();
        TextSearch = f.TextSearch ?? "";

        _selectedLocationIds.Clear();
        if (f.LocationIds != null) foreach (var id in f.LocationIds) _selectedLocationIds.Add(id);
        _selectedSiteIds.Clear();
        if (f.SiteIds != null) foreach (var id in f.SiteIds) _selectedSiteIds.Add(id);
        _selectedPmIds.Clear();
        if (f.PmIds != null) foreach (var id in f.PmIds) _selectedPmIds.Add(id);
        _selectedRompStatuses.Clear();
        if (f.RompStatuses != null) foreach (var s in f.RompStatuses) _selectedRompStatuses.Add(s);
        _selectedOrderStatuses.Clear();
        if (f.OrderStatuses != null) foreach (var s in f.OrderStatuses) _selectedOrderStatuses.Add(s);

        UpdateSelectionInfoLabels();
    }

    // ── Chart ──

    public void RebuildChart()
    {
        if (ChartGroupByField == null || ResultCount == 0)
        {
            ChartData = new ObservableCollection<ChartBar>();
            return;
        }

        // Aggregate over every matching row, not just the page on screen.
        var allRows = LoadAllRows();

        string groupKey = ChartGroupByField.Key;
        var groups = allRows.GroupBy(r => r.Values.GetValueOrDefault(groupKey)?.ToString() is { Length: > 0 } s ? s : "(blank)");

        List<(string Label, double Value)> raw;
        if (ChartAggregateField != null && ChartAggregateOp == "Sum")
        {
            string aggKey = ChartAggregateField.Key;
            raw = groups.Select(g => (g.Key, g.Sum(r => ToDouble(r.Values.GetValueOrDefault(aggKey))))).ToList();
        }
        else
        {
            raw = groups.Select(g => (g.Key, (double)g.Count())).ToList();
        }

        raw = raw.OrderByDescending(x => x.Value).Take(25).ToList();

        bool isSum = ChartAggregateOp == "Sum" && ChartAggregateField != null;
        ChartData = new ObservableCollection<ChartBar>(raw.Select(x => new ChartBar
        {
            Label     = x.Label,
            Value     = x.Value,
            ValueText = isSum ? x.Value.ToString("N1") : x.Value.ToString("N0"),
        }));

        ApplyChartScale(_lastChartWidth);
    }

    public void ApplyChartScale(double availableWidth)
    {
        if (availableWidth > 0) _lastChartWidth = availableWidth;
        if (ChartData.Count == 0 || _lastChartWidth <= 0) return;

        double max = ChartData.Max(b => b.Value);
        if (max <= 0) max = 1;
        foreach (var bar in ChartData) bar.BarWidth = bar.Value / max * _lastChartWidth;

        // ChartBar isn't independently observable — reassign to trigger the UI refresh.
        ChartData = new ObservableCollection<ChartBar>(ChartData);
    }

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i    => i,
        null     => 0,
        _        => double.TryParse(v.ToString(), out var d2) ? d2 : 0,
    };

    // ── Saved reports ──

    public void SaveAsNew(string name)
    {
        using var db = new AppDbContext();
        var sr = new SavedReport
        {
            Name                = name,
            Mode                = Mode.ToString(),
            FilterJson          = JsonSerializer.Serialize(BuildFilterState()),
            SelectedColumnsJson = JsonSerializer.Serialize(SelectedColumnKeys.ToList()),
            ChartGroupByField   = ChartGroupByField?.Key,
            ChartAggregateField = ChartAggregateField?.Key,
            ChartAggregateOp    = ChartAggregateOp,
        };
        db.SavedReports.Add(sr);
        db.SaveChanges();
        LoadSavedReports();
        SelectedSavedReport = SavedReports.FirstOrDefault(r => r.Id == sr.Id);
        StatusMessage = $"Saved \"{name}\".";
    }

    public void UpdateExisting(SavedReport sr)
    {
        using var db = new AppDbContext();
        var existing = db.SavedReports.Find(sr.Id);
        if (existing == null) return;

        existing.Mode                = Mode.ToString();
        existing.FilterJson          = JsonSerializer.Serialize(BuildFilterState());
        existing.SelectedColumnsJson = JsonSerializer.Serialize(SelectedColumnKeys.ToList());
        existing.ChartGroupByField   = ChartGroupByField?.Key;
        existing.ChartAggregateField = ChartAggregateField?.Key;
        existing.ChartAggregateOp    = ChartAggregateOp;
        db.SaveChanges();
        LoadSavedReports();
        StatusMessage = $"Updated \"{existing.Name}\".";
    }

    public void LoadSavedReport(SavedReport sr)
    {
        Mode = Enum.TryParse<ReportMode>(sr.Mode, out var m) ? m : ReportMode.Orders;

        var filter = string.IsNullOrWhiteSpace(sr.FilterJson)
            ? new ReportFilterState()
            : JsonSerializer.Deserialize<ReportFilterState>(sr.FilterJson) ?? new ReportFilterState();
        ApplyFilterState(filter);

        var cols = string.IsNullOrWhiteSpace(sr.SelectedColumnsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(sr.SelectedColumnsJson) ?? new List<string>();
        SelectedColumnKeys = new ObservableCollection<string>(cols);

        ChartGroupByField   = AvailableFields.FirstOrDefault(f => f.Key == sr.ChartGroupByField);
        ChartAggregateField = AvailableFields.FirstOrDefault(f => f.Key == sr.ChartAggregateField);
        ChartAggregateOp    = sr.ChartAggregateOp;

        SelectedSavedReport = sr;
        RunQuery();
    }

    public void DeleteSavedReport(SavedReport sr)
    {
        using var db = new AppDbContext();
        var existing = db.SavedReports.Find(sr.Id);
        if (existing == null) return;
        db.SavedReports.Remove(existing);
        db.SaveChanges();
        LoadSavedReports();
        if (SelectedSavedReport?.Id == sr.Id) SelectedSavedReport = null;
        StatusMessage = $"Deleted \"{sr.Name}\".";
    }

    public void RenameSavedReport(SavedReport sr, string newName)
    {
        using var db = new AppDbContext();
        var existing = db.SavedReports.Find(sr.Id);
        if (existing == null) return;
        existing.Name = newName;
        db.SaveChanges();
        LoadSavedReports();
        StatusMessage = $"Renamed to \"{newName}\".";
    }

    // ── Export ──

    public void ExportToExcel(string filePath)
    {
        var columns = SelectedColumnKeys
            .Select(key => AvailableFields.FirstOrDefault(f => f.Key == key))
            .Where(f => f != null)
            .Select(f => f!)
            .ToList();
        // Export the whole result set — exporting only the visible page would silently
        // truncate the spreadsheet to PageSize rows.
        new ReportExportService().Export(filePath, columns, LoadAllRows());
    }
}
