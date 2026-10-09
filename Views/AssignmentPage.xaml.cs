using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class AssignmentPage : UserControl
{
    private AssignmentViewModel? Vm => DataContext as AssignmentViewModel;

    public AssignmentPage()
    {
        InitializeComponent();

        // Site Count Only mode: ranking never computes a utilization percentage, so the column
        // would just show "—" for every candidate.
        if (new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Count")
            ColUtil.Visibility = Visibility.Collapsed;

        // A background sync reload replaces the Campuses collection, which wipes the ListBox's
        // multi-selection — put it back afterwards. The page is recreated by its DataTemplate on
        // each navigation while the VM lives for the app's lifetime, so (un)subscribe per page.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AssignmentViewModel oldVm) Unhook(oldVm);
            if (e.NewValue is AssignmentViewModel newVm) Hook(newVm);
        };
        Unloaded += (_, _) => { if (Vm is { } vm) Unhook(vm); };
        Loaded   += (_, _) => { if (Vm is { } vm) { Unhook(vm); Hook(vm); } };
    }

    private void Hook(AssignmentViewModel vm)
    {
        vm.ReloadedFromSync      += OnReloadedFromSync;
        vm.SelectionNeedsRestore += RestoreListBoxSelection;
    }

    private void Unhook(AssignmentViewModel vm)
    {
        vm.ReloadedFromSync      -= OnReloadedFromSync;
        vm.SelectionNeedsRestore -= RestoreListBoxSelection;
    }

    private void OnReloadedFromSync(IReadOnlySet<int> prevIds) =>
        Dispatcher.InvokeAsync(() => RestoreListBoxSelection(prevIds),
                               System.Windows.Threading.DispatcherPriority.Loaded);

    // ── Multi-select ListBox ──────────────────────────────────────────────────

    private bool _suppressListBoxEvent = false;

    private void CampusesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListBoxEvent || Vm is not { } vm) return;
        vm.SetSelectedCampuses(CampusesListBox.SelectedItems.Cast<Campus>());
    }

    private void BtnSelectAllCampuses_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _suppressListBoxEvent = true;
        CampusesListBox.SelectAll();
        _suppressListBoxEvent = false;
        vm.SetSelectedCampuses(CampusesListBox.SelectedItems.Cast<Campus>());
    }

    private void BtnSelectUnassigned_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _suppressListBoxEvent = true;
        CampusesListBox.SelectedItems.Clear();
        foreach (var item in CampusesListBox.Items)
        {
            if (item is Campus c && c.AssignedPmId == null)
                CampusesListBox.SelectedItems.Add(item);
        }
        _suppressListBoxEvent = false;
        vm.SetSelectedCampuses(CampusesListBox.SelectedItems.Cast<Campus>());
    }

    private void BtnClearCampuses_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _suppressListBoxEvent = true;
        CampusesListBox.SelectedItems.Clear();
        _suppressListBoxEvent = false;
        vm.SetSelectedCampuses(Enumerable.Empty<Campus>());
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

    private void BtnNewCampus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedLocation is not { } loc) return;
        var dlg = new TextInputDialog("Add Campus", $"Name of the new campus in cluster {loc.Name} (e.g. {loc.Name}089).")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (vm.AddCampus(dlg.Value) is { } id)
            RestoreListBoxSelection(new HashSet<int> { id });
    }

    private void BtnNewSite_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedCampus is not { } campus || vm.SelectedLocation is not { } loc) return;
        var dlg = new AddEditSiteDialog(null, new List<Location> { loc }) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        vm.AddSiteToCampus(dlg.Result!);
    }

    private void BtnNewRomp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var sites = vm.CampusSites.ToList();
        if (sites.Count == 0)
        {
            MessageBox.Show("This campus has no sites yet — add a site first.", "No Sites",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
        vm.SelectRomp(dlg.Result!.SiteId, dlg.Result!.Id);
    }

    // ── Assignment actions ────────────────────────────────────────────────────

    private void BtnAssignCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PmCandidate candidate) return;
        if (!vm.HasSelection)
        {
            MessageBox.Show("Please select at least one campus from the list first.",
                "No Campus Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (vm.IsBulkSelected)
        {
            var confirm = MessageBox.Show(
                $"Assign {candidate.Pm.FullName} to {vm.CampusSelectionInfo} (and their sites)?",
                "Confirm Bulk Assignment", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }
        AcknowledgeDialog.RunAssignment(Window.GetWindow(this), () => vm.AssignCandidate(candidate)); // re-selection via SelectionNeedsRestore
    }

    private void BtnAssignOverride_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!vm.HasSelection)
        {
            MessageBox.Show("Please select at least one campus from the list first.",
                "No Campus Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                $"Assign {vm.OverridePm.FullName} to {vm.CampusSelectionInfo} (and their sites)?",
                "Confirm Bulk Assignment", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }
        AcknowledgeDialog.RunAssignment(Window.GetWindow(this), () => vm.AssignOverride());
    }

    private void BtnAssignCluster_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.OverridePm == null)
        {
            MessageBox.Show("Please choose a PM from the override dropdown above first.",
                "No PM Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var clusterPm = vm.OverridePm.Id;
        AcknowledgeDialog.RunAssignment(Window.GetWindow(this), () => vm.AssignPmToCluster(clusterPm));
    }

    private void BtnUnassignCluster_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.AssignPmToCluster(null);
    }

    private void BtnUnassign_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!vm.HasSelection) return;
        var ids = vm.GetSelectedCampusIds();
        int n = ids.Count;
        var confirm = MessageBox.Show(
            $"Remove the PM from {(n == 1 ? "this campus" : $"{n} campuses")} and their sites?\n\nSites with their own PM keep it.",
            "Confirm Unassign", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        vm.Unassign();
    }

    // ── Site-level override ──────────────────────────────────────────────────

    private void BtnAssignSiteOverride_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedSite == null) return;
        if (vm.SiteOverridePm == null)
        {
            MessageBox.Show("Please choose a PM from the dropdown.",
                "No PM Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var sitePm = vm.SiteOverridePm.Id;
        AcknowledgeDialog.RunAssignment(Window.GetWindow(this), () => vm.AssignSiteOverride(sitePm));
    }

    private void BtnClearSiteOverride_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedSite is not { } site) return;
        var confirm = MessageBox.Show(
            $"Put site \"{site.Name}\" back on its campus's PM?",
            "Use Campus PM", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        vm.ClearSiteOverride();
    }

    // ── ROMP-level assignment ────────────────────────────────────────────────

    private void BtnAssignRompOverride_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.SelectedRomp == null) return;
        if (vm.RompOverridePm == null)
        {
            MessageBox.Show("Please choose a PM from the dropdown.",
                "No PM Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AcknowledgeDialog.RunAssignment(Window.GetWindow(this), () => vm.AssignRompOverride());
    }

    private void BtnUnassignRomp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.SelectedRomp is not { } romp) return;
        var confirm = MessageBox.Show(
            $"Remove the PM override from ROMP \"{romp.Name}\"? It will go back to following its site's PM.",
            "Confirm Remove Override", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        vm.UnassignRomp();
    }

    // Re-select the same campus IDs in the ListBox after the Campuses collection is reloaded
    private void RestoreListBoxSelection(IReadOnlySet<int> ids)
    {
        if (ids.Count == 0) return;
        _suppressListBoxEvent = true;
        CampusesListBox.SelectedItems.Clear();
        foreach (var item in CampusesListBox.Items)
        {
            if (item is Campus c && ids.Contains(c.Id))
                CampusesListBox.SelectedItems.Add(item);
        }
        _suppressListBoxEvent = false;
        if (Vm is { } vm)
            vm.SetSelectedCampuses(CampusesListBox.SelectedItems.Cast<Campus>());
    }

    // ── Site Assignments bulk import ────────────────────────────────────────────

    private void BtnDownloadTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new SaveFileDialog
        {
            Title      = "Save Site Assignments Template",
            FileName   = "Site_Assignments_Template",
            DefaultExt = ".xlsx",
            Filter     = "Excel Files|*.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sites = vm.GetAllSitesForExport();
            new SiteAssignmentImportService().GenerateTemplate(dlg.FileName, sites, vm.AllPms.ToList());

            var msg = sites.Count > 0
                ? $"Template saved with {sites.Count} current site(s) and their assignments.\n\nEdit the \"PM Data Name\" column, then use Import Assignments to apply changes."
                : "Template saved (no sites in database yet).\n\nFill in the \"Site Assignments\" sheet and use Import Assignments to load it.";
            MessageBox.Show(msg, "Template Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImportAssignments_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new OpenFileDialog
        {
            Title  = "Select Site Assignments File",
            Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var confirm = MessageBox.Show(
                "Import site assignments from this file?\n\n" +
                "Sites already assigned to a different PM than the sheet says will be skipped and reported, not overwritten. A site set to a PM different from its campus's PM gets its own PM.",
                "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var (result, messages) = new SiteAssignmentImportService().ImportAssignments(dlg.FileName);
            // Rows whose PM isn't on the team for that site are held back — offer them once, as a batch.
            if (result.OutsideTeam > 0 && AcknowledgeDialog.ConfirmOutsideTeam(Window.GetWindow(this),
                    $"{result.OutsideTeam} row(s) in this file assign a PM outside the team for that site:\n\n" +
                    string.Join("\n", messages.Where(m => m.Contains("outside the team")).Take(8)) +
                    (result.OutsideTeam > 8 ? $"\n…and {result.OutsideTeam - 8} more." : "")))
            {
                using (TeamRules.AllowOutsideTeam())
                {
                    var (second, secondMessages) = new SiteAssignmentImportService().ImportAssignments(dlg.FileName);
                    result = result with { Assigned = result.Assigned + second.Assigned, OutsideTeam = 0 };
                    messages = messages.Where(m => !m.Contains("outside the team")).ToList();
                    messages.Add($"{second.Assigned} row(s) assigned outside their team (acknowledged).");
                }
            }
            vm.RefreshAfterImport();

            var summary = $"Assigned: {result.Assigned}\nUnchanged: {result.Unchanged}\n" +
                          $"Conflicts (not changed): {result.Conflicts}\nErrors: {result.Errors}" +
                          (result.OutsideTeam > 0 ? $"\nOutside the team (not imported): {result.OutsideTeam}" : "");
            if (messages.Count > 0)
                summary += "\n\nDetails:\n" + string.Join("\n", messages.Take(30))
                         + (messages.Count > 30 ? $"\n...and {messages.Count - 30} more." : "");

            MessageBox.Show(summary, "Import Complete", MessageBoxButton.OK,
                result.Errors > 0 || result.Conflicts > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
