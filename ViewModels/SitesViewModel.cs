using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;
using System.Collections.ObjectModel;

namespace PMDashboard.ViewModels;

// Cluster (Location) → Campus → Site → ROMP
public enum SiteNavLevel { Locations, Campuses, Sites, Romps }

public partial class SitesViewModel : ObservableObject
{
    // ── Source collections (loaded from DB) ──
    [ObservableProperty] private ObservableCollection<Location> _locations = new();
    [ObservableProperty] private ObservableCollection<Campus>   _campuses  = new();
    [ObservableProperty] private ObservableCollection<Site>     _sites     = new();
    [ObservableProperty] private ObservableCollection<Romp>     _romps     = new();

    // ── Filtered display collections (bound to DataGrids) ──
    [ObservableProperty] private ObservableCollection<Location> _filteredLocations = new();
    [ObservableProperty] private ObservableCollection<Campus>   _filteredCampuses  = new();
    [ObservableProperty] private ObservableCollection<Site>     _filteredSites     = new();
    [ObservableProperty] private ObservableCollection<Romp>     _filteredRomps     = new();

    // ── Location filters ──
    [ObservableProperty] private string _locationSearch       = "";
    [ObservableProperty] private string _locationRegionFilter = "All";

    // Fully-closed locations (every site under them Complete) are hidden by default (see
    // AddEditLocationDialog's "Close All Sites & This Location" checkbox) — this toggle brings
    // them back into view.
    [ObservableProperty] private bool _showCompletedLocations;

    // ── Campus filters ──
    [ObservableProperty] private string _campusSearch = "";
    // Campuses whose every site is Complete are hidden by default, like sites and ROMPs.
    [ObservableProperty] private bool _showCompletedCampuses;

    // ── Site filters ──
    [ObservableProperty] private string _siteSearch       = "";
    [ObservableProperty] private string _siteStatusFilter = "All";
    [ObservableProperty] private string _sitePmFilter     = "All";
    [ObservableProperty] private ObservableCollection<string> _availablePmNames = new() { "All" };

    // Completed sites are hidden by default (see AddEditSiteDialog's "Close All ROMPs & This
    // Site" checkbox) — this toggle brings them back into view without disturbing SiteStatusFilter.
    [ObservableProperty] private bool _showCompletedSites;

    // ── ROMP filters ──
    [ObservableProperty] private string _rompSearch       = "";
    [ObservableProperty] private string _rompStatusFilter = "All";

    // Completed ROMPs are hidden by default (see AddEditRompDialog's "Mark Complete & Remove
    // from List" checkbox) — this toggle brings them back into view without disturbing RompStatusFilter.
    [ObservableProperty] private bool _showCompletedRomps;

    // Dynamic status lists driven by StatusConfig — used by SitesPage dropdowns.
    // AvailableRompStatusNames is an assignment picker (the per-ROMP quick-change dropdown), so
    // it excludes disabled statuses — except any disabled status a currently-loaded ROMP is
    // already on, unioned back in so that row's dropdown still shows its real value instead of
    // going blank. AvailableRompStatusFilters is a view filter, not an assignment, so it always
    // shows every configured status.
    public IReadOnlyList<string> AvailableRompStatusNames =>
        StatusConfig.Selectable
            .Union(Romps.Select(r => r.Status), StringComparer.OrdinalIgnoreCase)
            .ToList();
    public IReadOnlyList<string> AvailableRompStatusFilters => new[] { "All" }.Concat(StatusConfig.All).ToList();

    // Recompute filtered collections when source collections or filter values change
    partial void OnLocationsChanged(ObservableCollection<Location> value) => ApplyLocationFilters();
    partial void OnLocationSearchChanged(string value)                    => ApplyLocationFilters();
    partial void OnLocationRegionFilterChanged(string value)              => ApplyLocationFilters();
    partial void OnShowCompletedLocationsChanged(bool value)              => ApplyLocationFilters();

