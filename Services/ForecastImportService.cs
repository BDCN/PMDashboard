using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

public record ForecastImportResult(
    int RowsImported, int NewFieldsCreated, int SitesMatched, int SitesUnmatched, string FileName,
    // Diagnostics only — which sheet was actually read and how big ClosedXML thinks it is.
    // Surfaced to the user when RowsImported is 0, since the usual cause is "the real data
    // isn't on the first worksheet" (common on exports that put a summary/pivot tab first).
    string SheetName = "", int SheetCount = 0, int LastRow = 0, int LastCol = 0);

/// <summary>
/// Header-driven import for Forecast Planning uploads (one file = one ForecastType, per how the
/// real sheets arrive). Unlike the Orders/Sites importers, which read fixed column positions,
/// this reads whatever headers the file actually has and maps each one to a ForecastField —
/// reusing a remembered mapping (ForecastHeaderMapping) when one exists for this type, or
/// auto-creating a new type-specific field on the spot so the import never blocks on an
/// unrecognized column. The Fields management screen is where those auto-created fields get
/// reviewed afterward — renamed, merged into a global field, or marked ignored.
/// </summary>
public class ForecastImportService
{
    /// <summary>
    /// Opens the workbook, retrying once against a sanitized copy if ClosedXML's loader throws.
    /// Real-world forecast exports have hit two concrete failures here: an ArgumentException
    /// resolving an Excel Table's structured-reference column ("The column 'X' does not appear
    /// in the source range"), and a bare NotImplementedException from a pivot table — both
    /// thrown while ClosedXML eagerly parses constructs this import never uses (we only read
    /// header-row and data-row cell text). See XlsxSanitizer for exactly what gets stripped.
    /// </summary>
    private static XLWorkbook OpenWorkbook(string filePath)
    {
        try
        {
            return new XLWorkbook(filePath);
        }
        catch (Exception)
        {
            var sanitized = XlsxSanitizer.StripUnsupportedFeatures(filePath);
            return new XLWorkbook(new MemoryStream(sanitized));
        }
    }

    /// <summary>
    /// Picks the worksheet most likely to hold the real data: the one with the most used rows.
    /// Several real forecast exports put a summary/pivot tab first and the actual row-per-record
    /// data on a later tab — blindly reading Worksheets.First() on those reads a near-empty sheet
    /// (headers only, 0 data rows) instead. A summary tab is reliably much smaller than the data
    /// it summarizes, so "most rows" is a safe proxy without having to inspect header content.
    /// </summary>
    private static IXLWorksheet PickDataSheet(XLWorkbook wb) =>
        wb.Worksheets.OrderByDescending(ws => ws.LastRowUsed()?.RowNumber() ?? 0).First();

    /// <summary>Header row (row 1) of the data worksheet, in column order, including blanks.</summary>
    public List<string> ReadHeaders(string filePath)
    {
        using var wb = OpenWorkbook(filePath);
        var ws = PickDataSheet(wb);
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        var headers = new List<string>();
        for (int c = 1; c <= lastCol; c++)
            headers.Add(ws.Cell(1, c).GetString().Trim());
        return headers;
    }

