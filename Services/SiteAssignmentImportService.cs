using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>Result of a Site Assignments import run — see <see cref="SiteAssignmentImportService.ImportAssignments"/>.</summary>
/// <summary>OutsideTeam = rows held back because the PM isn't on the team for that site (re-run
/// inside TeamRules.AllowOutsideTeam to import them after the user acknowledges).</summary>
public record SiteAssignmentImportResult(int Assigned, int Unchanged, int Conflicts, int Errors, int OutsideTeam = 0);

/// <summary>
/// Bulk-assigns PMs to already-existing Sites from an Excel sheet — for recording assignments
/// that are already in place, not for creating new Sites (see SitesImportService for that).
/// Mirrors PmImportService's template/parse conventions.
/// </summary>
public class SiteAssignmentImportService
{
    private const string SheetName = "Site Assignments";

    // Pass existingSites to pre-populate the template with current assignments (including
    // unassigned ones, so the sheet doubles as an editable export of today's state).
    // Leave null/empty to generate a blank template with a dimmed example row.
    // Pass activePms to give the PM Data Name column an in-cell dropdown of active PMs —
    // leave null/empty to skip the dropdown (the column still works via free-text entry).
    public void GenerateTemplate(string filePath, List<Site>? existingSites = null, List<PM>? activePms = null)
    {
        using var wb = new XLWorkbook();
        BuildDataSheet(wb, existingSites, activePms);
        BuildInstructionsSheet(wb);
        wb.SaveAs(filePath);
    }

    private static void BuildDataSheet(XLWorkbook wb, List<Site>? sites, List<PM>? activePms)
    {
        var ws = wb.Worksheets.Add(SheetName);

        string[] headers = ["Cluster", "Site Name", "PM Data Name"];
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

        if (sites != null && sites.Count > 0)
        {
            var ordered = sites
                .OrderBy(s => s.Location?.Name ?? "")
                .ThenBy(s => s.Name)
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var s   = ordered[i];
                int row = i + 2;
                ws.Cell(row, 1).Value = s.Location?.Name ?? "";
                ws.Cell(row, 2).Value = s.Name;
                ws.Cell(row, 3).Value = s.AssignedPm?.DataName ?? "";
            }
        }
        else
        {
            string[] example = ["IAD", "IAD115", "jsmith"];
            for (int col = 1; col <= example.Length; col++)
            {
                var cell = ws.Cell(2, col);
                cell.Value = example[col - 1];
                cell.Style.Font.FontColor = XLColor.FromHtml("#94A3B8");
                cell.Style.Font.Italic = true;
            }
        }

        ws.Column(1).Width = 14; // Location
        ws.Column(2).Width = 20; // Site Name
        ws.Column(3).Width = 22; // PM Data Name

        ws.SheetView.FreezeRows(1);

