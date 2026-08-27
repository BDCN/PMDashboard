using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class SitesPage : UserControl
{
    private SitesViewModel? Vm => DataContext as SitesViewModel;

    public SitesPage()
    {
        InitializeComponent();
    }

    // ── Back to PM Detail (when navigated from PM Detail page) ──

    private void BtnBackToPm_Click(object sender, RoutedEventArgs e) =>
        Vm?.NavigateBackAction?.Invoke();

    // ── Breadcrumb navigation ──

    private void BtnBreadcrumbRoot_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.NavigateBack(); // from Sites → Locations, from Romps → Sites, etc.
        // If already at Locations, reload to refresh
        if (vm.NavLevel == SiteNavLevel.Locations)
            vm.LoadLocations();
    }

    private void BtnBreadcrumbLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // Only meaningful when at Romps level — go back to Sites
        if (vm.NavLevel == SiteNavLevel.Romps)
            vm.NavigateBack();
    }

    // ── Location drill-down ──

    private void BtnLocationLink_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Location loc) return;
        vm.NavigateToSites(loc);
    }

    private void BtnAddLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditLocationDialog(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.AddLocation(dlg.Result!);
    }

    private void BtnEditLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Location loc) return;
        var dlg = new AddEditLocationDialog(loc) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.UpdateLocation(dlg.Result!);
    }

    private void BtnDeleteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Location loc) return;
        var result = MessageBox.Show(
            $"Delete location \"{loc.Name}\"? All associated sites and ROMPs will also be deleted.",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) vm.DeleteLocation(loc);
    }

    // ── Site drill-down ──

    private void BtnSiteLink_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Site site) return;
        vm.NavigateToRomps(site);
    }

    private void BtnAddSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var locations = vm.Locations.ToList();
        if (locations.Count == 0)
        {
            MessageBox.Show("Please create at least one location first.", "No Locations",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new AddEditSiteDialog(null, locations) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.AddSite(dlg.Result!);
    }

    private void BtnEditSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Site site) return;
        var dlg = new AddEditSiteDialog(site, vm.Locations.ToList()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            vm.UpdateSite(dlg.Result!, dlg.BulkRompStatus);
    }

    private void BtnDeleteSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Site site) return;
        var result = MessageBox.Show(
            $"Delete site \"{site.Name}\"? All associated ROMPs will also be deleted.",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) vm.DeleteSite(site);
    }

    private void BtnAssignPm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Site site) return;
        try
        {
            var pms = vm.GetPms();
            if (pms.Count == 0)
            {
                MessageBox.Show("No Project Managers found. Add PMs via PM Maintenance first.",
                    "No PMs", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new AssignPmDialog(site, pms, site.AssignedPm) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            if (dlg.WasUnassigned)
                vm.AssignPm(site, null);
            else if (dlg.SelectedPm != null)
                vm.AssignPm(site, dlg.SelectedPm.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Assignment failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── ROMP actions ──

    // Inline status change from the row ComboBox — saves immediately without opening the dialog.
    // Guard: if newStatus == romp.Status the ComboBox is just initialising, skip.
    private void CboRompStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not ComboBox cbo) return;
        if (cbo.Tag is not Romp romp) return;
        var newStatus = cbo.SelectedItem as string;
        if (string.IsNullOrEmpty(newStatus) || newStatus == romp.Status) return;
        vm.UpdateRompStatus(romp, newStatus);
    }

    private void BtnShowAllRomps_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.LoadRomps(); // no filter — all ROMPs
    }

    private void BtnAddRomp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // When at Romps level, Sites is already loaded for this location
        var sites = vm.Sites.Count > 0
            ? vm.Sites.ToList()
            : vm.Locations.SelectMany(l => l.Sites).ToList();
        if (sites.Count == 0)
        {
            MessageBox.Show("Please create at least one site first.", "No Sites",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new AddEditRompDialog(null, sites) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.AddRomp(dlg.Result!, dlg.SelectedEquipmentCategoryIds);
    }

    private void BtnEditRomp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Romp romp) return;
        var sites = vm.Sites.Count > 0
            ? vm.Sites.ToList()
            : vm.Locations.SelectMany(l => l.Sites).ToList();
        var dlg = new AddEditRompDialog(romp, sites) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.UpdateRomp(dlg.Result!, dlg.SelectedEquipmentCategoryIds);
    }

    private void BtnDeleteRomp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Romp romp) return;
        var result = MessageBox.Show($"Delete ROMP \"{romp.Name}\"?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) vm.DeleteRomp(romp);
    }
}