    partial void OnCampusesChanged(ObservableCollection<Campus> value) => ApplyCampusFilters();
    partial void OnCampusSearchChanged(string value)                   => ApplyCampusFilters();
    partial void OnShowCompletedCampusesChanged(bool value)            => ApplyCampusFilters();

    private void ApplyCampusFilters()
    {
        var q = Campuses.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(CampusSearch))
        {
            var low = CampusSearch.ToLower();
            q = q.Where(c => c.Name.ToLower().Contains(low)
                          || c.PmDisplay.ToLower().Contains(low)
                          || c.Sites.Any(s => s.Name.ToLower().Contains(low)));
        }
        if (!ShowCompletedCampuses)
            q = q.Where(c => c.CampusStatus != "Complete");
        FilteredCampuses = new ObservableCollection<Campus>(q);
    }

    partial void OnSitesChanged(ObservableCollection<Site> value) { RefreshAvailablePmNames(); ApplySiteFilters(); }
    partial void OnSiteSearchChanged(string value)                => ApplySiteFilters();
    partial void OnSiteStatusFilterChanged(string value)          => ApplySiteFilters();
    partial void OnSitePmFilterChanged(string value)              => ApplySiteFilters();
    partial void OnShowCompletedSitesChanged(bool value)          => ApplySiteFilters();

    partial void OnRompsChanged(ObservableCollection<Romp> value)
    {
        ApplyRompFilters();
        OnPropertyChanged(nameof(AvailableRompStatusNames)); // may now include/exclude a disabled status in use
    }
    partial void OnRompSearchChanged(string value)                => ApplyRompFilters();
    partial void OnRompStatusFilterChanged(string value)          => ApplyRompFilters();
    partial void OnShowCompletedRompsChanged(bool value)          => ApplyRompFilters();

    private void ApplyLocationFilters()
    {
        var q = Locations.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(LocationSearch))
        {
            var low = LocationSearch.ToLower();
            q = q.Where(l => l.Name.ToLower().Contains(low)
                           || l.GeographicalLocation.ToLower().Contains(low));
        }
        if (LocationRegionFilter != "All")
            q = q.Where(l => l.GeographicalLocation == LocationRegionFilter);
        if (!ShowCompletedLocations)
            q = q.Where(l => !l.IsFullyClosed);
        FilteredLocations = new ObservableCollection<Location>(q);
    }

    private void RefreshAvailablePmNames()
    {
        var names = Sites.Where(s => s.AssignedPm != null)
                         .Select(s => s.AssignedPm!.FullName)
                         .Distinct().OrderBy(n => n);
        AvailablePmNames = new ObservableCollection<string>(
            new[] { "All" }.Concat(names));
    }

    private void ApplySiteFilters()
    {
        var q = Sites.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SiteSearch))
        {
            var low = SiteSearch.ToLower();
            q = q.Where(s => s.Name.ToLower().Contains(low)
                           || (s.AssignedPm?.FullName.ToLower().Contains(low) == true));
        }
        if (SiteStatusFilter != "All")
            // An explicit status choice (including "Complete") always wins over the
            // hide-completed default below — picking it is how you deliberately look at closed sites.
            q = q.Where(s => s.SiteStatus == SiteStatusFilter);
        else if (!ShowCompletedSites)
            q = q.Where(s => s.SiteStatus != "Complete");
        if (SitePmFilter != "All")
            q = q.Where(s => s.AssignedPm?.FullName == SitePmFilter);
        FilteredSites = new ObservableCollection<Site>(q);
    }

    private void ApplyRompFilters()
    {
        var q = Romps.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(RompSearch))
        {
            var low = RompSearch.ToLower();
            q = q.Where(r => r.Name.ToLower().Contains(low)
                           || r.Status.ToLower().Contains(low)
                           || (r.Site?.Name.ToLower().Contains(low) == true));
        }
        if (RompStatusFilter != "All")
            // An explicit status choice (including a complete one) always wins over the
            // hide-completed default below — picking it is how you deliberately look at closed ROMPs.
            q = q.Where(r => r.Status == RompStatusFilter);
        else if (!ShowCompletedRomps)
            q = q.Where(r => !StatusConfig.Complete.Contains(r.Status));
        FilteredRomps = new ObservableCollection<Romp>(q);
    }

    [ObservableProperty] private Location? _selectedLocation;
    [ObservableProperty] private Campus?   _selectedCampus;
    [ObservableProperty] private Site?     _selectedSite;
    [ObservableProperty] private Romp?     _selectedRomp;

    [ObservableProperty] private SiteNavLevel _navLevel = SiteNavLevel.Locations;

    // When navigated here from PM Detail, show a contextual back button
    [ObservableProperty] private string  _backButtonLabel = "";
    [ObservableProperty] private bool    _hasBackButton;
    public Action? NavigateBackAction { get; set; }

    private string _geoFilter = "All";

    // Bool helpers used by XAML DataTriggers — no enum namespace needed in XAML
    public bool IsAtLocationsLevel => NavLevel == SiteNavLevel.Locations;
    public bool IsAtCampusesLevel  => NavLevel == SiteNavLevel.Campuses;
    public bool IsAtSitesLevel     => NavLevel == SiteNavLevel.Sites;
    public bool IsBelowCampuses    => NavLevel is SiteNavLevel.Sites or SiteNavLevel.Romps;
    public bool IsAtRompsLevel     => NavLevel == SiteNavLevel.Romps;
    public bool IsAboveLocations   => NavLevel != SiteNavLevel.Locations;
    public bool IsAtRomps          => NavLevel == SiteNavLevel.Romps;

    partial void OnNavLevelChanged(SiteNavLevel value)
    {
        OnPropertyChanged(nameof(IsAtLocationsLevel));
        OnPropertyChanged(nameof(IsAtCampusesLevel));
        OnPropertyChanged(nameof(IsAtSitesLevel));
        OnPropertyChanged(nameof(IsBelowCampuses));
        OnPropertyChanged(nameof(IsAtRompsLevel));
        OnPropertyChanged(nameof(IsAboveLocations));
        OnPropertyChanged(nameof(IsAtRomps));
    }

    // ── Navigation ──

    /// <summary>
    /// Jump directly to the ROMs list for a specific site (called from PM Detail).
    /// The caller should set BackButtonLabel + NavigateBackAction before calling this.
    /// </summary>
    public void NavigateDirectToSiteRomps(int siteId)
    {
        using var db = new AppDbContext();
        var site = db.Sites.AsNoTracking()
                           .Include(s => s.Location)
                           .Include(s => s.Campus)
                           .FirstOrDefault(s => s.Id == siteId);
        if (site?.Location == null) return;

        SelectedLocation = site.Location;
        SelectedCampus   = site.Campus;
        // Load the campus's sites so the breadcrumb and Back work
        if (site.Campus != null) LoadSites(site.Campus);
        SelectedSite = Sites.FirstOrDefault(s => s.Id == siteId)
                        ?? new Site { Id = siteId, Name = site.Name, LocationId = site.LocationId };
        LoadRomps(SelectedSite);
        NavLevel = SiteNavLevel.Romps;
    }

    public void NavigateToCampuses(Location location)
    {
        SelectedLocation = location;
        LoadCampuses(location);
        NavLevel = SiteNavLevel.Campuses;
    }

    public void NavigateToSites(Campus campus)
    {
        SelectedCampus = campus;
        LoadSites(campus);
        NavLevel = SiteNavLevel.Sites;
    }

    public void NavigateToRomps(Site site)
    {
        SelectedSite = site;
        LoadRomps(site);
        NavLevel = SiteNavLevel.Romps;
    }

    public void NavigateBack()
    {
        switch (NavLevel)
        {
            case SiteNavLevel.Romps:
                if (SelectedCampus != null) LoadSites(SelectedCampus);
                NavLevel = SiteNavLevel.Sites;
                break;
            case SiteNavLevel.Sites:
                if (SelectedLocation != null) LoadCampuses(SelectedLocation);
                NavLevel = SiteNavLevel.Campuses;
                break;
            case SiteNavLevel.Campuses:
                LoadLocations(); // also sets NavLevel = Locations
                break;
        }
    }

    // ── Data loading ──

    public void ApplyGeoFilter(string filter)
    {
        _geoFilter = filter;
        ClearBackButton();
        LoadLocations();
    }

    private void ClearBackButton()
    {
        BackButtonLabel  = "";
        HasBackButton    = false;
        NavigateBackAction = null;
    }

    public void LoadLocations()
    {
        using var db = new AppDbContext();
        IQueryable<Location> query = db.Locations.AsNoTracking()
                                                  .Include(l => l.AssignedPm)
                                                  .Include(l => l.Sites).ThenInclude(s => s.Romps)
                                                  .Include(l => l.Sites).ThenInclude(s => s.AssignedPm)
                                                  .Include(l => l.Campuses);
        if (_geoFilter != "All")
            query = query.Where(l => l.GeographicalLocation == _geoFilter);

        var list = query.OrderBy(l => l.Name).ToList();
        Locations = new ObservableCollection<Location>(list);
        Campuses  = new ObservableCollection<Campus>();
        Sites     = new ObservableCollection<Site>();
        Romps     = new ObservableCollection<Romp>();
        NavLevel  = SiteNavLevel.Locations;
    }

    public void LoadCampuses(Location location)
    {
        using var db = new AppDbContext();
        var list = db.Campuses.AsNoTracking()
                     .Include(c => c.AssignedPm)
                     .Include(c => c.SubRegion)
                     .Include(c => c.Location).ThenInclude(l => l.SubRegion)
                     .Include(c => c.Sites).ThenInclude(s => s.Romps)
                     .Where(c => c.LocationId == location.Id)
                     .OrderBy(c => c.Name)
                     .ToList();
        Campuses = new ObservableCollection<Campus>(list);
        Sites    = new ObservableCollection<Site>();
        Romps    = new ObservableCollection<Romp>();
    }

    public void LoadSites(Campus campus)
    {
        using var db = new AppDbContext();
        var list = db.Sites.AsNoTracking()
                     .Include(s => s.Romps)
                     .Include(s => s.AssignedPm)
                     .Where(s => s.CampusId == campus.Id)
                     .OrderBy(s => s.Name)
                     .ToList();
        Sites = new ObservableCollection<Site>(list);
        Romps = new ObservableCollection<Romp>();
    }

    public void LoadRomps(Site? filterSite = null)
    {
        using var db = new AppDbContext();
        IQueryable<Romp> query = db.Romps.AsNoTracking()
                                         .Include(r => r.Site).ThenInclude(s => s.Location)
                                         .Include(r => r.Site).ThenInclude(s => s.AssignedPm)
                                         .Include(r => r.AssignedPm)
                                         .Include(r => r.EquipmentTypes)
                                             .ThenInclude(et => et.Category);
        if (filterSite != null)
            query = query.Where(r => r.SiteId == filterSite.Id);
        Romps = new ObservableCollection<Romp>(query.OrderBy(r => r.Name).ToList());
    }

    // ── Bulk import support ──

    /// <summary>Every ROMP with its Site + Location — for the Sites/ROMPs import template.</summary>
    public List<Romp> GetAllRompsForExport()
    {
        using var db = new AppDbContext();
        return db.Romps.AsNoTracking()
                       .Include(r => r.Site).ThenInclude(s => s.Location)
                       .OrderBy(r => r.Site!.Location!.Name).ThenBy(r => r.Site!.Name).ThenBy(r => r.Name)
                       .ToList();
    }

    /// <summary>Reloads from the top after a bulk import — safer than trying to preserve the
    /// current drill-down level, since an import can touch many unrelated locations at once.</summary>
    public void RefreshAfterImport() => LoadLocations();

    /// <summary>
    /// Background-sync refresh (another computer changed something): reloads at whatever
    /// drill-down level is showing, re-pointing the selected Location/Site by Id, instead of
    /// kicking the user back to the top like <see cref="RefreshAfterImport"/>. Leaves the
    /// ← Back-to-PM button alone. Falls back a level if the selected record no longer exists.
    /// </summary>
    public void RefreshFromSync()
    {
        var level    = NavLevel;
        var locId    = SelectedLocation?.Id;
        var campusId = SelectedCampus?.Id;
        var siteId   = SelectedSite?.Id;

        LoadLocations(); // resets NavLevel to Locations

        // ROMP level re-resolves straight from the DB — it may have been reached from PM Detail
        // for a Location outside the current geo filter, which LoadLocations() wouldn't list.
        if (level == SiteNavLevel.Romps && siteId is { } sid)
        {
            NavigateDirectToSiteRomps(sid); // no-op (stays at Locations) if the site is gone
            return;
        }
        if (level is SiteNavLevel.Campuses or SiteNavLevel.Sites && locId is { } lid)
        {
            var loc = Locations.FirstOrDefault(l => l.Id == lid);
            if (loc == null) return;
            NavigateToCampuses(loc);
            if (level == SiteNavLevel.Sites && campusId is { } cid
                && Campuses.FirstOrDefault(c => c.Id == cid) is { } campus)
                NavigateToSites(campus);
        }
    }

    // ── PM assignment ──

    /// <summary>PMs for an assignment picker. With a cluster, only the PMs the team rule allows
    /// there (TeamRules) — so an out-of-area PM can't be picked.</summary>
    public List<PM> GetPms(int? locationId = null, int? campusId = null)
    {
        using var db = new AppDbContext();
        var pms = db.PMs.AsNoTracking()
                        .OrderBy(p => p.GeographicalLocation)
                        .ThenBy(p => p.LastName)
                        .ThenBy(p => p.FirstName)
                        .ToList();
        return locationId is { } loc ? TeamRules.For(db).Eligible(pms, loc, campusId) : pms;
    }

    /// <summary>PMs allowed on every campus of a cluster — for "Assign PM" on a whole cluster,
    /// which may be split across sub-regions.</summary>
    public List<PM> GetPmsForCluster(int locationId)
    {
        using var db = new AppDbContext();
        var rules = TeamRules.For(db);
        var pms = db.PMs.AsNoTracking().OrderBy(p => p.GeographicalLocation).ThenBy(p => p.LastName).ThenBy(p => p.FirstName).ToList();
        return rules.EligibleForAll(pms, locationId, rules.CampusIdsIn(locationId));
    }

    public void AssignPm(Site site, int? pmId)
    {
        using var db = new AppDbContext();
        var tracked = db.Sites.Find(site.Id);
        if (tracked == null) return;
        AssignmentHistoryService.RecordChange(db, tracked, pmId);
        db.SaveChanges();
        RefreshAfterSiteChange();
    }

    /// <summary>Count of Sites under a Location — used to confirm the bulk assign below before it runs.</summary>
    public int GetSiteCountForLocation(Location location)
    {
        using var db = new AppDbContext();
        return db.Sites.Count(s => s.LocationId == location.Id);
    }

    /// <summary>
    /// Assigns one PM to every Campus under a Cluster in one action. Goes through
    /// AssignmentHistoryService per site (so each campus's own assignment history stays correct —
    /// a site already on the chosen PM is a no-op there) but batches everything into a single
    /// SaveChanges for the whole cluster, same reasoning as the Forecast "Add Campuses" fix: one
    /// SaveChanges per site in a loop is what made that freeze the UI on a large batch.
    /// </summary>
    public int AssignPmToLocation(Location location, int? pmId)
    {
        // Every campus in the cluster gets the PM, which flows down to each campus's sites except
        // site-level overrides (see CampusService.AssignCampus). One save for the whole cluster.
        using var db = new AppDbContext();
        var campuses = db.Campuses.Include(c => c.Sites).Where(c => c.LocationId == location.Id).ToList();
        if (pmId is { } checkPm)
        {
            // A split cluster's campuses can be in different sub-regions — every one must allow the PM.
            var rules = TeamRules.For(db);
            if (campuses.Count == 0) rules.Ensure(checkPm, location.Id);
            foreach (var c in campuses) rules.Ensure(checkPm, location.Id, c.Id);
        }
        int changed = 0;
        foreach (var campus in campuses)
        {
            campus.AssignedPmId = pmId;
            foreach (var site in campus.Sites.Where(s => !s.PmOverride))
            {
                if (site.AssignedPmId != pmId) changed++;
                site.Campus = campus;
                AssignmentHistoryService.RecordChange(db, site, pmId);
            }
        }
        db.SaveChanges();
        RefreshAfterSiteChange();
        return changed;
    }

    // ── Campus CRUD + campus-level assignment ──

    public List<Campus> GetCampusesForLocation(int locationId)
    {
        using var db = new AppDbContext();
        return db.Campuses.AsNoTracking().Where(c => c.LocationId == locationId).OrderBy(c => c.Name).ToList();
    }

    public void AddCampus(Location location, string name)
    {
        using var db = new AppDbContext();
        CampusService.GetOrCreate(db, location.Id, name);
        db.SaveChanges();
        LoadCampuses(location);
    }

    public void RenameCampus(Campus campus, string name)
    {
        using var db = new AppDbContext();
        var tracked = db.Campuses.Find(campus.Id);
        if (tracked == null) return;
        tracked.Name = name.Trim();
        db.SaveChanges();
        if (SelectedLocation != null) LoadCampuses(SelectedLocation);
    }

    /// <summary>Deletes an empty campus. Returns false (and does nothing) if it still has sites —
    /// they must be moved to another campus first so nothing is silently re-placed.</summary>
    public bool DeleteCampus(Campus campus)
    {
        using var db = new AppDbContext();
        if (db.Sites.Any(s => s.CampusId == campus.Id)) return false;
        var tracked = db.Campuses.Find(campus.Id);
        if (tracked != null) { db.Campuses.Remove(tracked); db.SaveChanges(); }
        if (SelectedLocation != null) LoadCampuses(SelectedLocation);
        return true;
    }

    public int AssignCampusPm(Campus campus, int? pmId)
    {
        var changed = CampusService.AssignCampus(campus.Id, pmId);
        RefreshAfterSiteChange();
        return changed;
    }

    public void MoveSite(Site site, int campusId)
    {
        CampusService.MoveSite(site.Id, campusId);
        RefreshAfterSiteChange();
    }

    /// <summary>Move… on a site: to an existing campus in its cluster, or a new one by name.</summary>
    public void MoveSiteToCampus(Site site, string campusName)
    {
        int campusId;
        using (var db = new AppDbContext())
        {
            var campus = CampusService.GetOrCreate(db, site.LocationId, campusName);
            db.SaveChanges();
            campusId = campus.Id;
        }
        MoveSite(site, campusId);
    }

    public void ClearSiteOverride(Site site)
    {
        CampusService.ClearOverride(site.Id);
        RefreshAfterSiteChange();
    }

    /// <summary>Individual-ROMP PM override — see AssignmentResolver/AssignmentHistoryService.RecordRompChange.</summary>
    public void AssignRompPm(Romp romp, int? pmId)
    {
        using var db = new AppDbContext();
        var tracked = db.Romps.Find(romp.Id);
        if (tracked == null) return;
        AssignmentHistoryService.RecordRompChange(db, tracked, pmId);
        db.SaveChanges();
        if (NavLevel == SiteNavLevel.Romps && SelectedSite != null) LoadRomps(SelectedSite);
    }

    // ── Location CRUD ──

    public void AddLocation(Location location)
    {
        using var db = new AppDbContext();
        db.Locations.Add(location);
        db.SaveChanges();
        LoadLocations();
    }

    // bulkRompStatus (e.g. the complete status) cascades to every ROMP at every Site under this
    // Location — same "Close All ROMPs & This Site" mechanism as UpdateSite, one level up.
    // closeAllSites additionally sets Site.IsClosed on every site here, same reason UpdateSite
    // sets it directly rather than relying only on bulkRompStatus — a zero-ROMP site has no
    // ROMPs for bulkRompStatus to touch, so without this a Location made up of empty sites could
    // never actually reach IsFullyClosed.
    public void UpdateLocation(Location updated, string? bulkRompStatus = null, bool closeAllSites = false)
    {
        using var db = new AppDbContext();
        var tracked = db.Locations.Find(updated.Id);
        if (tracked == null) return;
        db.Entry(tracked).CurrentValues.SetValues(updated);

        var sites = closeAllSites
            ? db.Sites.Where(s => s.LocationId == updated.Id).ToList()
            : null;

        if (closeAllSites && sites != null)
            foreach (var site in sites)
                site.IsClosed = true;

        if (!string.IsNullOrEmpty(bulkRompStatus))
        {
            var siteIds = sites?.Select(s => s.Id).ToList()
                       ?? db.Sites.Where(s => s.LocationId == updated.Id).Select(s => s.Id).ToList();
            var romps   = db.Romps.Where(r => siteIds.Contains(r.SiteId)).ToList();
            foreach (var romp in romps)
                romp.Status = bulkRompStatus;

            // Sites with no ROMPs have nothing for the loop above to touch — same problem
            // IsFullyClosed already worked around for closeAllSites. Those campuses stand in for
            // themselves instead (Site.StatusWhenEmpty), same as UpdateSite does one level down.
            var siteIdsWithRomps = romps.Select(r => r.SiteId).ToHashSet();
            var emptySiteIds = siteIds.Except(siteIdsWithRomps).ToList();
            if (emptySiteIds.Count > 0)
            {
                var emptySites = sites?.Where(s => emptySiteIds.Contains(s.Id)).ToList()
                               ?? db.Sites.Where(s => emptySiteIds.Contains(s.Id)).ToList();
                foreach (var site in emptySites)
                    site.StatusWhenEmpty = bulkRompStatus;
            }
        }

        db.SaveChanges();
        LoadLocations();
    }

    public void DeleteLocation(Location location)
    {
        using var db = new AppDbContext();
        db.Locations.Remove(location);
        db.SaveChanges();
        LoadLocations();
    }

    // ── Site CRUD ──

    public void AddSite(Site site)
    {
        using var db = new AppDbContext();
        // Added from inside a campus → it belongs there, by hand, and takes the campus's PM.
        // Otherwise CampusArrangementService places it (forecast campus, or one of its own name).
        if (SelectedCampus != null && NavLevel == SiteNavLevel.Sites)
        {
            site.CampusId     = SelectedCampus.Id;
            site.CampusManual = true;
            site.LocationId   = SelectedCampus.LocationId;
            site.AssignedPmId = db.Campuses.Where(c => c.Id == SelectedCampus.Id).Select(c => c.AssignedPmId).FirstOrDefault();
        }
        db.Sites.Add(site);
        db.SaveChanges();
        if (site.CampusId == null) CampusArrangementService.Arrange();
        else if (site.AssignedPmId != null)
        {
            // open an assignment-history row for the inherited PM
            var tracked = db.Sites.Find(site.Id)!;
            var pm = tracked.AssignedPmId; tracked.AssignedPmId = null;
            AssignmentHistoryService.RecordChange(db, tracked, pm);
            db.SaveChanges();
        }
        RefreshAfterSiteChange();
    }

    public void UpdateSite(Site updated, string? bulkRompStatus = null)
    {
        using var db = new AppDbContext();
        var tracked = db.Sites.Find(updated.Id);
        if (tracked == null) return;
        // SetValues copies every scalar — keep the campus placement and PM/override as they are
        // (the edit dialog doesn't manage them; moving and assigning have their own actions).
        var (campusId, campusManual, pmId, pmOverride) = (tracked.CampusId, tracked.CampusManual, tracked.AssignedPmId, tracked.PmOverride);
        db.Entry(tracked).CurrentValues.SetValues(updated);
        (tracked.CampusId, tracked.CampusManual, tracked.AssignedPmId, tracked.PmOverride) = (campusId, campusManual, pmId, pmOverride);

        if (!string.IsNullOrEmpty(bulkRompStatus))
        {
            var romps = db.Romps.Where(r => r.SiteId == updated.Id).ToList();
            if (romps.Count > 0)
            {
                foreach (var romp in romps)
                    romp.Status = bulkRompStatus;
            }
            else
            {
                // No ROMPs to carry a status — the campus acts as its own stand-in instead of the
                // picker silently doing nothing (see Site.StatusWhenEmpty).
                tracked.StatusWhenEmpty = bulkRompStatus;
            }
        }

        db.SaveChanges();
        RefreshAfterSiteChange();
    }

    public void DeleteSite(Site site)
    {
        using var db = new AppDbContext();
        db.Sites.Remove(site);
        db.SaveChanges();
        RefreshAfterSiteChange();
    }

    // Stay at Sites level after site changes; go back to Locations only if needed
    // Stay at the current level after a change: re-load the campus list or the campus's sites.
    private void RefreshAfterSiteChange()
    {
        if (NavLevel == SiteNavLevel.Sites && SelectedCampus != null)
            LoadSites(SelectedCampus);
        else if (NavLevel == SiteNavLevel.Campuses && SelectedLocation != null)
            LoadCampuses(SelectedLocation);
        else
            LoadLocations();
    }

    // ── ROMP CRUD ──

    public void AddRomp(Romp romp, IReadOnlyList<int>? equipmentCategoryIds = null)
    {
        using var db = new AppDbContext();
        db.Romps.Add(romp);
        db.SaveChanges();

        if (equipmentCategoryIds?.Count > 0)
            SaveEquipmentTypes(db, romp.Id, equipmentCategoryIds);

        RefreshAfterRompChange();
    }

    public void UpdateRomp(Romp updated, IReadOnlyList<int>? equipmentCategoryIds = null)
    {
        using var db = new AppDbContext();
        var tracked = db.Romps.Find(updated.Id);
        if (tracked == null) return;
        db.Entry(tracked).CurrentValues.SetValues(updated);
        db.SaveChanges();

        if (equipmentCategoryIds != null)
            SaveEquipmentTypes(db, updated.Id, equipmentCategoryIds);

        RefreshAfterRompChange();
    }

    // Replace all RompEquipmentType rows for a ROMP with the new selection.
    private static void SaveEquipmentTypes(AppDbContext db, int rompId, IReadOnlyList<int> categoryIds)
    {
        var existing = db.RompEquipmentTypes.Where(rt => rt.RompId == rompId).ToList();
        db.RompEquipmentTypes.RemoveRange(existing);

        foreach (var catId in categoryIds.Distinct())
            db.RompEquipmentTypes.Add(new Data.Models.RompEquipmentType
            {
                RompId              = rompId,
                EquipmentCategoryId = catId,
            });

        db.SaveChanges();
    }

    public void DeleteRomp(Romp romp)
    {
        using var db = new AppDbContext();
        db.Romps.Remove(romp);
        db.SaveChanges();
        RefreshAfterRompChange();
    }

    // Quick inline status change — updates DB and reloads without opening the edit dialog.
    public void UpdateRompStatus(Romp romp, string newStatus)
    {
        using var db = new AppDbContext();
        var tracked = db.Romps.Find(romp.Id);
        if (tracked == null) return;
        tracked.Status = newStatus;
        db.SaveChanges();
        RefreshAfterRompChange();
    }

    // Stay at Romps level after ROMP changes; fall back gracefully
    private void RefreshAfterRompChange()
    {
        if (NavLevel == SiteNavLevel.Romps && SelectedSite != null)
            LoadRomps(SelectedSite);
        else if (NavLevel == SiteNavLevel.Sites && SelectedCampus != null)
            LoadSites(SelectedCampus);
        else
            LoadLocations();
    }
}