        // PM Data Name dropdown (column C) — active PMs only. A plain inline list (the
        // technique used for Home Office/Region elsewhere) is capped at ~255 characters by
        // Excel, which a real PM roster's Data Names would likely exceed, so this uses a
        // range-reference dropdown against a hidden helper sheet instead.
        if (activePms is { Count: > 0 })
        {
            var listWs = wb.Worksheets.Add("Lists");
            listWs.Cell(1, 1).Value = "Active PM Data Names";
            var ordered = activePms.OrderBy(p => p.DataName).ToList();
            for (int i = 0; i < ordered.Count; i++)
                listWs.Cell(i + 2, 1).Value = ordered[i].DataName;
            listWs.Visibility = XLWorksheetVisibility.Hidden;

            var pmRange = listWs.Range(2, 1, ordered.Count + 1, 1);
            var dvPm = ws.Range("C2:C10000").CreateDataValidation();
            dvPm.List(pmRange);
            dvPm.ShowErrorMessage = true;
            dvPm.ErrorTitle = "Invalid PM Data Name";
            dvPm.ErrorMessage = "Please choose a PM from the dropdown list.";
        }
    }

    private static void BuildInstructionsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Instructions");

        ws.Cell("A1").Value = "Site Assignments Import — Instructions";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;

        ws.Cell("A3").Value = "1. Fill in the \"Site Assignments\" sheet starting at row 2.";
        ws.Cell("A4").Value = "2. Delete or overwrite the example row (row 2) before importing.";
        ws.Cell("A5").Value = "3. Cluster and Site Name must both match an existing Site exactly — this import does not create new Sites.";
        ws.Cell("A6").Value = "4. PM Data Name has a dropdown of active PMs — click the cell and use the arrow, or type an exact Data Name.";
        ws.Cell("A7").Value = "5. If a Site is already assigned to a different PM than the sheet says, that row is skipped and reported — it is not overwritten.";
        ws.Cell("A8").Value = "6. To unassign a PM from a site, use the Assignment or PM Detail page instead — this importer only assigns.";

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
            { "A", "Cluster",       "Required — must match an existing Cluster's code exactly" },
            { "B", "Site Name",   "Required — must match an existing Site under that Cluster" },
            { "C", "PM Data Name",  "Required — pick from the dropdown (active PMs only), or type an exact Data Name" },
        };
        for (int r = 0; r < cols.GetLength(0); r++)
        {
            ws.Cell(11 + r, 1).Value = cols[r, 0];
            ws.Cell(11 + r, 2).Value = cols[r, 1];
            ws.Cell(11 + r, 3).Value = cols[r, 2];
        }

        ws.Columns().AdjustToContents();
        ws.Column(3).Width = Math.Max(ws.Column(3).Width, 60);
        ws.Column(1).Width = 90;
        for (int r = 3; r <= 8; r++)
            ws.Cell(r, 1).Style.Alignment.WrapText = true;
    }

    /// <summary>
    /// Parses and applies the sheet in one pass (site/PM lookups need live DB access anyway).
    /// Returns counts plus a message per skipped/conflicting row; never throws for bad data —
    /// only file/IO-level exceptions bubble to the caller's try/catch.
    /// </summary>
    public (SiteAssignmentImportResult Result, List<string> Messages) ImportAssignments(string filePath)
    {
        var messages = new List<string>();
        int assigned = 0, unchanged = 0, conflicts = 0, errors = 0;
        int outsideTeam = 0;

        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.FirstOrDefault(s => s.Name == SheetName) ?? wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        using var db = new AppDbContext();
        var sites = db.Sites.Include(s => s.Location).Include(s => s.AssignedPm).ToList();
        var pms   = db.PMs.ToList();

        for (int row = 2; row <= lastRow; row++)
        {
            var r        = ws.Row(row);
            var location = r.Cell(1).GetString().Trim();
            var siteName = r.Cell(2).GetString().Trim();
            var pmData   = r.Cell(3).GetString().Trim();

            if (string.IsNullOrEmpty(location) && string.IsNullOrEmpty(siteName) && string.IsNullOrEmpty(pmData))
                continue; // blank row

            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(siteName))
            {
                messages.Add($"Row {row}: Cluster and Site Name are both required — skipped.");
                errors++;
                continue;
            }

            if (string.IsNullOrEmpty(pmData))
            {
                messages.Add($"Row {row} ({siteName}): PM Data Name is required — skipped.");
                errors++;
                continue;
            }

            var site = sites.FirstOrDefault(s =>
                string.Equals(s.Name, siteName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Location?.Name, location, StringComparison.OrdinalIgnoreCase));

            if (site == null)
            {
                // Distinguish "wrong Location" from "doesn't exist at all" — the former usually
                // means a typo in the Location column rather than a missing Site.
                bool existsElsewhere = sites.Any(s => string.Equals(s.Name, siteName, StringComparison.OrdinalIgnoreCase));
                messages.Add(existsElsewhere
                    ? $"Row {row}: Site \"{siteName}\" exists, but not under Cluster \"{location}\" — skipped."
                    : $"Row {row}: Site \"{siteName}\" not found under Cluster \"{location}\" — skipped.");
                errors++;
                continue;
            }

            var pm = pms.FirstOrDefault(p => string.Equals(p.DataName, pmData, StringComparison.OrdinalIgnoreCase));
            if (pm == null)
            {
                messages.Add($"Row {row} ({siteName}): PM Data Name \"{pmData}\" not found — skipped.");
                errors++;
                continue;
            }

            if (site.AssignedPmId == pm.Id)
            {
                unchanged++;
                continue;
            }

            if (site.AssignedPmId.HasValue)
            {
                messages.Add($"Row {row} ({siteName}): already assigned to {site.AssignedPm?.FullName ?? "another PM"} — not changed.");
                conflicts++;
                continue;
            }

            if (TeamRules.For(db).Reason(pm.Id, site.LocationId, site.CampusId, site.Id) is { } why && !TeamRules.OutsideTeamAllowed)
            {
                messages.Add($"Row {row} ({siteName}): outside the team — {why} — not imported.");
                outsideTeam++;
                continue;
            }

            AssignmentHistoryService.RecordChange(db, site, pm.Id); // site came from a tracked query on this same db context
            assigned++;
        }

        if (assigned > 0) db.SaveChanges();

        var result = new SiteAssignmentImportResult(assigned, unchanged, conflicts, errors, outsideTeam);
        AuditService.Log("Import", "SiteAssignments", details:
            $"Site assignment import: {assigned} assigned, {unchanged} unchanged, {conflicts} conflict(s), {errors} error(s)");

        return (result, messages);
    }
}
