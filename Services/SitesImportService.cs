using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>Result of a Sites/ROMPs import run — see <see cref="SitesImportService.ImportSites"/>.</summary>
public record SitesImportResult(
    int LocationsCreated, int SitesCreated, int RompsCreated, int RompsUpdated, int RompsUnchanged, int Errors);

/// <summary>
/// Bulk-creates/updates Sites and ROMPs (with their current Status) from an Excel sheet —
/// a direct setup/seeding tool, unlike Orders import's JobName-derived auto-creation.
/// Re-importing an already-existing Site/ROMP updates its Status rather than skipping it, so
/// the same sheet can be re-downloaded, edited, and re-uploaded to reflect status changes.
/// Mirrors PmImportService's template/parse conventions.
/// </summary>
public class SitesImportService
{
    private const string SheetName = "Sites Bulk Import";
    private static readonly string[] ValidRegions = ["East", "Central", "West"];

    // Pass existingRomps (Site + Site.Location included) to pre-populate the template with the
    // current database state — one row per ROMP. Leave null/empty for a blank example row.
    public void GenerateTemplate(string filePath, List<Romp>? existingRomps = null)
    {
        using var wb = new XLWorkbook();
        BuildDataSheet(wb, existingRomps);
        BuildInstructionsSheet(wb);
        wb.SaveAs(filePath);
    }

    private static void BuildDataSheet(XLWorkbook wb, List<Romp>? romps)
    {
        var ws = wb.Worksheets.Add(SheetName);

        string[] headers = ["Location", "Region", "Site Name", "ROMP Name", "Status"];
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

        if (romps != null && romps.Count > 0)
        {
            var ordered = romps
                .OrderBy(r => r.Site?.Location?.Name ?? "")
                .ThenBy(r => r.Site?.Name ?? "")
                .ThenBy(r => r.Name)
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var r   = ordered[i];
                int row = i + 2;
                ws.Cell(row, 1).Value = r.Site?.Location?.Name ?? "";
                ws.Cell(row, 2).Value = r.Site?.Location?.GeographicalLocation ?? "";
                ws.Cell(row, 3).Value = r.Site?.Name ?? "";
                ws.Cell(row, 4).Value = r.Name;
                ws.Cell(row, 5).Value = r.Status;
            }
        }
        else
        {
            string[] example = ["IAD", "East", "IAD115", "ROMP01", "Construction"];
            for (int col = 1; col <= example.Length; col++)
            {
                var cell = ws.Cell(2, col);
                cell.Value = example[col - 1];
                cell.Style.Font.FontColor = XLColor.FromHtml("#94A3B8");
                cell.Style.Font.Italic = true;
            }
        }

        ws.Column(1).Width = 14; // Location
        ws.Column(2).Width = 12; // Region
        ws.Column(3).Width = 18; // Site Name
        ws.Column(4).Width = 16; // ROMP Name
        ws.Column(5).Width = 18; // Status

        ws.SheetView.FreezeRows(1);

        // Region dropdown (column B) — only used when the Location doesn't already exist
        var regionRange = ws.Range("B2:B10000");
        var dvRegion = regionRange.CreateDataValidation();
        dvRegion.List("\"East,Central,West\"");
        dvRegion.ShowErrorMessage = true;
        dvRegion.ErrorTitle = "Invalid Region";
        dvRegion.ErrorMessage = "Please choose East, Central, or West";

