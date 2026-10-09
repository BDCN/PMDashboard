using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

/// <summary>One distinct forecast-sheet campus that has no matching Site yet. ClusterCode is
/// guessed from the Site Code's 3-letter airport-style prefix (e.g. "IAD922" → "IAD"), falling
/// back to the forecast row's own Cluster field text — in practice almost every real campus code
/// already carries its cluster as that prefix, and the prefix already matches an existing
/// Location far more often than not (checked against real data: ~90%).</summary>
public record UnmatchedCampus(string SiteCode, string ClusterCode, int? ExistingLocationId);

/// <summary>
/// One Gantt-style bar: a Cluster/Campus/Room Type combination, spanning the earliest NBD to the
/// latest PFHO among the forecast rows that share that Campus + Room Type. Forecast rows have no
/// direct ROMP link, so Room Type (a global forecast field, present on every type) stands in for
/// it — multiple rows for the same campus/room type are collapsed into one bar rather than drawn
/// separately, since they represent the same physical build.
/// </summary>
public enum ForecastBarStatus { NotStarted, InProgress, Completed }

/// <summary>Bar order on the Forecast Timeline. Default = cluster → campus → room type (load order).</summary>
public enum TimelineSort { Default, NbdAsc, NbdDesc, PfhoAsc, PfhoDesc }

public class ForecastTimelineRow
{
    public string   ClusterName { get; init; } = "";
    public string   Region      { get; init; } = "";
    public string   CampusName  { get; init; } = "";
    public string   RoomType    { get; init; } = "";
    public DateTime StartDate   { get; init; }
    public DateTime EndDate     { get; init; }
    public int      RowCount    { get; init; }

    // True when this bar's campus doesn't exist as a real Site yet — the Cluster/Campus text
    // comes straight from the forecast sheet's own columns (Cluster field, Site Code) instead of
    // our Location/Site hierarchy. Shown rather than dropped, since these are typically
    // planned/future builds that just haven't been entered into the system yet — but flagged so
    // it's clear they aren't verified against real records.
    public bool IsUnmatched { get; init; }

    // Null for unmatched campuses (nothing to compare against) and for matched campuses with no
    // PM currently assigned. Drives the PM compare filter — a bar only passes it when the
    // campus's real Site is actually assigned to one of the selected PMs right now.
    public int? AssignedPmId { get; init; }

    // Real Site.Id for a matched campus — null for an unmatched one. Used to assign a PM
    // directly (SuggestPms/AssignPmToForecastRow) without a fragile name-based lookup.
    public int? SiteId { get; init; }

    // Display name for AssignedPmId — null whenever that is. Purely for TooltipText; nothing
    // else needs the name, just the Id.
    public string? AssignedPmName { get; init; }

    // Set by ComputeConflicts (called at the end of Load) — see ScheduleConflict doc there.
    // ConflictCount is this bar's PM's total concurrent same-region CX-window sites, itself
    // included, so 1 always means "no conflict" even before the configured threshold is applied.
    // ConflictDetails is one formatted line per OTHER overlapping bar (not counting itself), for
    // TooltipText — which specific assignments are colliding and over what date range.
    public int          ConflictCount   { get; set; }
    public bool          HasConflict    { get; set; }
    public List<string>  ConflictDetails { get; set; } = new();

    // Set by ApplyScale — pixel position/width along the shared date axis.
    public double BarX     { get; set; }
    public double BarWidth { get; set; }

    public string SubLabel =>
        $"{ClusterName} · {RoomType} ({RowCount} item{(RowCount == 1 ? "" : "s")})" +
        (IsUnmatched ? " — not in system" : "");
    public string TooltipText =>
        $"{CampusName} — {RoomType}\n{StartDate:MMM d, yyyy}  →  {EndDate:MMM d, yyyy}\n{RowCount} forecast row(s)" +
        $"\n{BarStatus switch { ForecastBarStatus.NotStarted => "Not started", ForecastBarStatus.InProgress => "In progress", _ => "Completed" }}" +
        (IsAssigned ? $" · PM assigned{(AssignedPmName != null ? $" ({AssignedPmName})" : "")}" : "") +
        (HasConflict
            ? $"\n⚠ Schedule conflict — {AssignedPmName ?? "this PM"} has {ConflictCount} sites in {Region}'s CX window at once:\n" +
              string.Join("\n", ConflictDetails.Select(d => $"   • {d}"))
            : "") +
        (IsUnmatched ? "\n(no matching Site record yet)" : "");

    /// <summary>Progress state against today's date — drives the bar's fill color (blue/red/grey)
    /// independently of IsUnmatched's amber "not in system" flag, which still takes visual
    /// priority when both apply.</summary>
    public ForecastBarStatus BarStatus =>
        EndDate   < DateTime.Today ? ForecastBarStatus.Completed  :
        StartDate > DateTime.Today ? ForecastBarStatus.NotStarted :
        ForecastBarStatus.InProgress;

