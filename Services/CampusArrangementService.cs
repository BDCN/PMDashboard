using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Places every Site under a Campus using the forecast sheets' campus ("az") column.
///
/// Rules (agreed 2026-10-06):
///  • The site→campus map comes from every forecast row that has a campus field
///    (ForecastField.IsCampusField — Type O "az", Type L's az-style "Cluster"). A forecast type
///    without such a column (M, B, P) is covered automatically: its sites are looked up in the
///    map the other sheets built, since the map is keyed by site code, not by sheet.
///  • Campus names are kept exactly as written (trimmed). If a site's rows disagree, the
///    spelling used by most of its rows wins.
///  • A site a user moved by hand (Site.CampusManual) is never moved by the forecast.
///  • A site the forecast knows nothing about keeps its campus, or — if it has none yet — gets a
///    campus of its own name.
///  • PM assignments are never changed here. A newly created campus takes the most common PM of
///    its sites; any site with a different PM becomes a site-level override.
/// Safe to run repeatedly; it only writes what changed.
/// </summary>
public static class CampusArrangementService
{
    public record Result(int SitesPlaced, int SitesMoved, int CampusesCreated, int DuplicatesMerged, int MappedSites);

    private static readonly object _gate = new();

    public static Result Arrange()
    {
        lock (_gate) // startup, post-sync and post-import calls can overlap; one at a time
        {
            using var db = new AppDbContext();
            var map = BuildSiteCampusMap(db);

            var sites    = db.Sites.Include(s => s.Campus).ToList();
            var campuses = db.Campuses.ToList();
            int placed = 0, moved = 0, merged = 0;
            var createdCampuses = new HashSet<Campus>();
            var touched         = new HashSet<Campus>();

            // 1. Merge duplicate campuses (same cluster + name — e.g. two machines both created
            //    "ATL089" before syncing): everything moves to the lowest Id, empty copies go.
            foreach (var dup in campuses.GroupBy(c => (c.LocationId, c.Name)).Where(g => g.Count() > 1))
            {
                var keep = dup.OrderBy(c => c.Id).First();
                foreach (var extra in dup.Where(c => c.Id != keep.Id))
                {
                    foreach (var s in sites.Where(s => s.CampusId == extra.Id))
                    {
                        s.Campus = keep; s.CampusId = keep.Id;
                    }
                    keep.AssignedPmId ??= extra.AssignedPmId;
                    db.Campuses.Remove(extra);
                    merged++;
                }
                touched.Add(keep);
            }
            if (merged > 0) campuses = campuses.Where(c => db.Entry(c).State != EntityState.Deleted).ToList();

            Campus Find(int locationId, string name) =>
                campuses.FirstOrDefault(c => c.LocationId == locationId && c.Name == name)
                ?? Create(locationId, name);
            Campus Create(int locationId, string name)
            {
                var c = new Campus { LocationId = locationId, Name = name };
                db.Campuses.Add(c);
                campuses.Add(c);
                createdCampuses.Add(c);
                return c;
            }

            // 2. Place / re-place sites.
            foreach (var site in sites)
            {
                if (site.CampusManual && site.CampusId != null) continue;

                var key = site.Name.Trim().ToUpperInvariant();
                string target;
                if (map.TryGetValue(key, out var fromForecast)) target = fromForecast;
                else if (site.CampusId != null) continue;          // forecast silent — keep what it has
                else target = site.Name.Trim();                    // no data — campus of its own name

                if (site.Campus != null && site.Campus.Name == target && site.Campus.LocationId == site.LocationId)
                    continue;

                var wasPlaced = site.CampusId != null;
                var campus = Find(site.LocationId, target);
                site.Campus = campus;
                if (campus.Id != 0) site.CampusId = campus.Id;
                touched.Add(campus);
                if (wasPlaced) moved++; else placed++;
            }

            // 3. New campuses take their sites' most common PM; then refresh override flags in every
            //    touched campus. No site's PM changes.
            foreach (var campus in touched)
            {
                var members = sites.Where(s => s.Campus == campus).ToList();
                if (createdCampuses.Contains(campus))
                    campus.AssignedPmId = members.Where(s => s.AssignedPmId != null)
                                                 .GroupBy(s => s.AssignedPmId)
                                                 .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                                                 .Select(g => g.Key).FirstOrDefault();
                foreach (var s in members)
                    s.PmOverride = campus.AssignedPmId != null && s.AssignedPmId != campus.AssignedPmId;
            }

            if (placed + moved + merged + createdCampuses.Count > 0)
            {
                db.SaveChanges();
                AuditService.Log("Arrange", "Campus", null,
                    $"Placed {placed} site(s), moved {moved}, created {createdCampuses.Count} campus(es), merged {merged} duplicate(s) from forecast data");
            }
            return new Result(placed, moved, createdCampuses.Count, merged, map.Count);
        }
    }

    /// <summary>Site code (upper-case) → campus name as written, by majority of that site's
    /// forecast rows across every sheet that has a campus column.</summary>
    public static Dictionary<string, string> BuildSiteCampusMap(AppDbContext db)
    {
        var campusFields = db.ForecastFields.AsNoTracking()
            .Where(f => f.IsCampusField && !f.IsIgnored)
            .Select(f => new { f.ForecastTypeId, f.Key })
            .ToList();
        var votes = new Dictionary<string, Dictionary<string, int>>();
        if (campusFields.Count == 0) return new();

        foreach (var row in db.ForecastRows.AsNoTracking().Select(r => new { r.ForecastTypeId, r.SiteCode, r.ValuesJson }))
        {
            if (string.IsNullOrWhiteSpace(row.SiteCode)) continue;
            var keys = campusFields.Where(f => f.ForecastTypeId == null || f.ForecastTypeId == row.ForecastTypeId)
                                   .Select(f => f.Key).ToList();
            if (keys.Count == 0) continue;

            Dictionary<string, string>? values;
            try { values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson); }
            catch { continue; }
            if (values == null) continue;

            foreach (var k in keys)
            {
                if (!values.TryGetValue(k, out var campus) || string.IsNullOrWhiteSpace(campus)) continue;
                campus = campus.Trim();
                if (campus.StartsWith("#")) continue; // spreadsheet errors like #N/A
                var site = row.SiteCode.Trim().ToUpperInvariant();
                if (!votes.TryGetValue(site, out var v)) votes[site] = v = new();
                v[campus] = v.GetValueOrDefault(campus) + 1;
                break;
            }
        }
        return votes.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key);
    }
}
