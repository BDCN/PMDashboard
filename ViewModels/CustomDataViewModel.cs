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

public partial class CustomDataViewModel : ObservableObject
{
    [ObservableProperty] private ReportMode _mode = ReportMode.Orders;

    [ObservableProperty] private ObservableCollection<ReportField>   _staticFields   = new();
    [ObservableProperty] private ObservableCollection<CustomColumn>  _dynamicColumns = new();
    [ObservableProperty] private ObservableCollection<CustomDataRow> _rows           = new();

    [ObservableProperty] private ObservableCollection<ReportField> _dateFieldOptions = new();
    [ObservableProperty] private ReportField? _selectedDateField;
    [ObservableProperty] private DateTime?    _dateFrom;
    [ObservableProperty] private DateTime?    _dateTo;

    [ObservableProperty] private int    _resultCount;
    [ObservableProperty] private string _statusMessage = "";

    public bool IsOrdersMode    => Mode == ReportMode.Orders;
    public bool IsSitesRompMode => Mode == ReportMode.SitesRomps;

    partial void OnModeChanged(ReportMode value)
    {
        OnPropertyChanged(nameof(IsOrdersMode));
        OnPropertyChanged(nameof(IsSitesRompMode));
        DateFrom = null;
        DateTo   = null;
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

    public void ApplyFilter() => RunQuery();

    public void RunQuery()
    {
        using var db = new AppDbContext();
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
            var orders = ReportQueryService.RunOrdersQuery(db, filter);
            var ids    = orders.Select(o => o.Id).ToList();
            var custom = db.CustomRows.AsNoTracking()
                .Where(r => r.Mode == modeStr && ids.Contains(r.EntityId))
                .ToDictionary(r => r.EntityId);

            Rows = new ObservableCollection<CustomDataRow>(orders.Select(o =>
                BuildRow(o.Id, ReportFieldCatalog.BuildOrderRow(o).Values, dynCols, custom)));
        }
        else
        {
            var romps = ReportQueryService.RunSiteRompQuery(db, filter);
            var ids   = romps.Select(r => r.RompId).ToList();
            var custom = db.CustomRows.AsNoTracking()
                .Where(r => r.Mode == modeStr && ids.Contains(r.EntityId))
                .ToDictionary(r => r.EntityId);

            Rows = new ObservableCollection<CustomDataRow>(romps.Select(r =>
                BuildRow(r.RompId, ReportFieldCatalog.BuildSiteRompRow(r).Values, dynCols, custom)));
        }

        ResultCount   = Rows.Count;
        StatusMessage = $"{ResultCount} row(s).";
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
        db.SaveChanges();
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
        RefreshDynamicColumns();
    }

    public void RenameColumn(CustomColumn column, string newName)
    {
        using var db = new AppDbContext();
        var tracked = db.CustomColumns.Find(column.Id);
        if (tracked == null) return;
        tracked.Name = newName;
        db.SaveChanges();
        RefreshDynamicColumns();
    }

    public void DeleteColumn(CustomColumn column)
    {
        using var db = new AppDbContext();
        var tracked = db.CustomColumns.Find(column.Id);
        if (tracked == null) return;
        db.CustomColumns.Remove(tracked);
        db.SaveChanges();
        RefreshDynamicColumns();
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
