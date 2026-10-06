using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;
using System.Collections.ObjectModel;

namespace PMDashboard.ViewModels;

/// <summary>How many campuses and individual ROMP overrides a PM currently holds — used to
/// decide whether deactivating/deleting them needs to ask what happens to those assignments.</summary>
public record PmAssignmentSummary(int CampusCount, int RompOverrideCount)
{
    public int Total => CampusCount + RompOverrideCount;
}

public partial class PmMaintenanceViewModel : ObservableObject
{
    private string _geoFilter = "All";

    [ObservableProperty] private ObservableCollection<PM> _pms = new();
    [ObservableProperty] private PM? _selectedPm;
    [ObservableProperty] private string _searchText = "";

    // Called from MainViewModel when the filter changes or the page is navigated to.
    public void ApplyGeoFilter(string filter)
    {
        _geoFilter = filter;
        Load();
    }

    public void Load()
    {
        using var db = new AppDbContext();
        IQueryable<PM> query = db.PMs.AsNoTracking();

        if (_geoFilter != "All")
            query = query.Where(p => p.GeographicalLocation == _geoFilter);

        var list = query.OrderBy(p => p.LastName)
                        .ThenBy(p => p.FirstName)
                        .ToList();
        Pms = new ObservableCollection<PM>(list);
    }

    public void AddPm(PM pm)
    {
        using var db = new AppDbContext();
        db.PMs.Add(pm);
        db.SaveChanges();
        Load();
        // Load() reloads via AsNoTracking(), so the new row is a different object instance
        // than `pm` — re-find it by the Id SaveChanges just assigned so the grid can select/
        // scroll to it, instead of the new PM silently landing wherever it sorts in the list.
        SelectedPm = Pms.FirstOrDefault(p => p.Id == pm.Id);
    }

    public void UpdatePm(PM updated)
    {
        using var db = new AppDbContext();
        var tracked = db.PMs.Find(updated.Id);
        if (tracked == null) return;
        db.Entry(tracked).CurrentValues.SetValues(updated);
        db.SaveChanges();
        Load();
    }

    public void DeletePm(PM pm)
    {
        using var db = new AppDbContext();
        db.PMs.Remove(pm);
        db.SaveChanges();

        // A local-only delete doesn't propagate — CloudSyncService only ever compares
        // LastModified on rows that exist on both sides; it has no concept of "this row should
        // be removed", so it can't detect a deletion at all. Without also deleting from the
        // cloud directly here, the very next full pull (which runs on EVERY app launch, not
        // just once — see DoInitialPullAsync) would silently re-insert this PM right back into
        // local, since from the sync's point of view a row missing locally just looks like a
        // row that needs to be (re)created. This is what caused deleted PMs to "come back".
        if (DatabaseConfig.IsCloud)
        {
            try
            {
                using var cloud = new AppDbContext(forceCloud: true);
                var cloudPm = cloud.PMs.Find(pm.Id);
                if (cloudPm != null)
                {
                    cloud.PMs.Remove(cloudPm);
                    cloud.SaveChanges();
                }
            }
            catch
            {
                // Best-effort — if the cloud delete fails (offline, etc.) the PM may still
                // reappear on a future pull; the local delete above has still succeeded.
            }
        }

        Load();
    }

    /// <summary>How many campuses and individual ROMP overrides this PM currently holds.</summary>
    public PmAssignmentSummary GetAssignmentSummary(int pmId)
    {
        using var db = new AppDbContext();
        int campusCount = db.Sites.Count(s => s.AssignedPmId == pmId);
        int rompCount   = db.Romps.Count(r => r.AssignedPmId == pmId);
        return new PmAssignmentSummary(campusCount, rompCount);
    }

    /// <summary>
    /// Moves every campus assignment and individual ROMP override currently held by fromPmId
    /// over to toPmId (or clears them if toPmId is null), preserving assignment history on both
    /// sides via AssignmentHistoryService — used when deactivating or deleting a PM that still
    /// has active assignments.
    /// </summary>
    public void ReassignAll(int fromPmId, int? toPmId)
    {
        using var db = new AppDbContext();

        // Team boundary: all or nothing — name every cluster the new PM can't take.
        var sites = db.Sites.Include(s => s.Campus).Where(s => s.AssignedPmId == fromPmId).ToList();
        var romps = db.Romps.Include(r => r.Site).Where(r => r.AssignedPmId == fromPmId).ToList();
        if (toPmId is { } to)
        {
            var rules = TeamRules.For(db);
            var places = sites.Select(s => (Loc: s.LocationId, Campus: s.CampusId))
                              .Concat(romps.Select(r => (Loc: r.Site!.LocationId, Campus: r.Site.CampusId)))
                              .Distinct().Where(x => !rules.Allows(to, x.Loc, x.Campus)).ToList();
            var blocked = places.Select(x => x.Loc).Distinct().ToList();
            if (places.Count > 0)
            {
                var names = db.Locations.Where(l => blocked.Contains(l.Id)).Select(l => l.Name).OrderBy(n => n).ToList();
                throw new TeamRuleException(
                    $"{rules.Reason(to, places[0].Loc, places[0].Campus)}\n\nThe new PM can't take {names.Count} of this PM's clusters ({string.Join(", ", names)}). " +
                    "Nothing was changed — choose a PM on the same team, or unassign instead.");
            }
        }

        // Campuses this PM held move too, so their sites don't all turn into site overrides.
        foreach (var campus in db.Campuses.Where(c => c.AssignedPmId == fromPmId).ToList())
            campus.AssignedPmId = toPmId;

        foreach (var site in sites)
            AssignmentHistoryService.RecordChange(db, site, toPmId);

        foreach (var romp in romps)
            AssignmentHistoryService.RecordRompChange(db, romp, toPmId);

        db.SaveChanges();
    }

    public int BulkAddPms(IEnumerable<PM> pms)
    {
        var list = pms.ToList();
        using var db = new AppDbContext();
        db.PMs.AddRange(list);
        db.SaveChanges();
        Load();
        return list.Count;
    }

    // Returns all PMs regardless of the current geo filter — used for template export.
    public List<PM> GetAllPms()
    {
        using var db = new AppDbContext();
        return db.PMs.AsNoTracking()
                     .OrderBy(p => p.LastName)
                     .ThenBy(p => p.FirstName)
                     .ToList();
    }

    public void Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { Load(); return; }

        using var db = new AppDbContext();
        var lower = text.ToLower();
        IQueryable<PM> query = db.PMs.AsNoTracking()
            .Where(p => p.FirstName.ToLower().Contains(lower)
                     || p.LastName.ToLower().Contains(lower)
                     || p.DataName.ToLower().Contains(lower)
                     || p.HomeOffice.ToLower().Contains(lower)
                     || p.GeographicalLocation.ToLower().Contains(lower));

        if (_geoFilter != "All")
            query = query.Where(p => p.GeographicalLocation == _geoFilter);

        var list = query.OrderBy(p => p.LastName).ToList();
        Pms = new ObservableCollection<PM>(list);
    }
}
