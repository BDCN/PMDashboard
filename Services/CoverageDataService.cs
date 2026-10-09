using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;

namespace PMDashboard.Services;

/// <summary>
/// Builds CoveragePlanner's inputs from the database: work packages from the imported forecast
/// rows, the active PM roster, and the planning settings from AppConfig.
///
/// Packages follow the Forecast Timeline's rules exactly (ForecastTimelineViewModel.Load) so both
/// pages agree on what a campus's window is: one package per campus + room type, spanning the
/// earliest NBD to the latest PFHO of its rows; rows with a missing/unparsable NBD or PFHO, or
/// PFHO before NBD, are skipped. Forecast campuses not yet in the system are included as
/// unassigned packages (region guessed from the airport-code prefix), since they still need a PM.
/// </summary>
/// <summary>
/// The Coverage Planner model's default settings — the single source for first-run seeding
/// (App.xaml.cs), fallbacks when a stored value is unreadable (LoadSettings), and Configuration's
/// "Reset to defaults" button. Values as calibrated on real data in Oct 2026 (see CLAUDE.md).
/// </summary>
public static class CoverageDefaults
{
    public const double CapacityPerPm = 3.0;
    public const double BuildWeight   = 0.25;
    public const double CxWeight      = 1.0;
    public const int    CxWindowDays  = 14;   // shared with the Forecast Timeline's conflict rule
    public const int    HireLeadDays  = 90;
    public const int    HorizonWeeks  = 104;
}

public static class CoverageDataService
{
    public record Inputs(
        List<WorkPackage> Packages,
        List<PlannerPm>   Pms,
        PlannerSettings   Settings,
        int               SkippedRows,
        List<UndatedSite> UnassignedWithoutForecast);

    /// <summary>An unassigned Site with no usable forecast dates — can't be scheduled, but is
    /// still listed so it isn't invisible on the Unassigned tab.</summary>
    public record UndatedSite(int SiteId, string Campus, string Cluster, string Region);

