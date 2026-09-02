using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

/// <summary>
/// One row of the Custom Data grid — a real Order (Mode=Orders) or flattened ROMP
/// (Mode=SitesRomps), identified by EntityId. StaticValues come from ReportFieldCatalog
/// (read-only, same data the app already has). DynamicValues come from the matching
/// CustomRow, if any, keyed by CustomColumn.Key (editable).
/// </summary>
public class CustomDataRow
{
    public int EntityId { get; init; }
    public Dictionary<string, object?> StaticValues  { get; } = new();
    public Dictionary<string, object?> DynamicValues { get; } = new();
}

/// <summary>One selectable value in a column-header filter popup.</summary>
public partial class FilterValueItem : ObservableObject
{
    public string Display { get; init; } = "";
    [ObservableProperty] private bool _isChecked = true;
}

public partial class CustomDataViewModel : ObservableObject
{
    /// <summary>Display text used for a null/empty cell, in both the popup and the match test.</summary>
    public const string BlankDisplay = "(Blank)";

    /// <summary>Cap on distinct values offered in one popup, to stay responsive on large Orders sets.</summary>
    public const int MaxFilterValues = 1000;

    [ObservableProperty] private ReportMode _mode = ReportMode.Orders;

    [ObservableProperty] private ObservableCollection<ReportField>   _staticFields   = new();
    [ObservableProperty] private ObservableCollection<CustomColumn>  _dynamicColumns = new();

    /// <summary>Every row returned by the query — the source for distinct filter values.</summary>
    [ObservableProperty] private ObservableCollection<CustomDataRow> _rows           = new();

    /// <summary>The subset actually shown in the grid once header filters are applied.</summary>
    [ObservableProperty] private ObservableCollection<CustomDataRow> _filteredRows   = new();

    /// <summary>Column key → the display values the user ticked. Absent means "no filter".</summary>
    private readonly Dictionary<string, HashSet<string>> _columnFilters = new();

    public bool HasActiveFilters => _columnFilters.Count > 0;

    [ObservableProperty] private ObservableCollection<ReportField> _dateFieldOptions = new();
    [ObservableProperty] private ReportField? _selectedDateField;
    [ObservableProperty] private DateTime?    _dateFrom;
    [ObservableProperty] private DateTime?    _dateTo;

    [ObservableProperty] private int    _resultCount;
    [ObservableProperty] private string _statusMessage = "";

    // ── Paging ───────────────────────────────────────────────────────────────
    // Orders can run to six figures; Count and Skip/Take run in SQL so only one page of
    // rows is ever materialised.
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private int _pageSize = 250;
    [ObservableProperty] private int _totalCount;

    public IReadOnlyList<int> PageSizeOptions { get; } = new[] { 100, 250, 500, 1000 };

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool CanPrevPage => PageIndex > 0;
    public bool CanNextPage => PageIndex + 1 < TotalPages;

    public string PageStatus => TotalCount == 0
        ? "No rows"
        : $"Rows {PageIndex * PageSize + 1:N0}-{Math.Min(TotalCount, (PageIndex + 1) * PageSize):N0} of {TotalCount:N0}"
          + $"   (page {PageIndex + 1:N0} of {TotalPages:N0})";

    partial void OnPageSizeChanged(int value)
    {
        PageIndex = 0;
        RunQuery();
    }

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

    public bool IsOrdersMode    => Mode == ReportMode.Orders;
    public bool IsSitesRompMode => Mode == ReportMode.SitesRomps;

    partial void OnModeChanged(ReportMode value)
    {
        OnPropertyChanged(nameof(IsOrdersMode));
        OnPropertyChanged(nameof(IsSitesRompMode));
        DateFrom = null;
        DateTo   = null;
        // Column filters name values from the previous row set, so they cannot carry over.
        _columnFilters.Clear();
        PageIndex = 0;
        OnPropertyChanged(nameof(HasActiveFilters));
        InitializeCatalogForMode();
        RunQuery();
    }

    // ── Load ──

    public void Load()
    {
        InitializeCatalogForMode();
        RunQuery();
    }

