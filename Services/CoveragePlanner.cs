namespace PMDashboard.Services;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Coverage Planner — pure, stateless planning logic (no EF, no WPF), linked directly into
// PMDashboard.Tests like ScoringEngine. CoverageDataService builds the inputs from the database;
// CoveragePlannerViewModel shows the outputs.
//
// The model, in one paragraph: every campus + room type in the forecast sheets is a WORK
// PACKAGE with a window from its earliest NBD to its latest PFHO. A package puts LOAD on whoever
// covers it: a light BuildWeight per day during the build phase (NBD → start of CX) and a heavy
// CxWeight per day during the CX window (the last CxWindowDays before PFHO — the same window the
// Forecast Timeline uses for conflicts). Load is averaged per calendar week. One PM can carry
// CapacityPerPm load at once. From that:
//   • a PM's schedule   = their packages' load, week by week
//   • an overlap        = weeks where a PM's load exceeds capacity
//   • a gap             = spare capacity; an unassigned package "fits" a PM if adding it never
//                         pushes them over capacity in any week of its window (same region only)
//   • the hiring need   = per region, total load ÷ capacity, rounded up, minus the PMs that
//                         region has — hire HireLeadDays before each increase.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One campus + room type from the forecast, with its dated window.</summary>
public record WorkPackage(
    string   Key,            // stable identity, e.g. "m|IAD|IAD123|ROM"
    int?     SiteId,         // null when the forecast's campus isn't in the system yet
    string   Campus,
    string   Cluster,
    string   Region,         // East | Central | West | "" (unknown)
    string   RoomType,
    DateTime Nbd,
    DateTime Pfho,
    int?     AssignedPmId,   // null = unassigned
    string   SubRegion = ""); // the cluster's team sub-region ("" = unmapped → region rule)

/// <summary>A PM; SubRegion is their default sub-region ("" when not on a team), Region the region
/// that implies (else their own), OtherSubRegions any further sub-regions they also cover.
/// Hiring counts a PM in their default area only.</summary>
public record PlannerPm(int Id, string Name, string Region, string SubRegion = "", IReadOnlyList<string>? OtherSubRegions = null);

public record PlannerSettings(
    double   BuildWeight,     // load per day during the build phase
    double   CxWeight,        // load per day during the CX window
    double   CapacityPerPm,   // load one PM can carry at once
    int      CxWindowDays,
    int      HireLeadDays,
    DateTime Today,
    int      HorizonWeeks);

public record PmSchedule(
    PlannerPm              Pm,
    double[]               WeeklyLoad,
    List<WorkPackage>      Packages,     // assigned packages active inside the horizon
    double                 PeakLoad,
    double                 CurrentLoad); // this week

public record OverloadPeriod(
    PlannerPm          Pm,
    DateTime           From,
    DateTime           To,            // exclusive (start of the first week back under capacity)
    double             PeakLoad,
    List<WorkPackage>  Packages);     // this PM's packages active during the period

public record GapFit(
    WorkPackage Package,
    PlannerPm   Pm,
    double      PeakLoadAfter,        // the PM's highest weekly load over the package's window, with it added
    double      SpareAfter);          // capacity − PeakLoadAfter

public record HiringStep(
    string            Region,         // the hiring area's label: a sub-region ("East 1") or a region
    int               Count,          // additional PMs needed from NeededBy on
    int               TotalShort,     // cumulative shortfall at that point
    DateTime          NeededBy,       // first week the extra PMs are needed
    DateTime          HireBy,         // NeededBy − HireLeadDays
    double            PeakLoad,       // region load in that week
    List<WorkPackage> UnassignedDrivers, // unassigned packages active that week — the sites to cover
    string            ParentRegion = "");    // East | Central | West — for region filtering

public record RegionMonth(string Region, DateTime Month, double PeakLoad, int PmsNeeded, int PmsAvailable, string ParentRegion = "")
{
    public int Shortfall => Math.Max(0, PmsNeeded - PmsAvailable);
}

public record CoveragePlan(
    DateTime[]                    WeekStarts,
    List<PmSchedule>              Schedules,
    List<WorkPackage>             UnassignedPackages,
    List<OverloadPeriod>          Overloads,
    List<GapFit>                  GapFits,
    List<HiringStep>              Hiring,
    List<RegionMonth>             RegionMonths,
    int                           UnknownRegionPackages,
    Dictionary<int, string>?      PmAreas = null);   // PM id → their hiring-area label

public static class CoveragePlanner
{
    /// <summary>
    /// The team boundary (same rule as TeamRules): a package on a cluster mapped to a sub-region
    /// can only go to that sub-region's PMs; an unmapped one to any PM in its region.
    /// </summary>
    public static bool CanTake(PlannerPm pm, WorkPackage p) =>
        !string.IsNullOrEmpty(p.SubRegion)
            ? string.Equals(pm.SubRegion, p.SubRegion, StringComparison.OrdinalIgnoreCase)
              || (pm.OtherSubRegions?.Contains(p.SubRegion, StringComparer.OrdinalIgnoreCase) ?? false)
            : !string.IsNullOrEmpty(p.Region) && string.Equals(pm.Region, p.Region, StringComparison.OrdinalIgnoreCase);