        // Status dropdown (column E) — built from the LIVE status list, since RompStatus is an
        // admin-configurable table (Config → Site/ROMP Statuses), not a fixed enum.
        var statusList = string.Join(",", StatusConfig.All);
        var statusRange = ws.Range("E2:E10000");
        var dvStatus = statusRange.CreateDataValidation();
        dvStatus.List($"\"{statusList}\"");
        dvStatus.ShowErrorMessage = true;
        dvStatus.ErrorTitle = "Invalid Status";
        dvStatus.ErrorMessage = $"Please choose one of: {statusList}";
    }

    private static void BuildInstructionsSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Instructions");

        ws.Cell("A1").Value = "Sites/ROMPs Bulk Import — Instructions";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;

        ws.Cell("A3").Value = "1. Fill in the \"Sites Bulk Import\" sheet starting at row 2 — one row per ROMP.";
        ws.Cell("A4").Value = "2. Delete or overwrite the example row (row 2) before importing.";
        ws.Cell("A5").Value = "3. If the Location doesn't exist yet, Region (East/Central/West, column B) is required to create it.";
        ws.Cell("A6").Value = "4. If the Location already exists, Region is ignored — it is not updated from this sheet.";
        ws.Cell("A7").Value = "5. If the Site already exists under a different Location than the sheet says, that row is skipped and reported.";
        ws.Cell("A8").Value = "6. If the Site/ROMP combination already exists, its Status is updated to match the sheet — re-download, edit, and re-upload to record status changes.";
        ws.Cell("A9").Value = "7. Status (column E) must be one of the statuses configured in Config → Site/ROMP Statuses (see dropdown).";

        ws.Cell("A11").Value = "Column";
        ws.Cell("B11").Value = "Field";
        ws.Cell("C11").Value = "Notes";
        foreach (var cell in ws.Range("A11:C11").Cells())
        {
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        string[,] cols =
        {
            { "A", "Location",   "Required — 3-letter code; created automatically if new" },
            { "B", "Region",     "Required only when creating a new Location — East, Central, or West" },
            { "C", "Site Name",  "Required — created automatically if new" },
            { "D", "ROMP Name",  "Required — created automatically if new" },
            { "E", "Status",     "Required — must match a configured ROMP status exactly" },
        };
        for (int r = 0; r < cols.GetLength(0); r++)
        {
            ws.Cell(12 + r, 1).Value = cols[r, 0];
            ws.Cell(12 + r, 2).Value = cols[r, 1];
            ws.Cell(12 + r, 3).Value = cols[r, 2];
        }

        ws.Columns().AdjustToContents();
        ws.Column(3).Width = Math.Max(ws.Column(3).Width, 60);
        ws.Column(1).Width = 90;
        for (int r = 3; r <= 9; r++)
            ws.Cell(r, 1).Style.Alignment.WrapText = true;
    }

    /// <summary>
    /// Parses and applies the sheet in one pass — same local-first, single-SaveChanges pattern
    /// as OrdersViewModel.UpsertOrders' Location/Site/ROMP auto-creation. Never throws for bad
    /// data; only file/IO-level exceptions bubble to the caller's try/catch.
    /// </summary>
    public (SitesImportResult Result, List<string> Errors) ImportSites(string filePath)
    {
        var errors = new List<string>();
        int locationsCreated = 0, sitesCreated = 0, rompsCreated = 0, rompsUpdated = 0, unchanged = 0;

        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.FirstOrDefault(s => s.Name == SheetName) ?? wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        using var db = new AppDbContext();

        var locationCache = db.Locations.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var siteCache      = db.Sites.Include(s => s.Location).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var rompCache      = db.Romps.Include(r => r.Site)
                                .ToDictionary(r => $"{r.Site.Name}|{r.Name}", StringComparer.OrdinalIgnoreCase);

        var validStatuses = StatusConfig.All;

        for (int row = 2; row <= lastRow; row++)
        {
            var r        = ws.Row(row);
            var location = r.Cell(1).GetString().Trim();
            var region   = r.Cell(2).GetString().Trim();
            var siteName = r.Cell(3).GetString().Trim();
            var rompName = r.Cell(4).GetString().Trim();
            var status   = r.Cell(5).GetString().Trim();

            if (string.IsNullOrEmpty(location) && string.IsNullOrEmpty(siteName) && string.IsNullOrEmpty(rompName))
                continue; // blank row

            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(siteName) || string.IsNullOrEmpty(rompName))
            {
                errors.Add($"Row {row}: Location, Site Name, and ROMP Name are all required — skipped.");
                continue;
            }

            if (string.IsNullOrEmpty(status) || !validStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Row {row} ({siteName}/{rompName}): Status \"{status}\" is not a configured ROMP status — skipped.");
                continue;
            }
            // Normalize to the canonical stored casing (dropdown protects against this in
            // practice, but a hand-typed value could differ only by case).
            status = validStatuses.First(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));

            // ── Site: find, or create (creating its Location first if needed) ──
            Site site;
            if (siteCache.TryGetValue(siteName, out var existingSite))
            {
                if (existingSite.Location != null &&
                    !string.Equals(existingSite.Location.Name, location, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Row {row}: Site \"{siteName}\" already exists under Location \"{existingSite.Location.Name}\", not \"{location}\" — skipped.");
                    continue;
                }
                site = existingSite;
            }
            else
            {
                if (!locationCache.TryGetValue(location, out var loc))
                {
                    if (!ValidRegions.Contains(region, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add($"Row {row}: Location \"{location}\" doesn't exist yet, and Region must be East, Central, or West to create it — skipped.");
                        continue;
                    }
                    loc = new Location { Name = location, GeographicalLocation = region };
                    db.Locations.Add(loc);
                    locationCache[location] = loc;
                    locationsCreated++;
                }
                site = new Site { Name = siteName, Location = loc };
                db.Sites.Add(site);
                siteCache[siteName] = site;
                sitesCreated++;
            }

            // ── ROMP: find (update Status if changed) or create ──
            var rompKey = $"{siteName}|{rompName}";
            if (rompCache.TryGetValue(rompKey, out var romp))
            {
                if (!string.Equals(romp.Status, status, StringComparison.Ordinal))
                {
                    romp.Status = status;
                    rompsUpdated++;
                }
                else unchanged++;
            }
            else
            {
                romp = new Romp { Name = rompName, Site = site, Status = status };
                db.Romps.Add(romp);
                rompCache[rompKey] = romp;
                rompsCreated++;
            }
        }

        db.SaveChanges();

        var result = new SitesImportResult(locationsCreated, sitesCreated, rompsCreated, rompsUpdated, unchanged, errors.Count);
        AuditService.Log("Import", "SitesRomps", details:
            $"Sites/ROMPs import: {locationsCreated} location(s), {sitesCreated} site(s), {rompsCreated} ROMP(s) created, " +
            $"{rompsUpdated} ROMP(s) updated, {errors.Count} error(s)");

        return (result, errors);
    }
}