    /// <summary>
    /// Scores every known ForecastType by what fraction of its previously-seen headers (from
    /// ForecastHeaderMapping — i.e. headers seen on a prior import of that type) are present in
    /// the given header list. A type with no import history yet scores 0 and simply won't be
    /// suggested — the caller always has the option to pick a type manually regardless of score.
    /// </summary>
    public List<(ForecastType Type, double Score)> DetectType(List<string> headers)
    {
        using var db = new AppDbContext();
        var types = db.ForecastTypes.OrderBy(t => t.SortOrder).ToList();
        var mappingsByType = db.ForecastHeaderMappings
            .AsEnumerable()
            .GroupBy(m => m.ForecastTypeId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(m => m.SourceHeader).Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var headerSet = headers.Where(h => !string.IsNullOrWhiteSpace(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<(ForecastType, double)>();
        foreach (var type in types)
        {
            if (!mappingsByType.TryGetValue(type.Id, out var known) || known.Count == 0)
            {
                results.Add((type, 0));
                continue;
            }
            int matched = known.Count(h => headerSet.Contains(h));
            results.Add((type, (double)matched / known.Count));
        }
        return results.OrderByDescending(r => r.Item2).ToList();
    }

    /// <summary>
    /// Collapses a header/field name down to just its letters and digits, lowercased, so
    /// "PO Number", "po_number" and "PO-Number" all compare equal — the real sheets use
    /// inconsistent separators/casing for what's clearly the same field.
    /// </summary>
    private static string NormalizeForMatch(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public ForecastImportResult Import(string filePath, int forecastTypeId, string uploadedBy)
    {
        using var db = new AppDbContext();

        using var wb = OpenWorkbook(filePath);
        var ws = PickDataSheet(wb);
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        var headers = new List<string>();
        for (int c = 1; c <= lastCol; c++)
            headers.Add(ws.Cell(1, c).GetString().Trim());

        // A header repeated within this file's own header row (seen for real — "po_number",
        // "OSD" and "MK2/Titus" each appear twice on one of the actual sheet types) needs its
        // occurrences disambiguated by column position; a header that appears once doesn't.
        var headerCounts = headers.Where(h => !string.IsNullOrWhiteSpace(h))
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var existingMappings = db.ForecastHeaderMappings
            .Where(m => m.ForecastTypeId == forecastTypeId).ToList();
        var allFields = db.ForecastFields.ToList();
        var fieldsById = allFields.ToDictionary(f => f.Id);

        var columnFields = new Dictionary<int, ForecastField>();
        int newFieldsCreated = 0;

        for (int col = 0; col < headers.Count; col++)
        {
            var header = headers[col];
            if (string.IsNullOrWhiteSpace(header)) continue; // blank/junk header — ignored, not imported

            bool isDup = headerCounts.TryGetValue(header, out var cnt) && cnt > 1;
            var mapping = isDup
                ? existingMappings.FirstOrDefault(m =>
                    string.Equals(m.SourceHeader, header, StringComparison.OrdinalIgnoreCase) && m.ColumnIndex == col)
                : existingMappings.FirstOrDefault(m =>
                    string.Equals(m.SourceHeader, header, StringComparison.OrdinalIgnoreCase));

            ForecastField field;
            if (mapping != null && fieldsById.TryGetValue(mapping.ForecastFieldId, out var mappedField))
            {
                field = mappedField;
            }
            else
            {
                // No remembered mapping for this exact header on this type yet. Before creating
                // a brand new field, see if a global (shared) field already means the same thing
                // under a normalized-name match — this is what lets "po_number" on a fresh type
                // land on the same global "PO Number" field everything else uses, instead of
                // spawning a duplicate per type.
                var normalized = NormalizeForMatch(header);
                var reuse = allFields.FirstOrDefault(f =>
                        !f.IsIgnored && f.ForecastTypeId == null &&
                        NormalizeForMatch(f.Name) == normalized)
                    ?? allFields.FirstOrDefault(f =>
                        !f.IsIgnored && f.ForecastTypeId == forecastTypeId &&
                        NormalizeForMatch(f.Name) == normalized);

                if (reuse != null)
                {
                    field = reuse;
                }
                else
                {
                    field = new ForecastField
                    {
                        ForecastTypeId = forecastTypeId,
                        Name           = header,
                        FieldType      = "Text",
                        SortOrder      = 1000 + col,
                    };
                    db.ForecastFields.Add(field);
                    db.SaveChanges(); // need field.Id before Key can be set
                    field.Key = $"ff{field.Id}";
                    db.SaveChanges();
                    allFields.Add(field);
                    fieldsById[field.Id] = field;
                    newFieldsCreated++;
                }

                db.ForecastHeaderMappings.Add(new ForecastHeaderMapping
                {
                    ForecastTypeId  = forecastTypeId,
                    SourceHeader    = header,
                    ColumnIndex     = isDup ? col : -1,
                    ForecastFieldId = field.Id,
                });
            }

            // A field marked Ignored (Forecast Planning's Fields grid) is meant to be excluded
            // from import going forward, not just hidden from the Browse display — but the
            // mapping lookup above resolves straight from an EXISTING ForecastHeaderMapping
            // without ever consulting IsIgnored, so this is the one place that actually has to
            // check it: skip capturing a value for this column entirely. Already-imported rows
            // keep whatever they already have (never touched here), matching IsIgnored's own doc
            // comment that un-ignoring a field should find its history intact.
            if (field.IsIgnored) continue;

            columnFields[col] = field;
        }
        db.SaveChanges();

        var sitesByName = db.Sites.AsEnumerable()
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.LastModified).First(),
                StringComparer.OrdinalIgnoreCase);

        var import = new ForecastImport
        {
            ForecastTypeId = forecastTypeId,
            FileName       = Path.GetFileName(filePath),
            UploadedBy     = uploadedBy,
            UploadedAtUtc  = DateTime.UtcNow,
        };
        db.ForecastImports.Add(import);
        db.SaveChanges();

        int rowsImported = 0, sitesMatched = 0, sitesUnmatched = 0;
        var newRows = new List<ForecastRow>();

        for (int r = 2; r <= lastRow; r++)
        {
            var values = new Dictionary<string, string>();
            bool anyValue = false;
            string? siteCode = null;

            for (int col = 0; col < headers.Count; col++)
            {
                if (!columnFields.TryGetValue(col, out var field)) continue;
                var cellValue = ws.Cell(r, col + 1).GetString().Trim();
                if (string.IsNullOrEmpty(cellValue)) continue;
                anyValue = true;
                values[field.Key] = cellValue;
                if (field.IsSiteKey) siteCode = cellValue;
            }

            if (!anyValue) continue; // fully blank row

            int? siteId = null;
            if (siteCode != null)
            {
                if (sitesByName.TryGetValue(siteCode, out var site)) { siteId = site.Id; sitesMatched++; }
                else sitesUnmatched++;
            }

            newRows.Add(new ForecastRow
            {
                ForecastImportId = import.Id,
                ForecastTypeId   = forecastTypeId,
                SiteCode         = siteCode,
                SiteId           = siteId,
                ValuesJson       = JsonSerializer.Serialize(values),
            });
            rowsImported++;
        }

        db.ForecastRows.AddRange(newRows);
        import.RowCount = rowsImported;
        db.SaveChanges();

        return new ForecastImportResult(rowsImported, newFieldsCreated, sitesMatched, sitesUnmatched, import.FileName,
            ws.Name, wb.Worksheets.Count(), lastRow, lastCol);
    }
}