    private void InitializeCatalogForMode()
    {
        StaticFields = new ObservableCollection<ReportField>(
            Mode == ReportMode.Orders ? ReportFieldCatalog.OrderFields : ReportFieldCatalog.SiteRompFields);

        DateFieldOptions = new ObservableCollection<ReportField>(
            Mode == ReportMode.Orders
                ? ReportFieldCatalog.OrderDateFields
                : ReportFieldCatalog.SiteRompFields.Where(f => f.Key == "LastModified"));
        SelectedDateField = DateFieldOptions.FirstOrDefault();

        RefreshDynamicColumns();
    }

    // ── Query (static data + attached dynamic values) ──

    public void ApplyFilter()
    {
        PageIndex = 0;   // a new filter invalidates the current page position
        RunQuery();
    }

    public void RunQuery()
    {
        using var db = AppDbContext.ForRead();
        var filter = new ReportFilterState
        {
            DateField = SelectedDateField?.Key,
            DateFrom  = DateFrom,
            DateTo    = DateTo,
        };

        var dynCols  = DynamicColumns.ToList();
        var modeStr  = Mode.ToString();

        if (Mode == ReportMode.Orders)
        {
            var page   = ReportQueryService.RunOrdersQuery(db, filter, PageIndex, PageSize);
            TotalCount = page.TotalCount;
            var ids    = page.Rows.Select(o => o.Id).ToList();
            var custom = db.CustomRows.AsNoTracking()
                .Where(r => r.Mode == modeStr && ids.Contains(r.EntityId))
                .ToDictionary(r => r.EntityId);

            Rows = new ObservableCollection<CustomDataRow>(page.Rows.Select(o =>
                BuildRow(o.Id, ReportFieldCatalog.BuildOrderRow(o).Values, dynCols, custom)));
        }
        else
        {
            var page   = ReportQueryService.RunSiteRompQuery(db, filter, PageIndex, PageSize);
            TotalCount = page.TotalCount;
            var ids    = page.Rows.Select(r => r.RompId).ToList();
            var custom = db.CustomRows.AsNoTracking()
                .Where(r => r.Mode == modeStr && ids.Contains(r.EntityId))
                .ToDictionary(r => r.EntityId);

            Rows = new ObservableCollection<CustomDataRow>(page.Rows.Select(r =>
                BuildRow(r.RompId, ReportFieldCatalog.BuildSiteRompRow(r).Values, dynCols, custom)));
        }

        // A narrower filter can shrink the set below the current page; land on the last real
        // page rather than an empty grid. PageIndex is valid by then, so this recurses once.
        if (PageIndex > 0 && PageIndex >= TotalPages)
        {
            PageIndex = TotalPages - 1;
            RunQuery();
            return;
        }

        ApplyColumnFilters();
    }

    // ── Column header filters ────────────────────────────────────────────────

    /// <summary>Rebuilds FilteredRows from Rows using every active column filter (AND across columns).</summary>
    public void ApplyColumnFilters()
    {
        FilteredRows = _columnFilters.Count == 0
            ? new ObservableCollection<CustomDataRow>(Rows)
            : new ObservableCollection<CustomDataRow>(Rows.Where(RowPasses));

        ResultCount   = FilteredRows.Count;
        // Column filters refine the loaded page; the date-range filter above is what narrows
        // the whole data set, so the two counts are deliberately reported separately.
        StatusMessage = _columnFilters.Count == 0
            ? PageStatus
            : $"{PageStatus}   ·   {ResultCount:N0} shown on this page after {_columnFilters.Count} column filter(s)";
        OnPropertyChanged(nameof(HasActiveFilters));
        NotifyPagingChanged();
    }

    private bool RowPasses(CustomDataRow row)
    {
        foreach (var (key, allowed) in _columnFilters)
            if (!allowed.Contains(DisplayFor(row, key)))
                return false;
        return true;
    }

    /// <summary>
    /// The text a cell shows, used both to populate the filter popup and to test a row against it.
    /// Matching on display text keeps one comparison path for the boxed doubles, DateTimes, bools
    /// and strings that share these value dictionaries.
    /// </summary>
    public static string FormatCellValue(object? value) => value switch
    {
        null        => BlankDisplay,
        DateTime dt => dt.ToString("yyyy-MM-dd"),
        bool b      => b ? "True" : "False",
        double d    => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _           => value.ToString() is { Length: > 0 } t ? t : BlankDisplay,
    };