    private const double Eps = 1e-9;

    /// <summary>Monday of the week containing <paramref name="d"/>.</summary>
    public static DateTime WeekStart(DateTime d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // Monday = 0
        return d.Date.AddDays(-diff);
    }

    /// <summary>Start of the CX window — the last CxWindowDays before PFHO, never before NBD.</summary>
    public static DateTime CxStart(WorkPackage p, int cxWindowDays)
    {
        var s = p.Pfho.Date.AddDays(-cxWindowDays);
        return s < p.Nbd.Date ? p.Nbd.Date : s;
    }

    /// <summary>
    /// The package's average daily load during the 7-day week starting <paramref name="weekStart"/>.
    /// Days are whole calendar days; the window is NBD through PFHO inclusive.
    /// </summary>
    public static double WeekLoad(WorkPackage p, DateTime weekStart, PlannerSettings s)
    {
        var start = p.Nbd.Date;
        var end   = p.Pfho.Date.AddDays(1);          // exclusive
        var cx    = CxStart(p, s.CxWindowDays);
        var wEnd  = weekStart.AddDays(7);

        double buildDays = OverlapDays(start, cx,  weekStart, wEnd);
        double cxDays    = OverlapDays(cx,    end, weekStart, wEnd);
        return (buildDays * s.BuildWeight + cxDays * s.CxWeight) / 7.0;
    }

    private static double OverlapDays(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var from = aStart > bStart ? aStart : bStart;
        var to   = aEnd   < bEnd   ? aEnd   : bEnd;
        return to > from ? (to - from).TotalDays : 0;
    }

    /// <summary>The package's load for every week of the horizon.</summary>
    public static double[] LoadVector(WorkPackage p, DateTime[] weeks, PlannerSettings s)
    {
        var v = new double[weeks.Length];
        for (int i = 0; i < weeks.Length; i++) v[i] = WeekLoad(p, weeks[i], s);
        return v;
    }

