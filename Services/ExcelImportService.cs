using ClosedXML.Excel;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>Result of reading an orders workbook through a column mapping.</summary>
public sealed class OrderSheetParse
{
    public List<Order> Orders { get; } = new();

    /// <summary>Required fields whose mapped column isn't in this sheet (or that aren't mapped).
    /// Non-empty → nothing was read; the import must not go ahead.</summary>
    public List<string> MissingRequired { get; } = new();

    /// <summary>Optional fields mapped to a heading this sheet doesn't have — left empty.</summary>
    public List<string> MissingOptional { get; } = new();

    /// <summary>Field key → the sheet column it was read from (1-based).</summary>
    public Dictionary<string, int> ColumnOf { get; } = new();

    /// <summary>Row-1 heading of each column (1-based), as found in the sheet.</summary>
    public Dictionary<int, string> Headings { get; } = new();

    public bool CanImport => MissingRequired.Count == 0;

    /// <summary>"I (Job Name)" — where a field came from, for messages.</summary>
    public string Describe(string fieldKey) =>
        ColumnOf.TryGetValue(fieldKey, out var c)
            ? OrderColumnMapping.ColumnLetter(c) + (Headings.TryGetValue(c, out var h) && h.Length > 0 ? $" (\"{h}\")" : "")
            : "not imported";
}

public class ExcelImportService
{
    /// <summary>
    /// Reads the first worksheet of <paramref name="filePath"/>: row 1 = headings, data from row 2.
    /// Each Order field comes from the column its mapping names (saved mapping for
    /// <paramref name="sourceType"/> unless <paramref name="mapping"/> is given) — by heading,
    /// or by fixed column letter. See OrderColumnMapping.
    /// </summary>
    public OrderSheetParse Import(string filePath, string sourceType, Dictionary<string, ColumnSource>? mapping = null)
    {
        mapping ??= OrderColumnMapping.Load(sourceType);
        var result = new OrderSheetParse();

        using var workbook = new XLWorkbook(filePath);
        var sheet = workbook.Worksheets.First();

        // Headings — first occurrence wins if two columns share a heading.
        var byHeading = new Dictionary<string, int>();
        var lastCol   = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            var h = sheet.Cell(1, c).GetString().Trim();
            result.Headings[c] = h;
            var key = OrderColumnMapping.NormalizeHeading(h);
            if (key.Length > 0) byHeading.TryAdd(key, c);
        }

        foreach (var field in OrderColumnMapping.Fields)
        {
            var src = mapping.TryGetValue(field.Key, out var s) ? s : ColumnSource.Skip();
            int? col = null;
            if (!string.IsNullOrWhiteSpace(src.Header))
            {
                if (byHeading.TryGetValue(OrderColumnMapping.NormalizeHeading(src.Header), out var hc)) col = hc;
            }
            else if (!string.IsNullOrWhiteSpace(src.Column) && OrderColumnMapping.IsColumnLetter(src.Column.Trim().ToUpperInvariant()))
            {
                col = OrderColumnMapping.ColumnIndex(src.Column);
            }

            if (col is { } found) result.ColumnOf[field.Key] = found;
            else if (field.Required) result.MissingRequired.Add($"{field.Label} — {src}");
            else if (!src.IsSkipped) result.MissingOptional.Add($"{field.Label} — {src}");
        }
        if (!result.CanImport) return result;

        var rowKeys = OrderColumnMapping.RowKeyFields(sourceType)
            .Where(result.ColumnOf.ContainsKey).Select(k => result.ColumnOf[k]).ToList();
        var fields  = OrderColumnMapping.Fields.Where(f => result.ColumnOf.ContainsKey(f.Key))
            .Select(f => (f, Col: result.ColumnOf[f.Key])).ToList();

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= lastRow; row++)
        {
            var r = sheet.Row(row);
            if (rowKeys.Count > 0 && rowKeys.All(c => string.IsNullOrWhiteSpace(GetString(r, c))))
                continue;

            var order = new Order();
            foreach (var (f, col) in fields)
            {
                object? value = f.Kind switch
                {
                    OrderFieldKind.Number  => GetDouble(r, col),
                    OrderFieldKind.Integer => GetInt(r, col),
                    OrderFieldKind.Date    => GetDate(r, col),
                    _                      => GetString(r, col),
                };
                f.Property.SetValue(order, value);
            }
            result.Orders.Add(order);
        }
        return result;
    }

    /// <summary>Row-1 headings of a workbook's first sheet (1-based column → heading) — for the
    /// mapping dialog's "Load headings from a sample file".</summary>
    public static Dictionary<int, string> ReadHeadings(string filePath)
    {
        using var workbook = new XLWorkbook(filePath);
        var sheet   = workbook.Worksheets.First();
        var lastCol = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        var result  = new Dictionary<int, string>();
        for (int c = 1; c <= lastCol; c++) result[c] = sheet.Cell(1, c).GetString().Trim();
        return result;
    }

    private static string? GetString(IXLRow row, int col)
    {
        var val = row.Cell(col).GetString();
        return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
    }

    private static double? GetDouble(IXLRow row, int col)
    {
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return cell.GetDouble();
        if (double.TryParse(cell.GetString(), out var d)) return d;
        return null;
    }

    private static int? GetInt(IXLRow row, int col)
    {
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (int)cell.GetDouble();
        if (int.TryParse(cell.GetString(), out var i)) return i;
        return null;
    }

    private static DateTime? GetDate(IXLRow row, int col)
    {
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime();
        var str = cell.GetString();
        if (DateTime.TryParse(str, out var dt)) return dt;
        return null;
    }
}
