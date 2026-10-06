using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
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

    // Clusters › cluster › campus › site — each crumb jumps straight to its level.
    private void BtnBreadcrumbRoot_Click(object sender, RoutedEventArgs e) => Vm?.LoadLocations();

    private void BtnBreadcrumbLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedLocation == null) return;
        vm.NavigateToCampuses(vm.SelectedLocation);
    }

    private void BtnBreadcrumbCampus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedCampus == null) return;
        vm.NavigateToSites(vm.SelectedCampus);
    }

    // ── Location drill-down ──

    private void BtnLocationLink_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Location loc) return;
        vm.NavigateToCampuses(loc);
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
        if (dlg.ShowDialog() == true) vm.UpdateLocation(dlg.Result!, dlg.BulkRompStatus, dlg.CloseAllSites);
    }

    private void BtnAssignLocationPm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Location loc) return;

        var campusCount = vm.GetCampusesForLocation(loc.Id).Count;
        if (campusCount == 0)
        {
            MessageBox.Show($"\"{loc.Name}\" has no campuses yet.", "Assign PM",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new AssignPmDialog($"Cluster: {loc.Name}  ({campusCount} campus{(campusCount == 1 ? "" : "es")})",
            vm.GetPmsForCluster(loc.Id), currentPm: null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var confirm = MessageBox.Show(
            dlg.WasUnassigned
                ? $"Unassign the PM from all {campusCount} campus(es) in \"{loc.Name}\"?"
                : $"Assign {dlg.SelectedPm!.FullName} to all {campusCount} campus(es) in \"{loc.Name}\"?\n\n" +
                  "This replaces each campus's PM. Sites that have their own PM keep it.",
            "Confirm Cluster Assignment", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var pmId = dlg.WasUnassigned ? (int?)null : dlg.SelectedPm!.Id;
        vm.AssignPmToLocation(loc, pmId);
    }

    private void BtnDeleteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Location loc) return;
        var result = MessageBox.Show(
            $"Delete cluster \"{loc.Name}\"? All associated campuses and ROMPs will also be deleted.",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) vm.DeleteLocation(loc);
    }

    // ── Campus level ──

    private void BtnCampusLink_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Campus campus) return;
        vm.NavigateToSites(campus);
    }

    private void BtnAddCampus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedLocation is not { } loc) return;
        var dlg = new TextInputDialog("Add Campus", $"Name of the new campus in cluster {loc.Name} (e.g. {loc.Name}089).")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.AddCampus(loc, dlg.Value);
    }

    private void BtnRenameCampus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Campus campus) return;
        var dlg = new TextInputDialog("Rename Campus", "New name for this campus.", campus.Name)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.RenameCampus(campus, dlg.Value);
    }

    private void BtnDeleteCampus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Campus campus) return;
        if (campus.SiteCount > 0)
        {
            MessageBox.Show($"Campus \"{campus.Name}\" still has {campus.SiteCount} site(s). Move them to another campus first (open the campus, then Move… on each site).",
                "Campus Not Empty", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Delete the empty campus \"{campus.Name}\"?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            vm.DeleteCampus(campus);
    }

    private void BtnAssignCampusPm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Campus campus) return;
        var pms = vm.GetPms(campus.LocationId, campus.Id);
        if (pms.Count == 0)
        {
            MessageBox.Show("No PMs can be assigned here. Add PMs via PM Maintenance, and check this cluster's sub-region and the PMs' teams on the Teams page.",
                "No PMs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new AssignPmDialog($"Campus: {campus.Name}  ({campus.SiteCount} site{(campus.SiteCount == 1 ? "" : "s")})",
            pms, campus.AssignedPm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var pmId = dlg.WasUnassigned ? (int?)null : dlg.SelectedPm?.Id;
        if (!dlg.WasUnassigned && pmId == null) return;
        if (campus.OverrideCount > 0)
        {
            var go = MessageBox.Show(
                $"{campus.OverrideCount} site(s) in \"{campus.Name}\" have their own PM and will keep it. " +
                "Use Assign on those sites to put them back on the campus PM.\n\nContinue?",
                "Sites With Their Own PM", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (go != MessageBoxResult.Yes) return;
        }
        vm.AssignCampusPm(campus, pmId);
    }

    private void BtnMoveSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Site site) return;
        var campuses = vm.GetCampusesForLocation(site.LocationId);
        var dlg = new MoveSiteDialog(site, campuses, vm.SelectedCampus?.Name) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (dlg.CampusName == vm.SelectedCampus?.Name) return;
        vm.MoveSiteToCampus(site, dlg.CampusName);
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
            MessageBox.Show("Please create at least one cluster first.", "No Clusters",
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
            var pms = vm.GetPms(site.LocationId, site.CampusId);
            if (pms.Count == 0)
            {
                MessageBox.Show("No PMs can be assigned here. Add PMs via PM Maintenance, and check this cluster's sub-region and the PMs' teams on the Teams page.",
                    "No PMs", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new AssignPmDialog(site, pms, site.AssignedPm) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var campusPm = vm.SelectedCampus?.AssignedPm;
            int? pmId = dlg.WasUnassigned ? null : dlg.SelectedPm?.Id;
            if (!dlg.WasUnassigned && pmId == null) return;
            if (campusPm != null && pmId != campusPm.Id)
            {
                var go = MessageBox.Show(
                    $"Campus {vm.SelectedCampus!.Name} is assigned to {campusPm.FullName}.\n\n" +
                    $"Give site {site.Name} its own PM ({(pmId == null ? "none" : dlg.SelectedPm!.FullName)})? " +
                    "Assigning the campus later won't change it.",
                    "Site Override", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (go != MessageBoxResult.Yes) return;
            }
            vm.AssignPm(site, pmId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Assignment failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── ROMP actions ──

    private void BtnAssignRompPm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not Romp romp) return;
        try
        {
            var pms = vm.GetPms(vm.SelectedSite?.LocationId ?? vm.SelectedLocation?.Id, vm.SelectedSite?.CampusId);
            if (pms.Count == 0)
            {
                MessageBox.Show("No PMs can be assigned here. Add PMs via PM Maintenance, and check this cluster's sub-region and the PMs' teams on the Teams page.",
                    "No PMs", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var label = $"ROMP: {romp.Name}" + (romp.Site != null ? $"  (Campus: {romp.Site.Name})" : "");
            var dlg = new AssignPmDialog(label, pms, romp.AssignedPm) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            if (dlg.WasUnassigned)
                vm.AssignRompPm(romp, null);
            else if (dlg.SelectedPm != null)
                vm.AssignRompPm(romp, dlg.SelectedPm.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Assignment failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

    private void BtnSiteAssignmentHistory_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedSite is not { } site) return;
        var dlg = AssignmentHistoryDialog.ForSite(site.Id, site.Name);
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
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
            MessageBox.Show("Please create at least one campus first.", "No Campuses",
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

    // ── Sites/ROMPs bulk import ──────────────────────────────────────────────

    private void BtnDownloadSitesTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new SaveFileDialog
        {
            Title      = "Save Campuses/ROMPs Template",
            FileName   = "Campuses_Bulk_Import_Template",
            DefaultExt = ".xlsx",
            Filter     = "Excel Files|*.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var romps = vm.GetAllRompsForExport();
            new SitesImportService().GenerateTemplate(dlg.FileName, romps);

            var msg = romps.Count > 0
                ? $"Template saved with {romps.Count} current ROMP(s) and their status.\n\nEdit the Status column (or add new rows), then use Import Campuses to apply changes."
                : "Template saved (no campuses/ROMPs in database yet).\n\nFill in the \"Campuses Bulk Import\" sheet and use Import Campuses to load it.";
            MessageBox.Show(msg, "Template Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImportSites_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new OpenFileDialog
        {
            Title  = "Select Campuses/ROMPs File",
            Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var confirm = MessageBox.Show(
                "Import campuses/ROMPs from this file?\n\n" +
                "New Clusters/Campuses/ROMPs will be created; existing ROMPs will have their Status updated to match the sheet.",
                "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var (result, errors) = new SitesImportService().ImportSites(dlg.FileName);
            vm.RefreshAfterImport();

            var summary = $"Clusters created: {result.LocationsCreated}\nCampuses created: {result.SitesCreated}\n" +
                          $"ROMPs created: {result.RompsCreated}\nROMPs updated: {result.RompsUpdated}\n" +
                          $"Unchanged: {result.RompsUnchanged}\nErrors: {result.Errors}";
            if (errors.Count > 0)
                summary += "\n\nDetails:\n" + string.Join("\n", errors.Take(30))
                         + (errors.Count > 30 ? $"\n...and {errors.Count - 30} more." : "");

            MessageBox.Show(summary, "Import Complete", MessageBoxButton.OK,
                result.Errors > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