    public static CoveragePlan Plan(
        IReadOnlyList<WorkPackage> packages,
        IReadOnlyList<PlannerPm>   pms,
        PlannerSettings            s)
    {
        var first = WeekStart(s.Today);
        var weeks = Enumerable.Range(0, Math.Max(1, s.HorizonWeeks)).Select(i => first.AddDays(7 * i)).ToArray();
        var horizonEnd = weeks[^1].AddDays(7);

        // Only packages whose window touches the horizon matter.
        var live = packages.Where(p => p.Pfho.Date >= first && p.Nbd.Date < horizonEnd).ToList();
        var loads = live.ToDictionary(p => p, p => LoadVector(p, weeks, s));

        var pmById = pms.ToDictionary(p => p.Id);
        // A package assigned to a PM who isn't active (or doesn't exist) still needs covering.
        bool IsAssigned(WorkPackage p) => p.AssignedPmId is { } id && pmById.ContainsKey(id);

        // ── PM schedules ──
        var schedules = new List<PmSchedule>();
        var pmLoad = new Dictionary<int, double[]>();
        foreach (var pm in pms)
        {
            var mine = live.Where(p => p.AssignedPmId == pm.Id).OrderBy(p => p.Nbd).ToList();
            var v = new double[weeks.Length];
            foreach (var p in mine)
            {
                var lv = loads[p];
                for (int i = 0; i < v.Length; i++) v[i] += lv[i];
            }
            pmLoad[pm.Id] = v;
            schedules.Add(new PmSchedule(pm, v, mine, v.Length > 0 ? v.Max() : 0, v.Length > 0 ? v[0] : 0));
        }

        // ── Overlaps: runs of weeks over capacity ──
        var overloads = new List<OverloadPeriod>();
        foreach (var sch in schedules)
        {
            var v = sch.WeeklyLoad;
            int i = 0;
            while (i < v.Length)
            {
                if (v[i] <= s.CapacityPerPm + Eps) { i++; continue; }
                int j = i;
                double peak = 0;
                while (j < v.Length && v[j] > s.CapacityPerPm + Eps) { peak = Math.Max(peak, v[j]); j++; }
                int from = i, to = j;
                var involved = sch.Packages
                    .Where(p => Enumerable.Range(from, to - from).Any(k => loads[p][k] > Eps))
                    .ToList();
                overloads.Add(new OverloadPeriod(sch.Pm, weeks[from], weeks[from].AddDays(7 * (to - from)), peak, involved));
                i = j;
            }
        }

        // ── Unassigned + gap fits ──
        var unassigned = live.Where(p => !IsAssigned(p)).OrderBy(p => p.Nbd).ToList();
        var fits = new List<GapFit>();
        foreach (var p in unassigned)
        {
            if (string.IsNullOrEmpty(p.Region) && string.IsNullOrEmpty(p.SubRegion)) continue;
            var lv = loads[p];
            foreach (var pm in pms.Where(x => CanTake(x, p)))
            {
                var v = pmLoad[pm.Id];
                double peakAfter = 0;
                bool fits1 = true;
                for (int k = 0; k < v.Length; k++)
                {
                    if (lv[k] <= Eps) continue;
                    var after = v[k] + lv[k];
                    if (after > s.CapacityPerPm + Eps) { fits1 = false; break; }
                    peakAfter = Math.Max(peakAfter, after);
                }
                if (fits1) fits.Add(new GapFit(p, pm, peakAfter, s.CapacityPerPm - peakAfter));
            }
        }

        // ── Hiring: per hiring area, ceil(total load ÷ capacity) vs PMs available ──
        // An area is a sub-region (its packages, its team's PMs). Packages on clusters with no
        // sub-region yet form a region-level area, staffed by that region's PMs who aren't on a
        // team — with no teams set up at all, that's simply the old per-region view.
        var hiring = new List<HiringStep>();
        var months = new List<RegionMonth>();
        var cmp = StringComparer.OrdinalIgnoreCase;
        (string Region, string Sub) AreaOfPm(PlannerPm p) => (p.Region ?? "", p.SubRegion ?? "");
        (string Region, string Sub) AreaOfPkg(WorkPackage p) => (p.Region ?? "", p.SubRegion ?? "");
        var areas = pms.Select(AreaOfPm).Concat(live.Select(AreaOfPkg))
            .Where(a => !string.IsNullOrEmpty(a.Region) || !string.IsNullOrEmpty(a.Sub))
            .Select(a => (Region: a.Region.Trim(), Sub: a.Sub.Trim()))
            .Distinct()
            .GroupBy(a => a.Sub != "" ? "S|" + a.Sub.ToUpperInvariant() : "R|" + a.Region.ToUpperInvariant())
            .Select(g => g.OrderBy(x => x.Region == "" ? 1 : 0).First())    // a sub-region is one area, whatever region its rows claim
            .OrderBy(a => a.Region, cmp).ThenBy(a => a.Sub == "" ? 1 : 0).ThenBy(a => a.Sub, cmp)
            .ToList();
        var regionsWithTeams = areas.Where(a => a.Sub != "").Select(a => a.Region).ToHashSet(cmp);
        string Label((string Region, string Sub) a) =>
            a.Sub != "" ? a.Sub : regionsWithTeams.Contains(a.Region) ? $"{a.Region} (no sub-region)" : a.Region;
        bool InArea((string Region, string Sub) item, (string Region, string Sub) area) =>
            area.Sub != "" ? cmp.Equals(item.Sub, area.Sub)
                           : item.Sub == "" && cmp.Equals(item.Region, area.Region);
        var pmAreas = pms.ToDictionary(p => p.Id, p => Label(AreaOfPm(p)));

        foreach (var area in areas)
        {
            var region = Label(area);
            var regionPkgs = live.Where(p => InArea(AreaOfPkg(p), area)).ToList();
            int available = pms.Count(p => InArea(AreaOfPm(p), area));

            var demand = new double[weeks.Length];
            foreach (var p in regionPkgs)
            {
                var lv = loads[p];
                for (int k = 0; k < demand.Length; k++) demand[k] += lv[k];
            }
            int Needed(double load) => s.CapacityPerPm <= Eps ? 0 : (int)Math.Ceiling(load / s.CapacityPerPm - Eps);

            int planned = 0; // extra PMs already in the plan for this region
            for (int k = 0; k < weeks.Length; k++)
            {
                int shortNow = Math.Max(0, Needed(demand[k]) - available);
                if (shortNow <= planned) continue;
                var drivers = regionPkgs.Where(p => !IsAssigned(p) && loads[p][k] > Eps).OrderBy(p => p.Pfho).ToList();
                hiring.Add(new HiringStep(region, shortNow - planned, shortNow, weeks[k],
                                          weeks[k].AddDays(-s.HireLeadDays), demand[k], drivers, area.Region));
                planned = shortNow;
            }

            foreach (var g in weeks.Select((w, k) => (w, k)).GroupBy(x => new DateTime(x.w.Year, x.w.Month, 1)))
            {
                double peak = g.Max(x => demand[x.k]);
                months.Add(new RegionMonth(region, g.Key, peak, Needed(peak), available, area.Region));
            }
        }

        return new CoveragePlan(
            weeks, schedules, unassigned, overloads,
            fits.OrderBy(f => f.Package.Nbd).ThenByDescending(f => f.SpareAfter).ToList(),
            hiring, months,
            live.Count(p => string.IsNullOrEmpty(p.Region) && string.IsNullOrEmpty(p.SubRegion)),
            pmAreas);
    }
}
