using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using System.Collections.ObjectModel;

namespace PMDashboard.ViewModels;

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
        Load();
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
