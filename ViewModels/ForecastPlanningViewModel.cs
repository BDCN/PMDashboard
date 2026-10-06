using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

/// <summary>Flattened, read-only view of one ForecastRow for the browse grid.</summary>
public class ForecastRowDisplay
{
    public int     Id          { get; init; }
    public string? SiteCode    { get; init; }
    public bool    SiteMatched { get; init; }
    public string  FileName    { get; init; } = "";
    public DateTime UploadedAtUtc { get; init; }
    public string  Values      { get; init; } = "";
}

public partial class ForecastPlanningViewModel : ObservableObject
{
    private readonly ForecastImportService _importService = new();

    [ObservableProperty] private ObservableCollection<ForecastType> _types = new();
    [ObservableProperty] private ForecastType? _selectedType;

    [ObservableProperty] private ObservableCollection<ForecastField> _fields = new();

    [ObservableProperty] private ObservableCollection<ForecastImport> _recentImports = new();

    [ObservableProperty] private ObservableCollection<ForecastRowDisplay> _browseRows = new();
    [ObservableProperty] private int _browsePageIndex;
    [ObservableProperty] private int _browseTotalPages = 1;
    [ObservableProperty] private int _browseTotalRows;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _newTypeCode = "";
    [ObservableProperty] private string _newTypeName = "";

    private const int PageSize = 50;

    public void Load()
    {
        LoadTypes();
    }

    public void LoadTypes()
    {
        using var db = new AppDbContext();
        var list = db.ForecastTypes.AsNoTracking().OrderBy(t => t.SortOrder).ToList();
        Types = new ObservableCollection<ForecastType>(list);

        if (SelectedType == null || !list.Any(t => t.Id == SelectedType.Id))
            SelectedType = list.FirstOrDefault();
        else
            RefreshTypeDependentData();
    }

    partial void OnSelectedTypeChanged(ForecastType? value) => RefreshTypeDependentData();

    private void RefreshTypeDependentData()
    {
        LoadFields();
        LoadImports();
        BrowsePageIndex = 0;
        LoadBrowseData();
    }

    // ── Types CRUD ────────────────────────────────────────────────────────────

    public string? AddType()
    {
        var code = NewTypeCode.Trim();
        var name = NewTypeName.Trim();
        if (string.IsNullOrWhiteSpace(code)) return "Enter a type code.";
        if (string.IsNullOrWhiteSpace(name)) name = $"Type {code}";

        using var db = new AppDbContext();
        if (db.ForecastTypes.Any(t => t.Code.ToLower() == code.ToLower()))
            return $"A type with code \"{code}\" already exists.";

        var maxSort = db.ForecastTypes.Select(t => (int?)t.SortOrder).Max() ?? 0;
        db.ForecastTypes.Add(new ForecastType { Code = code, Name = name, SortOrder = maxSort + 1 });
        db.SaveChanges();

        NewTypeCode = "";
        NewTypeName = "";
        LoadTypes();
        return null;
    }

    // ── Fields management ────────────────────────────────────────────────────

    public void LoadFields()
    {
        if (SelectedType is not { } type) { Fields = new ObservableCollection<ForecastField>(); return; }
        using var db = new AppDbContext();
        var list = db.ForecastFields.AsNoTracking()
            .Where(f => f.ForecastTypeId == null || f.ForecastTypeId == type.Id)
            .AsEnumerable()
            .OrderBy(f => f.ForecastTypeId == null ? 0 : 1) // global fields first
            .ThenBy(f => f.SortOrder)
            .ToList();
        Fields = new ObservableCollection<ForecastField>(list);
    }

    public void RenameField(ForecastField field, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        using var db = new AppDbContext();
        var tracked = db.ForecastFields.Find(field.Id);
        if (tracked == null) return;

        // Compare against the freshly-loaded DB value, not the passed-in `field` — the grid's
        // own two-way binding already mutated `field.Name` to the new text before this ever
        // runs, so comparing newName to field.Name was really comparing it to itself: always
        // equal, so this returned early every time and the rename never reached SaveChanges().
        // Confirmed by the bug report: renames looked fine in the grid (that's just the live
        // binding) but vanished the moment anything else (e.g. toggling Ignored) called
        // LoadFields() and re-read the never-actually-saved name back out of the database.
        if (tracked.Name == newName) return;

        tracked.Name = newName;
        db.SaveChanges();
        LoadFields();
    }

    public void SetFieldIgnored(ForecastField field, bool ignored)
    {
        using var db = new AppDbContext();
        var tracked = db.ForecastFields.Find(field.Id);
        if (tracked == null) return;
        tracked.IsIgnored = ignored;
        db.SaveChanges();
        LoadFields();
    }

    public void SetFieldIsSiteKey(ForecastField field, bool isSiteKey)
    {
        using var db = new AppDbContext();
        if (isSiteKey)
        {
            // Only one site-key field makes sense per field set — clear any other global one
            // sharing scope with this field (global clears global; type-specific clears that type's).
            var others = db.ForecastFields.Where(f =>
                f.Id != field.Id && f.IsSiteKey &&
                ((field.ForecastTypeId == null && f.ForecastTypeId == null) ||
                 (field.ForecastTypeId != null && f.ForecastTypeId == field.ForecastTypeId)));
            foreach (var o in others) o.IsSiteKey = false;
        }
        var tracked = db.ForecastFields.Find(field.Id);
        if (tracked != null) tracked.IsSiteKey = isSiteKey;
        db.SaveChanges();
        LoadFields();
    }

