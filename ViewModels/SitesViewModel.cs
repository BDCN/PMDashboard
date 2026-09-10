using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;
using System.Collections.ObjectModel;

namespace PMDashboard.ViewModels;

public enum SiteNavLevel { Locations, Sites, Romps }

public partial class SitesViewModel : ObservableObject
{
    // ── Source collections (loaded from DB) ──
    [ObservableProperty] private ObservableCollection<Location> _locations = new();
    [ObservableProperty] private ObservableCollection<Site>     _sites     = new();
    [ObservableProperty] private ObservableCollection<Romp>     _romps     = new();

    // ── Filtered display collections (bound to DataGrids) ──
    [ObservableProperty] private ObservableCollection<Location> _filteredLocations = new();
    [ObservableProperty] private ObservableCollection<Site>     _filteredSites     = new();
    [ObservableProperty] private ObservableCollection<Romp>     _filteredRomps     = new();

    // ── Location filters ──
    [ObservableProperty] private string _locationSearch       = "";
    [ObservableProperty] private string _locationRegionFilter = "All";

    // Fully-closed locations (every site under them Complete) are hidden by default (see
    // AddEditLocationDialog's "Close All Sites & This Location" checkbox) — this toggle brings
    // them back into view.
    [ObservableProperty] private bool _showCompletedLocations;

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

    // Dynamic status lists driven by StatusConfig — used by SitesPage dropdowns
    public IReadOnlyList<string> AvailableRompStatusNames   => StatusConfig.All.ToList();
    public IReadOnlyList<string> AvailableRompStatusFilters => new[] { "All" }.Concat(StatusConfig.All).ToList();

    // Recompute filtered collections when source collections or filter values change
    partial void OnLocationsChanged(ObservableCollection<Location> value) => ApplyLocationFilters();
    partial void OnLocationSearchChanged(string value)                    => ApplyLocationFilters();
    partial void OnLocationRegionFilterChanged(string value)              => ApplyLocationFilters();
    partial void OnShowCompletedLocationsChanged(bool value)              => ApplyLocationFilters();

    partial void OnSitesChanged(ObservableCollection<Site> value) { RefreshAvailablePmNames(); ApplySiteFilters(); }
    partial void OnSiteSearchChanged(string value)                => ApplySiteFilters();
    partial void OnSiteStatusFilterChanged(string value)          => ApplySiteFilters();
    partial void OnSitePmFilterChanged(string value)              => ApplySiteFilters();
    partial void OnShowCompletedSitesChanged(bool value)          => ApplySiteFilters();

    partial void OnRompsChanged(ObservableCollection<Romp> value) => ApplyRompFilters();
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
    public bool IsAtSitesLevel     => NavLevel == SiteNavLevel.Sites;
    public bool IsAtRompsLevel     => NavLevel == SiteNavLevel.Romps;
    public bool IsAboveLocations   => NavLevel != SiteNavLevel.Locations;
    public bool IsAtRomps          => NavLevel == SiteNavLevel.Romps;

    partial void OnNavLevelChanged(SiteNavLevel value)
    {
        OnPropertyChanged(nameof(IsAtLocationsLevel));
        OnPropertyChanged(nameof(IsAtSitesLevel));
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
                           .FirstOrDefault(s => s.Id == siteId);
        if (site?.Location == null) return;

        SelectedLocation = site.Location;
        // Load the full sites list for this location so breadcrumb works
        LoadSites(site.Location);
        SelectedSite = Sites.FirstOrDefault(s => s.Id == siteId)
                        ?? new Site { Id = siteId, Name = site.Name, LocationId = site.LocationId };
        LoadRomps(SelectedSite);
        NavLevel = SiteNavLevel.Romps;
    }

    public void NavigateToSites(Location location)
    {
        SelectedLocation = location;
        LoadSites(location);
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
                if (SelectedLocation != null) LoadSites(SelectedLocation);
                NavLevel = SiteNavLevel.Sites;
                break;
            case SiteNavLevel.Sites:
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
                                                  .Include(l => l.Sites)
                                                  .ThenInclude(s => s.Romps);
        if (_geoFilter != "All")
            query = query.Where(l => l.GeographicalLocation == _geoFilter);

        var list = query.OrderBy(l => l.Name).ToList();
        Locations = new ObservableCollection<Location>(list);
        Sites     = new ObservableCollection<Site>();
        Romps     = new ObservableCollection<Romp>();
        NavLevel  = SiteNavLevel.Locations;
    }

    public void LoadSites(Location? filterLocation = null)
    {
        using var db = new AppDbContext();
        IQueryable<Site> query = db.Sites.AsNoTracking()
                                         .Include(s => s.Romps)
                                         .Include(s => s.AssignedPm);
        if (filterLocation != null)
            query = query.Where(s => s.LocationId == filterLocation.Id);
        Sites = new ObservableCollection<Site>(query.OrderBy(s => s.Name).ToList());
        Romps = new ObservableCollection<Romp>();
    }

    public void LoadRomps(Site? filterSite = null)
    {
        using var db = new AppDbContext();
        IQueryable<Romp> query = db.Romps.AsNoTracking()
                                         .Include(r => r.Site).ThenInclude(s => s.Location)
                                         .Include(r => r.Site).ThenInclude(s => s.AssignedPm)
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

    // ── PM assignment ──

    public List<PM> GetPms()
    {
        using var db = new AppDbContext();
        return db.PMs.AsNoTracking()
                     .OrderBy(p => p.GeographicalLocation)
                     .ThenBy(p => p.LastName)
                     .ThenBy(p => p.FirstName)
                     .ToList();
    }

    public void AssignPm(Site site, int? pmId)
    {
        using var db = new AppDbContext();
        var tracked = db.Sites.Find(site.Id);
        if (tracked == null) return;
        tracked.AssignedPmId = pmId;
        db.SaveChanges();
        RefreshAfterSiteChange();
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
        db.Sites.Add(site);
        db.SaveChanges();
        RefreshAfterSiteChange();
    }

    public void UpdateSite(Site updated, string? bulkRompStatus = null)
    {
        using var db = new AppDbContext();
        var tracked = db.Sites.Find(updated.Id);
        if (tracked == null) return;
        db.Entry(tracked).CurrentValues.SetValues(updated);

        if (!string.IsNullOrEmpty(bulkRompStatus))
        {
            var romps = db.Romps.Where(r => r.SiteId == updated.Id).ToList();
            foreach (var romp in romps)
                romp.Status = bulkRompStatus;
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
    private void RefreshAfterSiteChange()
    {
        if (NavLevel == SiteNavLevel.Sites && SelectedLocation != null)
            LoadSites(SelectedLocation);
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
        else if (NavLevel == SiteNavLevel.Sites && SelectedLocation != null)
            LoadSites(SelectedLocation);
        else
            LoadLocations();
    }
}