    private static string DisplayFor(CustomDataRow row, string key) =>
        row.DynamicValues.TryGetValue(key, out var dyn) ? FormatCellValue(dyn)
        : row.StaticValues.TryGetValue(key, out var stat) ? FormatCellValue(stat)
        : BlankDisplay;

    /// <summary>
    /// Distinct values for a column, ticked to match the filter currently applied to it.
    /// Values come from the unfiltered row set, so clearing one column's filter always restores
    /// its full value list rather than only what the other columns left visible.
    /// </summary>
    public List<FilterValueItem> GetFilterValues(string key)
    {
        var active = _columnFilters.TryGetValue(key, out var sel) ? sel : null;

        return Rows.Select(r => DisplayFor(r, key))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(v => v == BlankDisplay ? 1 : 0)
                   .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                   .Take(MaxFilterValues)
                   .Select(v => new FilterValueItem { Display = v, IsChecked = active == null || active.Contains(v) })
                   .ToList();
    }

    /// <summary>True when more distinct values exist than the popup is willing to list.</summary>
    public bool FilterValuesTruncated(string key) =>
        Rows.Select(r => DisplayFor(r, key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(MaxFilterValues).Any();

    public bool IsColumnFiltered(string key) => _columnFilters.ContainsKey(key);

    /// <summary>
    /// Applies a filter to one column. Selecting every available value (or none) clears the filter
    /// rather than storing a no-op set, so the header indicator stays truthful.
    /// </summary>
    public void SetColumnFilter(string key, IEnumerable<string> selectedDisplays)
    {
        var set   = new HashSet<string>(selectedDisplays, StringComparer.OrdinalIgnoreCase);
        var total = Rows.Select(r => DisplayFor(r, key)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (set.Count == 0 || set.Count >= total) _columnFilters.Remove(key);
        else                                      _columnFilters[key] = set;

        ApplyColumnFilters();
    }

    public void ClearColumnFilter(string key)
    {
        if (_columnFilters.Remove(key)) ApplyColumnFilters();
    }

    public void ClearAllFilters()
    {
        if (_columnFilters.Count == 0) return;
        _columnFilters.Clear();
        ApplyColumnFilters();
    }

    private static CustomDataRow BuildRow(
        int entityId, Dictionary<string, object?> staticValues,
        List<CustomColumn> dynCols, Dictionary<int, CustomRow> customByEntity)
    {
        var row = new CustomDataRow { EntityId = entityId };
        foreach (var kv in staticValues) row.StaticValues[kv.Key] = kv.Value;

        var stored = DeserializeValues(customByEntity.TryGetValue(entityId, out var cr) ? cr.ValuesJson : null);
        foreach (var col in dynCols)
            row.DynamicValues[col.Key] = stored.TryGetValue(col.Key, out var el) ? ParseValue(el, col.ColumnType) : null;

        return row;
    }

    private static Dictionary<string, JsonElement> DeserializeValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static object? ParseValue(JsonElement el, string columnType)
    {
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return columnType switch
        {
            "Number" => el.ValueKind == JsonValueKind.Number
                ? el.GetDouble()
                : double.TryParse(el.GetString(), out var d) ? d : null,
            "Date" => el.ValueKind == JsonValueKind.String && DateTime.TryParse(el.GetString(), out var dt)
                ? dt : (object?)null,
            "Bool" => el.ValueKind is JsonValueKind.True or JsonValueKind.False && el.GetBoolean(),
            _ => el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString(),
        };
    }

    // ── Dynamic cell persistence — upserts a CustomRow for this (Mode, EntityId) ──

    public void SaveRow(CustomDataRow row)
    {
        using var db = new AppDbContext();
        var modeStr  = Mode.ToString();
        var existing = db.CustomRows.FirstOrDefault(r => r.Mode == modeStr && r.EntityId == row.EntityId);
        if (existing == null)
        {
            existing = new CustomRow { Mode = modeStr, EntityId = row.EntityId };
            db.CustomRows.Add(existing);
        }
        existing.ValuesJson = JsonSerializer.Serialize(row.DynamicValues);

        // Touch the underlying Order/ROMP as well.
        //
        // The edit writes a CustomRow, not the entity itself, so without this the row's
        // "Last Changed" / "Last Changed By" would never move even though the user just
        // changed something on that row. Setting LastModified explicitly also marks the
        // entity Modified, so the SaveChanges stamp fills in LastModifiedBy.
        TouchParentEntity(db, row.EntityId);

        db.SaveChanges();

        // Logged against the underlying Order/Romp id, which is what the report's History
        // link queries - so a custom-column edit shows up in that row's history.
        var summary = string.Join(", ", DynamicColumns
            .Where(c => row.DynamicValues.TryGetValue(c.Key, out var v) && v != null)
            .Select(c => $"{c.Name}={FormatCellValue(row.DynamicValues[c.Key])}"));
        AuditService.Log("Update", Mode == ReportMode.Orders ? "Order" : "Romp", row.EntityId,
            string.IsNullOrEmpty(summary) ? "Custom values cleared" : $"Custom values: {summary}");
    }

    /// <summary>
    /// Marks the Order or ROMP behind a Custom Data row as changed, so the change-tracking
    /// columns reflect a custom-cell edit. Best-effort: a missing parent must not stop the
    /// custom values themselves from saving.
    /// </summary>
    private void TouchParentEntity(AppDbContext db, int entityId)
    {
        try
        {
            if (Mode == ReportMode.Orders)
            {
                var order = db.Orders.Find(entityId);
                if (order != null) order.LastModified = DateTime.UtcNow;
            }
            else
            {
                var romp = db.Romps.Find(entityId);
                if (romp != null) romp.LastModified = DateTime.UtcNow;
            }
        }
        catch { /* parent gone or unreachable - the custom values still save */ }
    }

    // ── Column CRUD (mode-scoped; shared with ManageColumnsDialog — the "setup screen") ──

    public void AddColumn(string name, string columnType)
    {
        using var db = new AppDbContext();
        var modeStr   = Mode.ToString();
        var maxOrder  = db.CustomColumns.Where(c => c.Mode == modeStr).Select(c => (int?)c.SortOrder).Max() ?? 0;
        var col = new CustomColumn { Mode = modeStr, Name = name, ColumnType = columnType, SortOrder = maxOrder + 1 };
        db.CustomColumns.Add(col);
        db.SaveChanges();          // Id assigned here
        col.Key = $"col{col.Id}";  // stable key, decoupled from the (renameable) display Name
        db.SaveChanges();
        AuditService.Log("Create", "CustomColumn", col.Id, $"{modeStr}: \"{name}\" ({columnType})");
        RefreshDynamicColumns();
    }

    public void RenameColumn(CustomColumn column, string newName)
    {
        using var db = new AppDbContext();
        var tracked = db.CustomColumns.Find(column.Id);
        if (tracked == null) return;
        var oldName = tracked.Name;
        tracked.Name = newName;
        db.SaveChanges();
        AuditService.Log("Update", "CustomColumn", tracked.Id, $"Renamed \"{oldName}\" -> \"{newName}\"");
        RefreshDynamicColumns();
    }

    /// <summary>
    /// How many stored rows currently hold a value for this column — shown in the delete
    /// confirmation so the user knows what the purge below will actually destroy.
    /// </summary>
    public int CountRowsWithValuesFor(CustomColumn column)
    {
        using var db = new AppDbContext();
        return db.CustomRows.AsNoTracking()
                 .Where(r => r.Mode == column.Mode)
                 .AsEnumerable()
                 .Count(r => DeserializeValues(r.ValuesJson).ContainsKey(column.Key));
    }

    /// <summary>
    /// Deletes the column and purges its values from every CustomRow.
    ///
    /// The stored values are keyed by CustomColumn.Key, and a re-added column always gets a new
    /// Key, so values left behind here would be permanently unreachable rather than recoverable —
    /// stripping them keeps the JSON honest instead of accumulating dead keys.
    /// </summary>
    public void DeleteColumn(CustomColumn column)
    {
        using var db = new AppDbContext();
        var tracked = db.CustomColumns.Find(column.Id);
        if (tracked == null) return;

        var mode = tracked.Mode;
        var key  = tracked.Key;
        db.CustomColumns.Remove(tracked);

        // Purge the column's values, rewriting only the rows that actually carry the key.
        if (!string.IsNullOrEmpty(key))
        {
            foreach (var row in db.CustomRows.Where(r => r.Mode == mode).ToList())
            {
                var values = DeserializeValues(row.ValuesJson);
                if (!values.Remove(key)) continue;
                row.ValuesJson = JsonSerializer.Serialize(
                    values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
            }
        }

        db.SaveChanges();
        AuditService.Log("Delete", "CustomColumn", column.Id,
            $"{mode}: \"{column.Name}\" deleted and its values purged");

        // Drop any header filter and saved position that referenced the removed column.
        ClearColumnFilter(key);
        var order = GetSavedColumnOrder();
        if (order.Remove(key)) SaveColumnOrder(order);
        RefreshDynamicColumns();
    }

    // ── Persistent grid column order ─────────────────────────────────────────

    private string ColumnOrderConfigKey => ConfigKeys.CustomDataColumnOrderPrefix + Mode;

    /// <summary>
    /// The saved left-to-right order of grid columns for this mode, as column keys.
    /// Empty when the user has never reordered. Stored in AppConfigs so it travels to other
    /// machines over the normal sync path, and covers static and dynamic columns alike —
    /// SortOrder only ever ranked dynamic columns against each other, which could never
    /// place a custom column in between two static ones.
    /// </summary>
    public List<string> GetSavedColumnOrder()
    {
        var json = new ConfigService().Get(ColumnOrderConfigKey, "");
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void SaveColumnOrder(IEnumerable<string> keys) =>
        new ConfigService().Set(ColumnOrderConfigKey, JsonSerializer.Serialize(keys.ToList()));

    /// <summary>
    /// Moves a key one slot within the saved order, so the Manage Columns arrows and a
    /// drag in the grid cannot end up disagreeing about where a column belongs.
    /// </summary>
    private void ShiftInSavedOrder(string key, string swapWithKey)
    {
        var order = GetSavedColumnOrder();
        int a = order.IndexOf(key), b = order.IndexOf(swapWithKey);
        if (a < 0 || b < 0) return;          // not tracked yet — SortOrder alone governs
        (order[a], order[b]) = (order[b], order[a]);
        SaveColumnOrder(order);
    }

    public void MoveColumnUp(CustomColumn column)   => SwapColumnOrder(column, -1);
    public void MoveColumnDown(CustomColumn column) => SwapColumnOrder(column, +1);

    private void SwapColumnOrder(CustomColumn column, int direction)
    {
        using var db = new AppDbContext();
        var modeStr = Mode.ToString();
        var all     = db.CustomColumns.Where(c => c.Mode == modeStr).OrderBy(c => c.SortOrder).ToList();
        int idx     = all.FindIndex(c => c.Id == column.Id);
        int target  = idx + direction;
        if (idx < 0 || target < 0 || target >= all.Count) return;
        (all[idx].SortOrder, all[target].SortOrder) = (all[target].SortOrder, all[idx].SortOrder);
        db.SaveChanges();
        ShiftInSavedOrder(all[idx].Key, all[target].Key);
        RefreshDynamicColumns();
    }

    /// <summary>
    /// Reloads DynamicColumns for the current mode and resyncs already-loaded Rows' DynamicValues
    /// to match, without re-running the (unchanged) static Orders/ROMPs query.
    /// </summary>
    private void RefreshDynamicColumns()
    {
        using var db = new AppDbContext();
        var modeStr = Mode.ToString();
        var cols = db.CustomColumns.AsNoTracking()
                                   .Where(c => c.Mode == modeStr)
                                   .OrderBy(c => c.SortOrder)
                                   .ToList();
        DynamicColumns = new ObservableCollection<CustomColumn>(cols);

        if (Rows.Count == 0) return;

        var entityIds = Rows.Select(r => r.EntityId).ToList();
        var custom = db.CustomRows.AsNoTracking()
            .Where(r => r.Mode == modeStr && entityIds.Contains(r.EntityId))
            .ToDictionary(r => r.EntityId);

        foreach (var row in Rows)
        {
            row.DynamicValues.Clear();
            var stored = DeserializeValues(custom.TryGetValue(row.EntityId, out var cr) ? cr.ValuesJson : null);
            foreach (var col in cols)
                row.DynamicValues[col.Key] = stored.TryGetValue(col.Key, out var el) ? ParseValue(el, col.ColumnType) : null;
        }
    }
}