    /// <summary>
    /// Marks a field as the campus column (Type O's "az", Type L's "Cluster"): its value is the
    /// campus each row's site belongs to. Changing it re-arranges sites into campuses.
    /// </summary>
    public CampusArrangementService.Result SetFieldIsCampus(ForecastField field, bool isCampus)
    {
        using (var db = new AppDbContext())
        {
            var tracked = db.ForecastFields.Find(field.Id);
            if (tracked != null) tracked.IsCampusField = isCampus;
            db.SaveChanges();
        }
        LoadFields();
        return CampusArrangementService.Arrange();
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    /// <summary>Reads just the header row and scores it against every known type — the UI uses
    /// this to pre-select a type in the upload dialog rather than defaulting to whatever's
    /// currently selected on the page.</summary>
    public (List<string> Headers, ForecastType? BestGuess, double BestScore) DetectUpload(string filePath)
    {
        var headers = _importService.ReadHeaders(filePath);
        var ranked = _importService.DetectType(headers);
        var best = ranked.FirstOrDefault();
        return (headers, best.Type, best.Score);
    }

    /// <summary>What the campus arrangement after the last upload changed (null if it failed).</summary>
    public CampusArrangementService.Result? LastArrangement { get; private set; }

    public ForecastImportResult Import(string filePath, int forecastTypeId)
    {
        var result = _importService.Import(filePath, forecastTypeId, CurrentUser.Email);
        AuditService.Log("Import", "Forecast", details:
            $"Imported {result.FileName}: {result.RowsImported} row(s), {result.NewFieldsCreated} new field(s), " +
            $"{result.SitesMatched} site(s) matched, {result.SitesUnmatched} unmatched");
        // New forecast rows can place sites into campuses (or create the sites' campuses).
        try { LastArrangement = CampusArrangementService.Arrange(); }
        catch { LastArrangement = null; }
        if (SelectedType?.Id == forecastTypeId) RefreshTypeDependentData();
        return result;
    }

    // ── Recent imports + browse data ─────────────────────────────────────────

    public void LoadImports()
    {
        if (SelectedType is not { } type) { RecentImports = new ObservableCollection<ForecastImport>(); return; }
        using var db = new AppDbContext();
        var list = db.ForecastImports.AsNoTracking()
            .Where(i => i.ForecastTypeId == type.Id)
            .OrderByDescending(i => i.UploadedAtUtc)
            .Take(10)
            .ToList();
        RecentImports = new ObservableCollection<ForecastImport>(list);
    }

    public void LoadBrowseData()
    {
        if (SelectedType is not { } type)
        {
            BrowseRows = new ObservableCollection<ForecastRowDisplay>();
            BrowseTotalPages = 1;
            BrowseTotalRows = 0;
            return;
        }

        using var db = new AppDbContext();
        var query = db.ForecastRows.AsNoTracking()
            .Where(r => r.ForecastTypeId == type.Id)
            .OrderByDescending(r => r.Id);

        BrowseTotalRows = query.Count();
        BrowseTotalPages = Math.Max(1, (int)Math.Ceiling(BrowseTotalRows / (double)PageSize));
        BrowsePageIndex  = Math.Min(BrowsePageIndex, BrowseTotalPages - 1);

        var page = query.Skip(BrowsePageIndex * PageSize).Take(PageSize)
            .Include(r => r.Site)
            .ToList();

        var fieldsByKey = db.ForecastFields.AsNoTracking()
            .Where(f => f.ForecastTypeId == null || f.ForecastTypeId == type.Id)
            .ToDictionary(f => f.Key);

        var importsById = db.ForecastImports.AsNoTracking()
            .Where(i => i.ForecastTypeId == type.Id)
            .ToDictionary(i => i.Id);

        var display = new List<ForecastRowDisplay>();
        foreach (var row in page)
        {
            string valuesText;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson) ?? new();
                valuesText = string.Join("  ·  ", dict
                    .Where(kv => fieldsByKey.TryGetValue(kv.Key, out var f) && !f.IsIgnored)
                    .Select(kv => $"{fieldsByKey[kv.Key].Name}: {kv.Value}"));
            }
            catch { valuesText = "(unreadable)"; }

            importsById.TryGetValue(row.ForecastImportId, out var import);

            display.Add(new ForecastRowDisplay
            {
                Id            = row.Id,
                SiteCode      = row.SiteCode,
                SiteMatched   = row.SiteId != null,
                FileName      = import?.FileName ?? "",
                UploadedAtUtc = import?.UploadedAtUtc ?? default,
                Values        = valuesText,
            });
        }
        BrowseRows = new ObservableCollection<ForecastRowDisplay>(display);
    }

    public void NextPage()
    {
        if (BrowsePageIndex >= BrowseTotalPages - 1) return;
        BrowsePageIndex++;
        LoadBrowseData();
    }

    public void PrevPage()
    {
        if (BrowsePageIndex <= 0) return;
        BrowsePageIndex--;
        LoadBrowseData();
    }
}
