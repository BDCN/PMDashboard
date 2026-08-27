using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class AssignmentPage : UserControl
{
    private AssignmentViewModel? Vm => DataContext as AssignmentViewModel;

    public AssignmentPage() => InitializeComponent();

    // ── Multi-select ListBox ──────────────────────────────────────────────────

    private bool _suppressListBoxEvent = false;

    private void SitesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListBoxEvent || Vm is not { } vm) return;
        vm.SetSelectedSites(SitesListBox.SelectedItems.Cast<Site>());
    }

    private void BtnSelectAllSites_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _suppressListBoxEvent = true;
        SitesListBox.SelectAll();
        _suppressListBoxEvent = false;
        vm.SetSelectedSites(SitesListBox.SelectedItems.Cast<Site>());
    }

    private void BtnSelectUnassigned_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _suppressListBoxEvent = true;
        SitesListBox.SelectedItems.Clear();
        foreach (var item in SitesListBox.Items)
        {
            if (item is Site s && s.AssignedPmId == null)
                SitesListBox.SelectedItems.Add(item);
        }
        _suppressListBoxEvent = false;
        vm.SetSelectedSites(SitesListBox.SelectedItems.Cast<Site>());
    }

    private void BtnClearSites_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _suppressListBoxEvent = true;
        SitesListBox.SelectedItems.Clear();
        _suppressListBoxEvent = false;
        vm.SetSelectedSites(Enumerable.Empty<Site>());
    }

    // ── Inline creation ───────────────────────────────────────────────────────

    private void BtnNewLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditLocationDialog(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        using var db = new PMDashboard.Data.AppDbContext();
        db.Locations.Add(dlg.Result!);
        db.SaveChanges();
        vm.Load();
        vm.SelectedLocation = vm.Locations.FirstOrDefault(l => l.Id == dlg.Result!.Id);
    }

    private void BtnNewSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var locations = vm.Locations.ToList();
        var dlg = new AddEditSiteDialog(null, locations) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        using var db = new PMDashboard.Data.AppDbContext();
        db.Sites.Add(dlg.Result!);
        db.SaveChanges();
        var locId = vm.SelectedLocation?.Id;
        vm.Load();
        vm.SelectedLocation = vm.Locations.FirstOrDefault(l => l.Id == locId);
        // Auto-select the new site in the ListBox
        var newSite = vm.Sites.FirstOrDefault(s => s.Id == dlg.Result!.Id);
        if (newSite != null)
        {
            SitesListBox.SelectedItems.Clear();
            SitesListBox.SelectedItem = newSite;
        }
    }

    private void BtnNewRomp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var sites = vm.Sites.ToList();
        if (sites.Count == 0)
        {
            MessageBox.Show("Please select a site first.", "No Sites", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new AddEditRompDialog(null, sites) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        using var db = new PMDashboard.Data.AppDbContext();
        db.Romps.Add(dlg.Result!);
        db.SaveChanges();
        foreach (var catId in dlg.SelectedEquipmentCategoryIds)
            db.RompEquipmentTypes.Add(new PMDashboard.Data.Models.RompEquipmentType
                { RompId = dlg.Result!.Id, EquipmentCategoryId = catId });
        if (dlg.SelectedEquipmentCategoryIds.Count > 0) db.SaveChanges();
        vm.SelectedRomp = vm.Romps.FirstOrDefault(r => r.Id == dlg.Result!.Id);
    }

    // ── Assignment actions ────────────────────────────────────────────────────

    private void BtnAssignCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PmCandidate candidate) return;
        if (!vm.HasSelection)
        {
            MessageBox.Show("Please select at least one site from the list first.",
                "No Site Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (vm.IsBulkSelected)
        {
            var confirm = MessageBox.Show(
                $"Assign {candidate.Pm.FullName} to {vm.SiteSelectionInfo}?",
                "Confirm Bulk Assignment", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }
        var prevIds = vm.GetSelectedSiteIds().ToHashSet();
        vm.AssignCandidate(candidate);
        RestoreListBoxSelection(prevIds);
    }

    private void BtnAssignOverride_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!vm.HasSelection)
        {
            MessageBox.Show("Please select at least one site from the list first.",
                "No Site Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (vm.OverridePm == null)
        {
            MessageBox.Show("Please choose a PM from the override dropdown.",
                "No PM Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (vm.IsBulkSelected)
        {
            var confirm = MessageBox.Show(
                $"Assign {vm.OverridePm.FullName} to {vm.SiteSelectionInfo}?",
                "Confirm Bulk Assignment", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }
        var prevIds = vm.GetSelectedSiteIds().ToHashSet();
        vm.AssignOverride();
        RestoreListBoxSelection(prevIds);
    }

    private void BtnUnassign_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!vm.HasSelection) return;
        int n = vm.GetSelectedSiteIds().Count;
        var confirm = MessageBox.Show(
            $"Remove PM assignment from {(n == 1 ? "this site" : $"{n} sites")}?",
            "Confirm Unassign", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        var prevIds = vm.GetSelectedSiteIds().ToHashSet();
        vm.Unassign();
        RestoreListBoxSelection(prevIds);
    }

    // Re-select the same site IDs in the ListBox after the Sites collection is reloaded
    private void RestoreListBoxSelection(IReadOnlySet<int> ids)
    {
        if (ids.Count == 0) return;
        _suppressListBoxEvent = true;
        SitesListBox.SelectedItems.Clear();
        foreach (var item in SitesListBox.Items)
        {
            if (item is Site s && ids.Contains(s.Id))
                SitesListBox.SelectedItems.Add(item);
        }
        _suppressListBoxEvent = false;
        // Re-sync ViewModel selection with what's now in the ListBox
        if (Vm is { } vm)
            vm.SetSelectedSites(SitesListBox.SelectedItems.Cast<Site>());
    }
}
