using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class PmDetailPage : UserControl
{
    private PmDetailViewModel? Vm => DataContext as PmDetailViewModel;

    public PmDetailPage()
    {
        InitializeComponent();

        // Site Count Only mode: per-site score is meaningless without the weighted scoring
        // system, so hide it rather than show "0.00" for every row.
        if (new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Count")
            ColSiteScore.Visibility = Visibility.Collapsed;
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e) => Vm?.GoBack();

    // ── Bulk selection ────────────────────────────────────────────────────────

    private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        bool select = ChkSelectAll.IsChecked == true;
        foreach (var row in vm.Sites) row.IsSelected = select;
        SitesGrid.Items.Refresh();   // force DataGrid to re-read IsSelected from all rows
    }

    // Keep the Select All checkbox in a consistent tri-state when individual rows change
    private void SiteCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        int total    = vm.Sites.Count;
        int selected = vm.Sites.Count(s => s.IsSelected);
        ChkSelectAll.IsChecked = selected == 0 ? false
                               : selected == total ? true
                               : (bool?)null;   // indeterminate
    }

    private void BtnUnassignSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var selected = vm.Sites.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("No sites are checked. Tick the checkboxes next to the sites you want to unassign.",
                "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(
            $"Unassign this PM from {selected.Count} selected site{(selected.Count == 1 ? "" : "s")}?",
            "Confirm Bulk Unassign", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        vm.BulkUnassignSites(selected.Select(s => s.SiteId).ToList());
        ChkSelectAll.IsChecked = false;
    }

    // Navigate to the site's ROMs list
    private void BtnSiteLink_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PmSiteRow row) return;
        vm.NavigateToSiteRomps?.Invoke(row.SiteId);
    }

    // Edit site — open AddEditSiteDialog then save changes
    private void BtnEditSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PmSiteRow row) return;

        using var db = new AppDbContext();
        var site = db.Sites.AsNoTracking()
                           .Include(s => s.Location)
                           .FirstOrDefault(s => s.Id == row.SiteId);
        if (site == null) return;

        var locations = db.Locations.AsNoTracking().OrderBy(l => l.Name).ToList();
        var dlg = new AddEditSiteDialog(site, locations) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        using var db2 = new AppDbContext();
        var tracked = db2.Sites.Find(row.SiteId);
        if (tracked == null) return;
        db2.Entry(tracked).CurrentValues.SetValues(dlg.Result!);
        if (!string.IsNullOrEmpty(dlg.BulkRompStatus))
        {
            var romps = db2.Romps.Where(r => r.SiteId == row.SiteId).ToList();
            foreach (var r in romps) r.Status = dlg.BulkRompStatus;
        }
        db2.SaveChanges();

        // Reload the PM detail page to reflect changes
        vm.Load(vm.CurrentPmId);
    }

    // Unassign PM from this site
    private void BtnUnassignSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PmSiteRow row) return;

        var confirm = MessageBox.Show(
            $"Unassign this PM from site \"{row.SiteName}\"?",
            "Confirm Unassign", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes)
            vm.UnassignSite(row.SiteId);
    }
}
