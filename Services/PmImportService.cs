using ClosedXML.Excel;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

public class PmImportService
{
    private static readonly string[] ValidHomeOffices = ["FRE", "VAN", "JAX", "Remote"];
    private static readonly string[] ValidGeoLocations = ["East", "Central", "West"];

    // Pass existingPms to pre-populate the template with current database records.
    // Leave null (or empty) to generate a blank template with a dimmed example row.
    public void GenerateTemplate(string filePath, List<PM>? existingPms = null)
    {
        using var wb = new XLWorkbook();
        BuildDataSheet(wb, existingPms);
        BuildInstructionsSheet(wb);
        wb.SaveAs(filePath);
    }

    private static void BuildDataSheet(XLWorkbook wb, List<PM>? pms)
    {
        var ws = wb.Worksheets.Add("PM Bulk Import");

        // A  B          C            D                      E          F         G
        string[] headers =
        [
            "First Name", "Last Name", "Home Office", "Geographical Location",
            "Data Name", "Capacity", "Ideal Utilization (%)"
        ];

        for (int col = 1; col <= headers.Length; col++)
        {
            var cell = ws.Cell(1, col);
            cell.Value = headers[col - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = XLColor.FromHtml("#1D4ED8");
        }

        if (pms != null && pms.Count > 0)
        {
            // Write current PM data rows
            for (int i = 0; i < pms.Count; i++)
            {
                var pm  = pms[i];
                int row = i + 2;
                ws.Cell(row, 1).Value = pm.FirstName;
                ws.Cell(row, 2).Value = pm.LastName;
                ws.Cell(row, 3).Value = pm.HomeOffice;
                ws.Cell(row, 4).Value = pm.GeographicalLocation;
                ws.Cell(row, 5).Value = pm.DataName;
                ws.Cell(row, 6).Value = pm.Capacity;
                ws.Cell(row, 7).Value = pm.IdealUtilization;
            }
        }
        else
        {
            // No data — show a single dimmed example row so the format is clear
            object[] example = ["Jane", "Smith", "FRE", "East", "jsmith", 1.0, 85.0];
            for (int col = 1; col <= example.Length; col++)
            {
                var cell = ws.Cell(2, col);
                if (example[col - 1] is double d) cell.Value = d;
                else cell.Value = (string)example[col - 1];
                cell.Style.Font.FontColor = XLColor.FromHtml("#94A3B8");
                cell.Style.Font.Italic = true;
            }
        }

        ws.Column(1).Width = 18; // First Name
        ws.Column(2).Width = 18; // Last Name
        ws.Column(3).Width = 16; // Home Office
        ws.Column(4).Width = 22; // Geographical Location
        ws.Column(5).Width = 22; // Data Name
        ws.Column(6).Width = 13; // Capacity
        ws.Column(7).Width = 24; // Ideal Utilization

        ws.SheetView.FreezeRows(1);

        // Home Office dropdown (column C)
        var hoRange = ws.Range("C2:C10000");
        var dvHo = hoRange.CreateDataValidation();
        dvHo.List("\"FRE,VAN,JAX,Remote\"");
        dvHo.ShowErrorMessage = true;
        dvHo.ErrorTitle = "Invalid Home Office";
        dvHo.ErrorMessage = "Please choose one of: FRE, VAN, JAX, Remote";

        // Geographical Location dropdown (column D)
        var geoRange = ws.Range("D2:D10000");
        var dvGeo = geoRange.CreateDataValidation();
        dvGeo.List("\"East,Central,West\"");
        dvGeo.ShowErrorMessage = true;
        dvGeo.ErrorTitle = "Invalid Region";
        dvGeo.ErrorMessage = "Please choose East, Central, or West";
    }

    private static void BuildInstructionsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Instructions");

        ws.Cell("A1").Value = "PM Bulk Import — Instructions";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;

        ws.Cell("A3").Value = "1. Fill in the \"PM Bulk Import\" sheet starting at row 2.";
        ws.Cell("A4").Value = "2. Delete or overwrite the example row (row 2) before importing.";
        ws.Cell("A5").Value = "3. Home Office: FRE, VAN, JAX, or Remote (column C dropdown).";
        ws.Cell("A6").Value = "4. Geographical Location: East, Central, or West (column D dropdown).";
        ws.Cell("A7").Value = "5. First Name and Last Name are required. All other fields are optional.";
        ws.Cell("A8").Value = "6. Capacity is decimal (e.g. 1.0 = full time). Ideal Utilization is 0–100.";

        ws.Cell("A10").Value = "Column";
        ws.Cell("B10").Value = "Field";
        ws.Cell("C10").Value = "Notes";
        foreach (var cell in ws.Range("A10:C10").Cells())
        {
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        string[,] cols =
        {
            { "A", "First Name",             "Required" },
            { "B", "Last Name",              "Required" },
            { "C", "Home Office",            "Required — FRE, VAN, JAX, or Remote" },
            { "D", "Geographical Location",  "Required — East, Central, or West" },
            { "E", "Data Name",              "Optional — short data-system identifier" },
            { "F", "Capacity",               "Optional — decimal (default 0)" },
            { "G", "Ideal Utilization (%)",  "Optional — number 0–100 (default 0)" },
        };
        for (int r = 0; r < cols.GetLength(0); r++)
        {
            ws.Cell(11 + r, 1).Value = cols[r, 0];
            ws.Cell(11 + r, 2).Value = cols[r, 1];
            ws.Cell(11 + r, 3).Value = cols[r, 2];
        }

        ws.Columns().AdjustToContents();
        ws.Column(3).Width = Math.Max(ws.Column(3).Width, 50);
        ws.Column(1).Width = 80;
        for (int r = 3; r <= 8; r++)
            ws.Cell(r, 1).Style.Alignment.WrapText = true;
    }

    public (List<PM> Pms, List<string> Errors) ParsePms(string filePath)
    {
        var pms    = new List<PM>();
        var errors = new List<string>();

        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.FirstOrDefault(s => s.Name == "PM Bulk Import")
                  ?? wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 2; row <= lastRow; row++)
        {
            var r          = ws.Row(row);
            var firstName  = r.Cell(1).GetString().Trim();
            var lastName   = r.Cell(2).GetString().Trim();
            var homeOffice = r.Cell(3).GetString().Trim();
            var geoLoc     = r.Cell(4).GetString().Trim();
            var dataName   = r.Cell(5).GetString().Trim();

            if (string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName)
                && string.IsNullOrEmpty(homeOffice)) continue;

            if (string.IsNullOrEmpty(firstName) || string.IsNullOrEmpty(lastName))
            {
                errors.Add($"Row {row}: First Name and Last Name are required — skipped.");
                continue;
            }

            if (!ValidHomeOffices.Contains(homeOffice, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Row {row} ({firstName} {lastName}): Home Office \"{homeOffice}\" is invalid — skipped.");
                continue;
            }

            if (!string.IsNullOrEmpty(geoLoc) && !ValidGeoLocations.Contains(geoLoc, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Row {row} ({firstName} {lastName}): Geographical Location \"{geoLoc}\" must be East, Central, or West — defaulting to empty.");
                geoLoc = "";
            }

            double capacity = 0, utilization = 0;
            var capCell  = r.Cell(6);
            var utilCell = r.Cell(7);

            if (!capCell.IsEmpty())
            {
                if (capCell.DataType == XLDataType.Number) capacity = capCell.GetDouble();
                else if (!double.TryParse(capCell.GetString(), out capacity))
                    errors.Add($"Row {row}: Capacity is not a number — defaulting to 0.");
            }
            if (!utilCell.IsEmpty())
            {
                if (utilCell.DataType == XLDataType.Number) utilization = utilCell.GetDouble();
                else if (!double.TryParse(utilCell.GetString(), out utilization))
                    errors.Add($"Row {row}: Ideal Utilization is not a number — defaulting to 0.");
            }

            pms.Add(new PM
            {
                FirstName            = firstName,
                LastName             = lastName,
                HomeOffice           = homeOffice,
                GeographicalLocation = geoLoc,
                DataName             = dataName,
                Capacity             = capacity,
                IdealUtilization     = utilization,
            });
        }

        return (pms, errors);
    }
}