    public static PlannerSettings LoadSettings(DateTime? today = null)
    {
        var cs = new ConfigService();
        double D(string k, double def) =>
            double.TryParse(cs.Get(k, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        int I(string k, int def) => int.TryParse(cs.Get(k, def.ToString()), out var v) ? v : def;

        return new PlannerSettings(
            BuildWeight:   Math.Max(0, D(ConfigKeys.CoverageBuildWeight, CoverageDefaults.BuildWeight)),
            CxWeight:      Math.Max(0, D(ConfigKeys.CoverageCxWeight, CoverageDefaults.CxWeight)),
            CapacityPerPm: Math.Max(0.01, D(ConfigKeys.CoverageCapacityPerPm, CoverageDefaults.CapacityPerPm)),
            CxWindowDays:  Math.Max(0, I(ConfigKeys.CxWindowDays, CoverageDefaults.CxWindowDays)),
            HireLeadDays:  Math.Max(0, I(ConfigKeys.CoverageHireLeadDays, CoverageDefaults.HireLeadDays)),
            Today:         (today ?? DateTime.Today).Date,
            HorizonWeeks:  Math.Clamp(I(ConfigKeys.CoverageHorizonWeeks, CoverageDefaults.HorizonWeeks), 4, 520));
    }

    public static Inputs Load(DateTime? today = null)
    {
        using var db = new AppDbContext();

        var globalFields = db.ForecastFields.AsNoTracking().Where(f => f.ForecastTypeId == null).ToList();
        var nbdKey      = globalFields.FirstOrDefault(f => f.Name == "NBD")?.Key;
        var pfhoKey     = globalFields.FirstOrDefault(f => f.Name == "PFHO")?.Key;
        var roomTypeKey = globalFields.FirstOrDefault(f => f.Name == "Room Type")?.Key;
        var clusterKey  = globalFields.FirstOrDefault(f => f.Name == "Cluster")?.Key;

        // Fallback NBD per forecast type: some sheets have no column mapped to the global "NBD"
        // field but carry their own NBD-named date column instead — e.g. the LV sheet's
        // "NBD _EDD" (13k+ rows, the largest forecast) — so without this those campuses would be
        // invisible to planning. Only NBD-named Date fields qualify; EDD (equipment delivery) is
        // deliberately not used as a start date.
        var typeNbdKeys = db.ForecastFields.AsNoTracking()
            .Where(f => f.ForecastTypeId != null && f.FieldType == "Date" && !f.IsIgnored && f.Name.StartsWith("NBD"))
            .ToList()
            .GroupBy(f => f.ForecastTypeId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.SortOrder).Select(f => f.Key).ToList());

        var sites = db.Sites.AsNoTracking().Include(s => s.Location).ToDictionary(s => s.Id);
        // Team boundary: each PM carries their PE's sub-region (and that sub-region's region);
        // each package its cluster's sub-region — CoveragePlanner.CanTake matches the two.
        var teams = TeamRules.For(db);
        // Forecast site → campus (az), so a forecast site not in the system yet still lands in its
        // campus's sub-region when that campus is split off from its cluster.
        var siteCampus = CampusArrangementService.BuildSiteCampusMap(db);
        var pms = db.PMs.AsNoTracking().Where(p => p.IsActive)
                        .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
                        .AsEnumerable()
                        .Select(p =>
                        {
                            var (sub, region) = teams.PmTeam(p.Id);
                            return new PlannerPm(p.Id, p.FullName, region, sub, teams.PmSubRegions(p.Id).Skip(1).ToList());
                        })
                        .ToList();

        var groups  = new Dictionary<string, WorkPackage>();
        int skipped = 0;
        var datedSiteIds = new HashSet<int>();

        if (nbdKey != null && pfhoKey != null)
        {
            foreach (var row in db.ForecastRows.AsNoTracking().Select(r => new { r.SiteId, r.SiteCode, r.ForecastTypeId, r.ValuesJson }))
            {
                Dictionary<string, string>? values;
                try { values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson); }
                catch { skipped++; continue; }
                if (values == null) { skipped++; continue; }

                DateTime? TryDate(string? k) =>
                    k != null && values.TryGetValue(k, out var str) && DateTime.TryParse(str, out var d) ? d : null;

                var nbdOpt = TryDate(nbdKey);
                if (nbdOpt == null && typeNbdKeys.TryGetValue(row.ForecastTypeId, out var fallbacks))
                    nbdOpt = fallbacks.Select(TryDate).FirstOrDefault(d => d != null);
                var pfhoOpt = TryDate(pfhoKey);

                if (nbdOpt is not { } nbd || pfhoOpt is not { } pfho || pfho < nbd) { skipped++; continue; }

                var roomType = roomTypeKey != null && values.TryGetValue(roomTypeKey, out var rt) && !string.IsNullOrWhiteSpace(rt)
                    ? rt : "(unspecified)";

                string cluster, region, campus, key, subRegion;
                int? siteId, pmId;
                if (row.SiteId is { } sid && sites.TryGetValue(sid, out var site))
                {
                    cluster = site.Location?.Name ?? "—";
                    region  = site.Location?.GeographicalLocation ?? "";
                    subRegion = teams.EffectiveArea(site.LocationId, site.CampusId).SubRegion; // campus's own, else cluster's
                    campus  = site.Name;
                    siteId  = site.Id;
                    // Same effective owner as the rest of the app: the campus's PM, or failing that
                    // the cluster's directly-assigned PM (zero-campus clusters).
                    pmId    = site.AssignedPmId ?? site.Location?.AssignedPmId;
                    key     = $"m|{site.Id}|{roomType}";
                    datedSiteIds.Add(site.Id);
                }
                else
                {
                    cluster = clusterKey != null && values.TryGetValue(clusterKey, out var c) && !string.IsNullOrWhiteSpace(c)
                        ? c : "(Not in system)";
                    var prefix = !string.IsNullOrWhiteSpace(row.SiteCode) && row.SiteCode.Length >= 3 ? row.SiteCode[..3] : null;
                    region  = AirportLookup.Lookup(prefix)?.GeoRegion ?? "";
                    // A forecast site not in the system can still be on a known cluster.
                    subRegion = teams.SubRegionForForecast(prefix ?? cluster,
                        row.SiteCode is { } sc && siteCampus.TryGetValue(sc.Trim().ToUpperInvariant(), out var cn) ? cn : null);
                    campus  = string.IsNullOrWhiteSpace(row.SiteCode) ? "(Unknown site)" : row.SiteCode!;
                    siteId  = null;
                    pmId    = null;
                    key     = $"u|{campus}|{roomType}";
                }

                if (groups.TryGetValue(key, out var g))
                    groups[key] = g with
                    {
                        Nbd  = nbd  < g.Nbd  ? nbd  : g.Nbd,
                        Pfho = pfho > g.Pfho ? pfho : g.Pfho,
                    };
                else
                    groups[key] = new WorkPackage(key, siteId, campus, cluster, region, roomType, nbd, pfho, pmId, subRegion);
            }
        }

        var activePmIds = pms.Select(p => p.Id).ToHashSet();
        var undated = sites.Values
            .Where(s => !datedSiteIds.Contains(s.Id)
                        && !s.IsClosed
                        && !((s.AssignedPmId ?? s.Location?.AssignedPmId) is { } pm && activePmIds.Contains(pm)))
            .OrderBy(s => s.Location?.Name).ThenBy(s => s.Name)
            .Select(s => new UndatedSite(s.Id, s.Name, s.Location?.Name ?? "—", s.Location?.GeographicalLocation ?? ""))
            .ToList();

        return new Inputs(groups.Values.ToList(), pms, LoadSettings(today), skipped, undated);
    }
}
