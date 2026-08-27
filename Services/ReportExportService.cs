using ClosedXML.Excel;

namespace PMDashboard.Services;

/// <summary>Exports Report Builder results to a styled Excel worksheet, following PmImportService's header convention.</summary>
public class ReportExportService
{
    public void Export(string filePath, IReadOnlyList<ReportField> columns, IReadOnlyList<ReportRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Report Results");

        for (int col = 1; col <= columns.Count; col++)
        {
            var cell = ws.Cell(1, col);
            cell.Value = columns[col - 1].DisplayName;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = XLColor.FromHtml("#1D4ED8");
        }

        for (int i = 0; i < rows.Count; i++)
        {
            int row = i + 2;
            for (int col = 1; col <= columns.Count; col++)
            {
                var field = columns[col - 1];
                var value = rows[i].Values.GetValueOrDefault(field.Key);
                var cell  = ws.Cell(row, col);

                switch (value)
                {
                    case null:
                        cell.Value = "";
                        break;
                    case double d:
                        cell.Value = d;
                        break;
                    case int n:
                        cell.Value = n;
                        break;
                    case DateTime dt:
                        cell.Value = dt;
                        cell.Style.DateFormat.Format = "yyyy-MM-dd";
                        break;
                    default:
                        cell.Value = value.ToString();
                        break;
                }
            }
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
        wb.SaveAs(filePath);
    }
}
