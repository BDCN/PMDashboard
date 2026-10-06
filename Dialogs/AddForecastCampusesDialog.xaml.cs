using System.Collections.ObjectModel;
using System.Windows;
using PMDashboard.ViewModels;

namespace PMDashboard.Dialogs;

public class CampusGridRow
{
    public bool   IsSelected  { get; set; } = true;
    public string SiteCode    { get; init; } = "";
    public string ClusterCode { get; init; } = "";
    public string StatusText  { get; init; } = "";
    public UnmatchedCampus Source { get; init; } = null!;
}

public class NewClusterRow
{
    private static readonly string[] AllRegions = ["East", "Central", "West"];

    public string   ClusterCode      { get; init; } = "";
    public int      CampusCount      { get; init; }
    public string   CampusCountLabel => $"{CampusCount} site{(CampusCount == 1 ? "" : "s")}";
    public string?  SelectedRegion   { get; set; }
    public string[] RegionOptions    => AllRegions; // instance property so {Binding} can see it
}

public partial class AddForecastCampusesDialog : Window
{
    private readonly ForecastTimelineViewModel _vm;
    private readonly List<CampusGridRow> _rows;
    private readonly List<NewClusterRow> _newClusters;

    public int AddedCount { get; private set; }

    public AddForecastCampusesDialog(ForecastTimelineViewModel vm, List<UnmatchedCampus> campuses)
    {
        InitializeComponent();
        _vm = vm;

        _newClusters = campuses
            .Where(c => c.ExistingLocationId == null)
            .GroupBy(c => c.ClusterCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NewClusterRow { ClusterCode = g.Key, CampusCount = g.Count() })
            .OrderBy(c => c.ClusterCode)
            .ToList();

        _rows = campuses.Select(c => new CampusGridRow
        {
            SiteCode    = c.SiteCode,
            ClusterCode = c.ClusterCode,
            StatusText  = c.ExistingLocationId != null ? "Existing cluster" : "New cluster — set region above",
            Source      = c,
        }).ToList();

        NewClustersPanel.Visibility = _newClusters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewClustersList.ItemsSource = _newClusters;
        CampusGrid.ItemsSource      = new ObservableCollection<CampusGridRow>(_rows);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        CampusGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        var regionsByCluster = _newClusters
            .Where(c => !string.IsNullOrWhiteSpace(c.SelectedRegion))
            .ToDictionary(c => c.ClusterCode, c => c.SelectedRegion!, StringComparer.OrdinalIgnoreCase);

        var missingRegion = _newClusters
            .Where(c => string.IsNullOrWhiteSpace(c.SelectedRegion))
            .Select(c => c.ClusterCode)
            .ToList();

        var selected = _rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            TxtError.Text = "Select at least one site to add.";
            return;
        }

        // Only block on a missing region if a selected row actually needs that cluster — a new
        // cluster the user left blank is fine as long as nothing checked depends on it.
        var blockedOn = selected
            .Where(r => r.Source.ExistingLocationId == null && !regionsByCluster.ContainsKey(r.ClusterCode))
            .Select(r => r.ClusterCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (blockedOn.Count > 0)
        {
            TxtError.Text = $"Choose a region for: {string.Join(", ", blockedOn)}";
            return;
        }

        // Even batched, writing/re-linking a large selection takes a visible moment — run it off
        // the UI thread so the window stays responsive instead of Windows marking it "Not
        // Responding" (what used to happen here when AddCampuses ran per-campus on this thread).
        var toAdd = selected.Select(r => r.Source).ToList();
        BtnAdd.IsEnabled     = false;
        BtnCancel.IsEnabled  = false;
        BtnAdd.Content       = "Adding…";
        TxtError.Text        = "";
        try
        {
            AddedCount = await Task.Run(() => _vm.AddCampuses(toAdd, regionsByCluster));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            TxtError.Text = $"Couldn't add sites: {ex.Message}";
            BtnAdd.IsEnabled    = true;
            BtnCancel.IsEnabled = true;
            BtnAdd.Content      = "Add Selected";
        }
    }
}
