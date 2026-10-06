namespace PMDashboard.Data.Models;

/// <summary>
/// A column definition for forecast data — either global (ForecastTypeId is null, shared across
/// every type: Site, Region, Edd, etc. — the columns that recur across all 5 real sheet types)
/// or scoped to one ForecastType (type-specific, e.g. "MK2/Titus" which only appears on Type L).
/// Key is generated once at creation ("ff{Id}") and never changes, mirroring CustomColumn —
/// renaming Name never orphans values already stored under the old Key in ForecastRow.ValuesJson.
/// </summary>
public class ForecastField
{
    public int          Id             { get; set; }
    public int?         ForecastTypeId { get; set; }       // null = global/shared across all types
    public ForecastType? ForecastType  { get; set; }
    public string        Name          { get; set; } = "";
    public string Key            { get; set; } = "";
    public string FieldType      { get; set; } = "Text";   // "Text" | "Number" | "Date"

    /// <summary>This field's value is the site code to match against Site.Name on import.</summary>
    public bool IsSiteKey { get; set; }

    /// <summary>This field's value names the row's campus (the forecast's "az" column) — used by
    /// CampusArrangementService to place each site under its campus. Seeded on for Type O's "az"
    /// and Type L's az-style "Cluster" column; editable in Forecast Planning → Fields.</summary>
    public bool IsCampusField { get; set; }

    /// <summary>Excluded from import/display — kept (not deleted) so its mapping history and any
    /// already-imported values remain intact if it's ever un-ignored.</summary>
    public bool IsIgnored { get; set; }

    public int SortOrder { get; set; }

    public DateTime LastModified { get; set; }

    /// <summary>Computed, EF-ignored (see AppDbContext.OnModelCreating) — for the Fields grid's Scope column.</summary>
    public string ScopeLabel => ForecastTypeId == null ? "Global" : "Type-specific";

    /// <summary>
    /// Computed, EF-ignored — the Excel column letter (A, B, ..., Z, AA, AB, ...) this field's
    /// data came from in its source sheet, so a user staring at "Unlabeled Column 7" can go find
    /// column G in their own copy of the file and see what it actually is. Only meaningful for a
    /// type-specific field: SortOrder for those is set to 1000 + the field's 0-based column index
    /// at creation time (see ForecastImportService.Import), so the column index is just
    /// SortOrder - 1000. A global field is shared across every sheet type, potentially at a
    /// different column position in each one, so there's no single correct answer here — blank
    /// rather than a misleading guess.
    /// </summary>
    public string ExcelColumn
    {
        get
        {
            if (ForecastTypeId == null || SortOrder < 1000) return "";
            int n = SortOrder - 1000 + 1; // 1-based for the standard bijective base-26 conversion
            var letters = "";
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                letters = (char)('A' + rem) + letters;
                n = (n - 1) / 26;
            }
            return letters;
        }
    }
}