    /// <summary>True once the campus's real Site has a PM — fades the bar's status color toward
    /// green (see ForecastTimelinePage's bar style) as a quick "is this covered?" signal layered
    /// on top of the progress color.</summary>
    public bool IsAssigned => AssignedPmId != null;
}

public partial class ForecastTimelineViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<ForecastTimelineRow> _rows  = new();
    [ObservableProperty] private ObservableCollection<AxisTick>            _ticks = new();

    [ObservableProperty] private int    _barCount;
    [ObservableProperty] private int    _clusterCount;
    [ObservableProperty] private int    _excludedCount;      // rows dropped: missing/unparsable NBD or PFHO
    [ObservableProperty] private int    _newCampusBarCount;   // bars shown for campuses not in Sites yet
    [ObservableProperty] private int    _conflictBarCount;    // bars whose PM has too many same-region CX-window overlaps
    [ObservableProperty] private string _dateRangeText = "–";
    [ObservableProperty] private bool   _hasData;

    // ── Chart filters — applied on top of the already-loaded data, no DB re-query needed ──
    [ObservableProperty] private DateTime? _startDateFilter;
    [ObservableProperty] private DateTime? _endDateFilter;
    [ObservableProperty] private string    _selectedCluster = "All";
    // Page-local Region filter — East/Central/West, same values as the sidebar's Geographic
    // Location control (ApplyGeoFilter), but applied in-memory here like Cluster/PM/Campus rather
    // than re-querying the DB, so it can be set independently of the sidebar without affecting
    // every other page. A bar with no Region at all (unmatched campus — no real Site/Location to
    // derive one from) only ever shows under "All", same as it already does for the sidebar filter.
    [ObservableProperty] private string    _selectedRegion = "All";
    // "All" | "Assigned" | "Unassigned" — filters on ForecastTimelineRow.IsAssigned (the campus's
    // real Site has a PM right now). An unmatched campus (no real Site) is never assigned, so it
    // only ever shows under "All" or "Unassigned", same pattern as Region above.
    [ObservableProperty] private string    _selectedAssignment = "All";
    // Default OFF: bars whose end date has already passed are hidden unless this is checked.
    [ObservableProperty] private bool      _showPast;
    [ObservableProperty] private ObservableCollection<string> _clusterOptions = new();
    [ObservableProperty] private ObservableCollection<string> _regionOptions = new(new[] { "All", "East", "Central", "West" });
    [ObservableProperty] private ObservableCollection<string> _assignmentOptions = new(new[] { "All", "Assigned", "Unassigned" });

    // ── PM compare + campus scope — set via the filter-bar popups, not plain bound properties
    // (multi-select), so these are plain fields with a label the popups update directly. ──
    [ObservableProperty] private string _pmFilterLabel     = "All PMs";
    [ObservableProperty] private string _campusFilterLabel = "All Sites";
    [ObservableProperty] private ObservableCollection<PM>     _pmOptions     = new();
    [ObservableProperty] private ObservableCollection<string> _campusOptions = new();

    private HashSet<int>    _selectedPmIds   = new();
    private HashSet<string> _selectedCampuses = new(StringComparer.OrdinalIgnoreCase);

    private List<ForecastTimelineRow> _rawRows = new();
    private string _geoFilter  = "All";
    private double _lastWidth = 700;

    public void ApplyGeoFilter(string filter) { _geoFilter = filter; Load(); }

    // ── Sort ── NBD sorts on each bar's StartDate (earliest NBD of its rows), PFHO on its EndDate
    // (latest PFHO) — the same two dates the bar is drawn between. First click on a date sorts
    // earliest → latest, the next click flips it; the clear button goes back to Default.
    [ObservableProperty] private TimelineSort _sort = TimelineSort.Default;

    public string NbdSortLabel => Sort switch
    {
        TimelineSort.NbdAsc  => "NBD ↑ earliest first",
        TimelineSort.NbdDesc => "NBD ↓ latest first",
        _                    => "NBD",
    };
    public string PfhoSortLabel => Sort switch
    {
        TimelineSort.PfhoAsc  => "PFHO ↑ earliest first",
        TimelineSort.PfhoDesc => "PFHO ↓ latest first",
        _                     => "PFHO",
    };
    public bool IsSorted => Sort != TimelineSort.Default;

    public void ToggleNbdSort()  => Sort = Sort == TimelineSort.NbdAsc  ? TimelineSort.NbdDesc  : TimelineSort.NbdAsc;
    public void TogglePfhoSort() => Sort = Sort == TimelineSort.PfhoAsc ? TimelineSort.PfhoDesc : TimelineSort.PfhoAsc;
    public void ClearSort()      => Sort = TimelineSort.Default;

    partial void OnSortChanged(TimelineSort value)
    {
        OnPropertyChanged(nameof(NbdSortLabel));
        OnPropertyChanged(nameof(PfhoSortLabel));
        OnPropertyChanged(nameof(IsSorted));
        ApplyScale(_lastWidth);
    }

    /// <summary>Orders already-filtered bars by the chosen date. Ties fall back to the other
    /// date, then cluster/campus/room type, so equal-date bars keep a stable, readable order.</summary>
    private List<ForecastTimelineRow> SortRows(List<ForecastTimelineRow> rows)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<ForecastTimelineRow> ordered = Sort switch
        {
            TimelineSort.NbdAsc   => rows.OrderBy(r => r.StartDate).ThenBy(r => r.EndDate),
            TimelineSort.NbdDesc  => rows.OrderByDescending(r => r.StartDate).ThenByDescending(r => r.EndDate),
            TimelineSort.PfhoAsc  => rows.OrderBy(r => r.EndDate).ThenBy(r => r.StartDate),
            TimelineSort.PfhoDesc => rows.OrderByDescending(r => r.EndDate).ThenByDescending(r => r.StartDate),
            _ => null!,
        };
        if (ordered == null) return rows; // Default — _rawRows is already in cluster/campus/room order
        return ordered.ThenBy(r => r.ClusterName, cmp).ThenBy(r => r.CampusName, cmp).ThenBy(r => r.RoomType, cmp).ToList();
    }

    partial void OnStartDateFilterChanged(DateTime? value) => ApplyScale(_lastWidth);
    partial void OnEndDateFilterChanged(DateTime? value)   => ApplyScale(_lastWidth);
    partial void OnSelectedClusterChanged(string value)    => ApplyScale(_lastWidth);
    partial void OnSelectedRegionChanged(string value)     => ApplyScale(_lastWidth);
    partial void OnSelectedAssignmentChanged(string value) => ApplyScale(_lastWidth);
    partial void OnShowPastChanged(bool value)              => ApplyScale(_lastWidth);

    /// <summary>Applied from the PM filter popup's Apply button.</summary>
    public void SetPmFilter(List<PM> selected)
    {
        _selectedPmIds = selected.Select(p => p.Id).ToHashSet();
        PmFilterLabel = selected.Count == 0
            ? "All PMs"
            : selected.Count == 1
                ? selected[0].FullName
                : $"{selected.Count} PMs";
        ApplyScale(_lastWidth);
    }

    /// <summary>Applied from the Campus filter popup's Apply button.</summary>
    public void SetCampusFilter(List<string> selected)
    {
        _selectedCampuses = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        CampusFilterLabel = selected.Count == 0
            ? "All Sites"
            : selected.Count == 1
                ? selected[0]
                : $"{selected.Count} Sites";
        ApplyScale(_lastWidth);
    }

    public void Load()
    {
        using var db = new AppDbContext();

        // NBD/PFHO/Room Type are global forecast fields (ForecastTypeId == null) — shared across
        // every type rather than type-specific, so their Key is looked up by Name instead of
        // being hardcoded, since the auto-increment id behind "ffN" varies by install/history.
        var globalFields = db.ForecastFields.Where(f => f.ForecastTypeId == null).ToList();
        var nbdKey      = globalFields.FirstOrDefault(f => f.Name == "NBD")?.Key;
        var pfhoKey     = globalFields.FirstOrDefault(f => f.Name == "PFHO")?.Key;
        var roomTypeKey = globalFields.FirstOrDefault(f => f.Name == "Room Type")?.Key;
        var clusterKey  = globalFields.FirstOrDefault(f => f.Name == "Cluster")?.Key;

        if (nbdKey == null || pfhoKey == null)
        {
            _rawRows = new();
            ExcludedCount = 0;
            ClusterOptions = new ObservableCollection<string> { "All" };
            ApplyScale(_lastWidth);
            return;
        }

        var sites = db.Sites.Include(s => s.Location).AsNoTracking().ToDictionary(s => s.Id);
        var pmNames = db.PMs.AsNoTracking().ToDictionary(p => p.Id, p => p.FullName);
        var forecastRows = db.ForecastRows.AsNoTracking().ToList();

        int excluded = 0;
        var groups = new Dictionary<string, (string Cluster, string Region, string Campus, string RoomType,
            bool IsUnmatched, int? AssignedPmId, int? SiteId, DateTime Min, DateTime Max, int Count)>();

        foreach (var row in forecastRows)
        {
            Dictionary<string, string>? values;
            try { values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson); }
            catch { continue; }
            if (values == null) continue;

            if (!values.TryGetValue(nbdKey, out var nbdStr) || !values.TryGetValue(pfhoKey, out var pfhoStr)) { excluded++; continue; }
            if (!DateTime.TryParse(nbdStr, out var nbd) || !DateTime.TryParse(pfhoStr, out var pfho)) { excluded++; continue; }
            if (pfho < nbd) { excluded++; continue; } // bad data — would draw a negative-width bar

            string clusterName, region, campusName;
            bool isUnmatched;
            int? assignedPmId;
            int? matchedSiteId;
            if (row.SiteId != null && sites.TryGetValue(row.SiteId.Value, out var site))
            {
                clusterName   = site.Location?.Name ?? "—";
                region        = site.Location?.GeographicalLocation ?? "";
                campusName    = site.Name;
                isUnmatched   = false;
                assignedPmId  = site.AssignedPmId;
                matchedSiteId = site.Id;
            }
            else
            {
                // No matching Site yet — typically a planned/future build not entered into the
                // system. Shown anyway, labeled from the sheet's own Cluster/Site Code columns,
                // rather than silently dropped.
                clusterName = clusterKey != null && values.TryGetValue(clusterKey, out var c) && !string.IsNullOrWhiteSpace(c)
                    ? c
                    : "(Not in system)";
                // Site Codes are airport-code-prefixed (e.g. "ALE060" -> "ALE") even before a real
                // Site exists, so the region can still be resolved from AirportLookup — same prefix
                // guess GetUnmatchedCampuses uses for its cluster guess. Falls back to "" (shows
                // only under "All") for a code AirportLookup doesn't recognize.
                var airportPrefix = !string.IsNullOrWhiteSpace(row.SiteCode) && row.SiteCode.Length >= 3
                    ? row.SiteCode[..3]
                    : null;
                region       = AirportLookup.Lookup(airportPrefix)?.GeoRegion ?? "";
                campusName   = string.IsNullOrWhiteSpace(row.SiteCode) ? "(Unknown site)" : row.SiteCode;
                isUnmatched   = true;
                assignedPmId  = null; // nothing to compare a PM against — matches "if their site is in the sheet" only
                matchedSiteId = null;
            }

            if (_geoFilter != "All" && region != _geoFilter) continue;

            var roomType = roomTypeKey != null && values.TryGetValue(roomTypeKey, out var rt) && !string.IsNullOrWhiteSpace(rt)
                ? rt
                : "(unspecified)";

            var key = $"{(isUnmatched ? "u" : "m")}|{clusterName}|{campusName}|{roomType}";
            if (groups.TryGetValue(key, out var existing))
                groups[key] = (
                    existing.Cluster, existing.Region, existing.Campus, existing.RoomType, existing.IsUnmatched,
                    existing.AssignedPmId, existing.SiteId,
                    nbd < existing.Min ? nbd : existing.Min,
                    pfho > existing.Max ? pfho : existing.Max,
                    existing.Count + 1);
            else
                groups[key] = (clusterName, region, campusName, roomType, isUnmatched, assignedPmId, matchedSiteId, nbd, pfho, 1);
        }

        var result = groups.Values.Select(g => new ForecastTimelineRow
        {
            ClusterName  = g.Cluster,
            Region       = g.Region,
            CampusName   = g.Campus,
            RoomType     = g.RoomType,
            IsUnmatched    = g.IsUnmatched,
            AssignedPmId   = g.AssignedPmId,
            AssignedPmName = g.AssignedPmId.HasValue && pmNames.TryGetValue(g.AssignedPmId.Value, out var pmName) ? pmName : null,
            SiteId         = g.SiteId,
            StartDate    = g.Min,
            EndDate      = g.Max,
            RowCount     = g.Count,
        }).ToList();

        _rawRows = result
            .OrderBy(r => r.ClusterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.CampusName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RoomType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ComputeConflicts(_rawRows);
        ExcludedCount = excluded;

        // All clusters/campuses present in the loaded (geo-filtered) data — independent of the
        // other filters below, so a dropdown never loses an option just because another filter
        // currently hides everything under it.
        var clusters = _rawRows.Select(r => r.ClusterName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
        ClusterOptions = new ObservableCollection<string>(new[] { "All" }.Concat(clusters));
        if (!ClusterOptions.Contains(SelectedCluster, StringComparer.OrdinalIgnoreCase)) SelectedCluster = "All";

        var campusNames = _rawRows.Select(r => r.CampusName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
        CampusOptions = new ObservableCollection<string>(campusNames);

        PmOptions = new ObservableCollection<PM>(
            db.PMs.AsNoTracking().Where(p => p.IsActive)
                .OrderBy(p => p.FirstName).ThenBy(p => p.LastName).ToList());

        ApplyScale(_lastWidth);
    }

    public void ApplyScale(double availableWidth)
    {
        if (availableWidth > 0) _lastWidth = availableWidth;

        var filtered = SortRows(FilterRows(_rawRows));

        BarCount          = filtered.Count;
        ClusterCount      = filtered.Select(r => r.ClusterName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        NewCampusBarCount = filtered.Count(r => r.IsUnmatched);
        ConflictBarCount  = filtered.Count(r => r.HasConflict);
        HasData           = filtered.Count > 0;

        if (filtered.Count == 0)
        {
            DateRangeText = "–";
            Rows  = new ObservableCollection<ForecastTimelineRow>();
            Ticks = new ObservableCollection<AxisTick>();
            return;
        }
        if (_lastWidth <= 0) return;

        // The axis spans the chosen date-range filter when one is set, NOT the full extent of
        // whatever bars happen to overlap it — a bar that overlaps the window but runs well
        // beyond it (e.g. starts in range, ends years later) would otherwise stretch the whole
        // axis out to its real end date, defeating the point of filtering to a window at all.
        // Bars are clamped to the window below for the same reason — they're drawn only across
        // the visible portion of their span, not their full un-clamped length.
        var minDate = StartDateFilter ?? filtered.Min(r => r.StartDate);
        var maxDate = EndDateFilter   ?? filtered.Max(r => r.EndDate);
        DateRangeText = $"{minDate:MMM d, yyyy} – {maxDate:MMM d, yyyy}";

        var totalDays  = Math.Max(1.0, (maxDate - minDate).TotalDays);
        var scale      = _lastWidth / totalDays;

        foreach (var row in filtered)
        {
            var visibleStart = row.StartDate < minDate ? minDate : row.StartDate;
            var visibleEnd   = row.EndDate   > maxDate ? maxDate : row.EndDate;
            row.BarX     = (visibleStart - minDate).TotalDays * scale;
            row.BarWidth = Math.Max(3.0, (visibleEnd - visibleStart).TotalDays * scale);
        }

        // Tick spacing adapts to the total span — real forecast data here runs ~7 years end to
        // end, and labeling every month over that range would just overlap into noise. Step up
        // to quarterly/yearly ticks (January-only, year label) as the span grows; January ticks
        // are always drawn heavier as year markers regardless of step.
        var totalMonths = (maxDate.Year - minDate.Year) * 12 + (maxDate.Month - minDate.Month) + 1;
        var monthStep   = totalMonths <= 24 ? 1 : totalMonths <= 60 ? 3 : 12;

        var ticks  = new List<AxisTick>();
        var cursor = new DateTime(minDate.Year, minDate.Month, 1);
        int i = 0;
        while (cursor <= maxDate)
        {
            if (i % monthStep == 0)
            {
                var x = (cursor - minDate).TotalDays * scale;
                if (x >= 0)
                {
                    var label = monthStep >= 12 ? cursor.ToString("yyyy") : cursor.ToString("MMM yyyy");
                    ticks.Add(new AxisTick { Label = label, X = x, IsMajor = cursor.Month == 1 });
                }
            }
            cursor = cursor.AddMonths(1);
            i++;
        }

        Ticks = new ObservableCollection<AxisTick>(ticks);
        Rows  = new ObservableCollection<ForecastTimelineRow>(filtered);
    }

    /// <summary>
    /// Cluster / date-range / Show Past / PM-compare / Campus filters — applied in memory on top
    /// of the already-loaded rows, so adjusting any of them re-renders instantly with no DB round
    /// trip.
    /// Date range uses "overlaps the window" semantics (standard for a Gantt-style view): a bar
    /// is included as long as any part of its span falls between Start and End, not just bars
    /// that start or end strictly inside it.
    /// PM compare only matches a bar whose real Site is CURRENTLY assigned to one of the selected
    /// PMs — there's no PM-side date to plot, so this is a straight "is their campus in the
    /// forecast sheet" filter rather than a second set of bars.
    /// </summary>
    private List<ForecastTimelineRow> FilterRows(List<ForecastTimelineRow> source)
    {
        var today = DateTime.Today;
        return source.Where(r =>
                (SelectedCluster == "All" || string.Equals(r.ClusterName, SelectedCluster, StringComparison.OrdinalIgnoreCase)) &&
                (SelectedRegion == "All" || string.Equals(r.Region, SelectedRegion, StringComparison.OrdinalIgnoreCase)) &&
                (SelectedAssignment == "All" || (SelectedAssignment == "Assigned") == r.IsAssigned) &&
                (ShowPast || r.EndDate >= today) &&
                (StartDateFilter == null || r.EndDate   >= StartDateFilter.Value) &&
                (EndDateFilter   == null || r.StartDate <= EndDateFilter.Value) &&
                (_selectedPmIds.Count == 0 || (r.AssignedPmId != null && _selectedPmIds.Contains(r.AssignedPmId.Value))) &&
                (_selectedCampuses.Count == 0 || _selectedCampuses.Contains(r.CampusName)))
            .ToList();
    }

    /// <summary>
    /// Every distinct forecast-sheet campus (by Site Code) with no matching Site yet, across ALL
    /// forecast rows regardless of the current region filter — the add-campuses dialog should
    /// offer everything, not just whatever happens to be visible on the chart right now.
    /// </summary>
    public List<UnmatchedCampus> GetUnmatchedCampuses()
    {
        using var db = new AppDbContext();

        // Same most-recently-modified dedup as elsewhere in this app (ToSafeNameDictionary) —
        // this DB has at least one genuine duplicate-named Location (two "SHV" rows), and a
        // plain ToDictionary would throw on it.
        var locByName = db.Locations.AsNoTracking().ToList()
            .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.LastModified).First(),
                StringComparer.OrdinalIgnoreCase);

        var clusterKey = db.ForecastFields
            .Where(f => f.ForecastTypeId == null && f.Name == "Cluster")
            .Select(f => f.Key).FirstOrDefault();

        var rows = db.ForecastRows.AsNoTracking()
            .Where(r => r.SiteId == null)
            .Select(r => new { r.SiteCode, r.ValuesJson })
            .ToList();

        var seen = new Dictionary<string, UnmatchedCampus>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SiteCode) || seen.ContainsKey(row.SiteCode)) continue;

            var clusterGuess = row.SiteCode.Length >= 3 ? row.SiteCode[..3].ToUpperInvariant() : row.SiteCode;
            if (clusterKey != null)
            {
                try
                {
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson);
                    if (values != null && values.TryGetValue(clusterKey, out var c) && !string.IsNullOrWhiteSpace(c))
                        clusterGuess = c.Trim();
                }
                catch { /* malformed row — fall back to the prefix guess */ }
            }

            locByName.TryGetValue(clusterGuess, out var existingLoc);
            seen[row.SiteCode] = new UnmatchedCampus(row.SiteCode, clusterGuess, existingLoc?.Id);
        }

        return seen.Values
            .OrderBy(c => c.ClusterCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.SiteCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Creates a real Site (and, where needed, a brand-new Location) for each selected campus.
    /// <paramref name="newLocationRegions"/> supplies the chosen region for any ClusterCode that
    /// doesn't already exist — one new Location per distinct code, reused across every campus
    /// under it. Re-running Load() afterward is the caller's job, so the chart reflects the newly
    /// matched campuses (their bars move from the amber "not in system" state to normal).
    ///
    /// Batched deliberately: the original version re-queried the full ForecastRows table (tens of
    /// thousands of rows) once PER campus plus up to 3 SaveChanges() calls per campus, all
    /// synchronously — adding a batch of 100+ campuses froze the UI thread for minutes (Windows
    /// marks it "Not Responding") since this runs on the caller's thread. Now it's a handful of
    /// SaveChanges() calls total regardless of how many campuses are added, and CALLERS should
    /// still run this off the UI thread (see AddForecastCampusesDialog.BtnAdd_Click) since even a
    /// few large batched writes can take a visible moment.
    /// </summary>
    public int AddCampuses(List<UnmatchedCampus> toAdd, Dictionary<string, string> newLocationRegions)
    {
        using var db = new AppDbContext();

        var existingSiteNames = db.Sites.AsNoTracking()
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var locationIdByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Phase 1 — create every genuinely-new Location up front, one SaveChanges for all of them.
        // SaveChanges populates each tracked entity's Id in place, so the Location objects
        // themselves are the simplest way to read the new Ids back afterward.
        var newLocationsByCode = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
        foreach (var clusterCode in toAdd
                     .Where(c => c.ExistingLocationId == null)
                     .Select(c => c.ClusterCode)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!newLocationRegions.TryGetValue(clusterCode, out var region) || string.IsNullOrWhiteSpace(region))
                continue; // no region chosen for this new cluster — its campuses are skipped below
            var newLocation = new Location { Name = clusterCode, GeographicalLocation = region };
            db.Locations.Add(newLocation);
            newLocationsByCode[clusterCode] = newLocation;
        }
        db.SaveChanges();
        foreach (var (code, loc) in newLocationsByCode)
            locationIdByCode[code] = loc.Id;

        // Phase 2 — create every Site, one SaveChanges for all of them.
        var sitesByCampus = new Dictionary<UnmatchedCampus, Site>();
        foreach (var campus in toAdd)
        {
            if (existingSiteNames.Contains(campus.SiteCode)) continue; // defensive — shouldn't happen

            int locationId;
            if (campus.ExistingLocationId != null)
                locationId = campus.ExistingLocationId.Value;
            else if (locationIdByCode.TryGetValue(campus.ClusterCode, out var id))
                locationId = id;
            else
                continue; // no region chosen for this new cluster — skip until it is

            var site = new Site { Name = campus.SiteCode, LocationId = locationId };
            db.Sites.Add(site);
            sitesByCampus[campus] = site;
        }
        db.SaveChanges();

        // Phase 3 — re-point already-imported forecast rows at their new Sites, one query (not
        // one per campus) and one SaveChanges for the whole batch. The forecast rows for these
        // campuses were imported with SiteId left null — creating the Site doesn't retroactively
        // fix them on its own.
        var siteCodes = sitesByCampus.Keys.Select(c => c.SiteCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatchedRowsByCode = db.ForecastRows
            .Where(r => r.SiteId == null)
            .AsEnumerable() // SiteCode comparison needs to be case-insensitive; do that in memory
            .Where(r => r.SiteCode != null && siteCodes.Contains(r.SiteCode))
            .GroupBy(r => r.SiteCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (campus, site) in sitesByCampus)
        {
            if (unmatchedRowsByCode.TryGetValue(campus.SiteCode, out var rows))
                foreach (var row in rows)
                    row.SiteId = site.Id;

            AuditService.Log("Create", "Site", site.Id,
                $"Added \"{site.Name}\" under cluster {campus.ClusterCode} from Forecast Timeline review");
        }
        db.SaveChanges();

        if (sitesByCampus.Count > 0)
            try { CampusArrangementService.Arrange(); } catch { /* best-effort; runs again at next startup/sync */ }
        return sitesByCampus.Count;
    }

    // ── Schedule conflict detection ─────────────────────────────────────────────
    //
    // Real-world process this models: a ROMP in its CX (cutover) phase has a daily early-morning
    // standup at a fixed local time, so a PM physically cannot cover two CX-phase sites in the
    // same time zone at once. The forecast data has no separate CX sub-phase of its own — only
    // the combined NBD→PFHO span — so the CX window is approximated as the last CxWindowDays
    // before each bar's PFHO (clamped to not start before the bar's own NBD). Time zone is
    // approximated by the existing East/Central/West Region bucket, same one RegionMatch and the
    // sidebar geo filter already use elsewhere in the app — not a precise per-site time zone.
    // MaxConcurrentCxPerRegion is the configurable "some overlap is OK" tolerance: how many sites
    // one PM may have in the same region's CX window at once before it's flagged as a conflict.

    private static (int CxWindowDays, int MaxConcurrentPerRegion) LoadConflictConfig()
    {
        var cs = new ConfigService();
        int cxDays    = int.TryParse(cs.Get(ConfigKeys.CxWindowDays, "14"), out var d) ? d : 14;
        int maxPerReg = int.TryParse(cs.Get(ConfigKeys.MaxConcurrentCxPerRegion, "1"), out var m) ? m : 1;
        return (cxDays, maxPerReg);
    }

    private static (DateTime Start, DateTime End) CxWindow(ForecastTimelineRow row, int cxWindowDays)
    {
        var start = row.EndDate.AddDays(-cxWindowDays);
        if (start < row.StartDate) start = row.StartDate;
        return (start, row.EndDate);
    }

    private static bool Overlaps((DateTime Start, DateTime End) a, (DateTime Start, DateTime End) b)
        => a.Start <= b.End && b.Start <= a.End;

    /// <summary>
    /// Sets ConflictCount/HasConflict on every assigned, region-known row by counting, per PM,
    /// how many of their OTHER same-region bars have an overlapping CX window. Pairwise overlap
    /// rather than true sweep-line peak concurrency — simpler, and the right granularity for "does
    /// THIS bar collide with something else this PM already has," which is what the UI shows.
    /// </summary>
    private static void ComputeConflicts(List<ForecastTimelineRow> rows)
    {
        var (cxDays, maxPerRegion) = LoadConflictConfig();

        var assigned = rows.Where(r => r.AssignedPmId != null && !string.IsNullOrEmpty(r.Region)).ToList();
        var windows  = assigned.ToDictionary(r => r, r => CxWindow(r, cxDays));

        foreach (var row in assigned)
        {
            var window = windows[row];
            var overlappingRows = assigned.Where(other =>
                !ReferenceEquals(other, row) &&
                other.AssignedPmId == row.AssignedPmId &&
                string.Equals(other.Region, row.Region, StringComparison.OrdinalIgnoreCase) &&
                Overlaps(window, windows[other])).ToList();

            row.ConflictCount = overlappingRows.Count + 1; // +1 for itself, so 1 always reads as "no conflict"
            row.HasConflict   = row.ConflictCount > maxPerRegion;
            row.ConflictDetails = overlappingRows.Select(other =>
            {
                var w = windows[other];
                return $"{other.CampusName} — {other.RoomType}  (CX window {w.Start:MMM d} – {w.End:MMM d, yyyy})";
            }).ToList();
        }
    }

    /// <summary>One ranked PM candidate for SuggestPms — see that method's doc for the ranking.</summary>
    public record PmSuggestion(PM Pm, bool RegionMatch, int ClusterFamiliarity, bool WouldConflict, int ConflictCount, int ActiveSiteCount);

    /// <summary>
    /// Ranks active PMs as candidates to cover an unassigned (matched) campus/room-type bar.
    /// Sorted: no schedule conflict first, then region match, then existing familiarity with this
    /// same cluster, then fewer currently-active sites (prefer a less-loaded PM) — a plain
    /// multi-key sort rather than a fabricated weighted score, so the ordering is easy to explain.
    /// Deliberately self-contained here rather than threaded through the Assignment page's
    /// AssignmentRules engine (which is built around a known Site/ROMP, not a forecast bar that
    /// may still be unmatched) — see conversation notes for why.
    /// </summary>
    public List<PmSuggestion> SuggestPms(ForecastTimelineRow forRow)
    {
        using var db = new AppDbContext();
        var (cxDays, maxPerRegion) = LoadConflictConfig();
        var targetWindow = CxWindow(forRow, cxDays);

        // Team boundary: only PMs allowed on this bar's cluster (TeamRules); for a cluster not in
        // the system yet, the region rule.
        var rules = TeamRules.For(db);
        var at = forRow.SiteId is { } sid
            ? db.Sites.Where(s => s.Id == sid).Select(s => new { Loc = (int?)s.LocationId, s.CampusId, SiteId = (int?)s.Id }).FirstOrDefault()
            : db.Locations.Where(l => l.Name == forRow.ClusterName).Select(l => new { Loc = (int?)l.Id, CampusId = (int?)null, SiteId = (int?)null }).FirstOrDefault();
        int? locId = at?.Loc;
        var pms = db.PMs.AsNoTracking().Where(p => p.IsActive).AsEnumerable()
            .Where(p => locId is { } l
                ? rules.Allows(p.Id, l, at!.CampusId, at.SiteId)
                : string.IsNullOrEmpty(forRow.Region) || string.Equals(rules.PmTeam(p.Id).Region, forRow.Region, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var clusterFamiliarity = db.Sites.AsNoTracking()
            .Include(s => s.Location)
            .Where(s => s.AssignedPmId != null && s.Location != null && s.Location.Name == forRow.ClusterName)
            .GroupBy(s => s.AssignedPmId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var activeSiteCounts = db.Sites.AsNoTracking()
            .Where(s => s.AssignedPmId != null && !s.IsClosed)
            .GroupBy(s => s.AssignedPmId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var sameRegionBarsByPm = _rawRows
            .Where(r => r.AssignedPmId != null && string.Equals(r.Region, forRow.Region, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.AssignedPmId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var suggestions = pms.Select(pm =>
        {
            int overlapping = sameRegionBarsByPm.TryGetValue(pm.Id, out var bars)
                ? bars.Count(b => Overlaps(targetWindow, CxWindow(b, cxDays)))
                : 0;
            var conflictCount = overlapping + 1; // +1 for the bar being assigned

            return new PmSuggestion(
                Pm:                 pm,
                RegionMatch:        string.Equals(pm.GeographicalLocation, forRow.Region, StringComparison.OrdinalIgnoreCase),
                ClusterFamiliarity: clusterFamiliarity.GetValueOrDefault(pm.Id),
                WouldConflict:      conflictCount > maxPerRegion,
                ConflictCount:      conflictCount,
                ActiveSiteCount:    activeSiteCounts.GetValueOrDefault(pm.Id));
        })
        .OrderBy(s => s.WouldConflict)
        .ThenByDescending(s => s.RegionMatch)
        .ThenByDescending(s => s.ClusterFamiliarity)
        .ThenBy(s => s.ActiveSiteCount)
        .ThenBy(s => s.Pm.FullName)
        .ToList();

        return suggestions;
    }

    /// <summary>Assigns a PM to a matched bar's real Site and reloads so the bar's color/conflict
    /// state picks it up immediately. No-op (returns false) for an unmatched bar — there's no real
    /// Site yet to assign.</summary>
    public bool AssignPmToForecastRow(ForecastTimelineRow forRow, int pmId)
    {
        if (forRow.SiteId is not { } siteId) return false;

        using var db = new AppDbContext();
        var site = db.Sites.Find(siteId);
        if (site == null) return false;

        AssignmentHistoryService.RecordChange(db, site, pmId);
        db.SaveChanges();
        Load();
        return true;
    }
}
