using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class PmMaintenancePage : UserControl
{
    private PmMaintenanceViewModel? Vm => DataContext as PmMaintenanceViewModel;

    public PmMaintenancePage()
    {
        InitializeComponent();

        // Site Count Only mode: these columns drive numbers nobody uses in that mode, so hide
        // them rather than leave editable fields that quietly do nothing.
        if (new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Count")
        {
            ColSiteCap.Visibility    = Visibility.Collapsed;
            ColScoreCap.Visibility   = Visibility.Collapsed;
            ColMultiplier.Visibility = Visibility.Collapsed;
            ColIdealUtil.Visibility  = Visibility.Collapsed;
        }
    }

    // ── Inline row save ──

    private bool _wasActiveBeforeEdit;

    // Captures the pre-edit Active state — by the time RowEditEnding fires, a checkbox cell's
    // two-way binding has already pushed the new value into the PM object, so there's no other
    // way to tell whether this edit is a true->false transition.
    private void PmGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is PM pm) _wasActiveBeforeEdit = pm.IsActive;
    }

    private void PmGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not PM pm) return;

        if (_wasActiveBeforeEdit && !pm.IsActive && !ResolveAssignmentsBeforeChange(vm, pm, isDeleting: false))
        {
            // User cancelled the reassignment prompt — the checkbox's binding already flipped
            // pm.IsActive to false in memory, but PM has no INotifyPropertyChanged, so the only
            // reliable way to restore the visual "checked" state is to reload from the DB
            // (nothing was saved yet, so the DB still has IsActive = true).
            e.Cancel = true;
            vm.Load();
            return;
        }

        // Dispatch to Background priority so WPF finishes committing the row
        // before we call UpdatePm (which calls Load() and replaces the collection).
        Dispatcher.BeginInvoke(new Action(() => vm.UpdatePm(pm)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// If this PM currently holds campus assignments and/or individual ROMP overrides, prompts
    /// for what should happen to them before a deactivation or delete proceeds. Returns true if
    /// the caller should go ahead (nothing assigned, or the prompt was resolved — reassigned or
    /// unassigned); false if the user cancelled, in which case the caller must not proceed.
    /// </summary>
    private bool ResolveAssignmentsBeforeChange(PmMaintenanceViewModel vm, PM pm, bool isDeleting)
    {
        var summary = vm.GetAssignmentSummary(pm.Id);
        if (summary.Total == 0) return true;

        var dlg = new PmReassignmentPromptDialog(pm.FullName, summary, isDeleting)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return false;

        if (dlg.Choice == PmReassignmentChoice.Unassign)
        {
            vm.ReassignAll(pm.Id, null);
            return true;
        }

        if (dlg.Choice == PmReassignmentChoice.Reassign)
        {
            var candidates = vm.GetAllPms().Where(p => p.Id != pm.Id && p.IsActive).ToList();
            if (candidates.Count == 0)
            {
                MessageBox.Show("There are no other active PMs to assign these to.",
                    "No PMs Available", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            var pickDlg = new AssignPmDialog($"Reassign from {pm.FullName} to:", candidates, null)
                { Owner = Window.GetWindow(this) };
            if (pickDlg.ShowDialog() != true || pickDlg.SelectedPm == null) return false;
            try { vm.ReassignAll(pm.Id, pickDlg.SelectedPm.Id); }
            catch (TeamRuleException ex)
            {
                MessageBox.Show(ex.Message, "Outside the Team's Area", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        return false;
    }

    // ── Add PM (dialog still used for new entries) ──

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditPmDialog(null, vm.GetAllPms()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        vm.AddPm(dlg.Result!);
        if (vm.SelectedPm != null) PmGrid.ScrollIntoView(vm.SelectedPm);
    }

    // ── Edit (full dialog) ──

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PM pm) return;
        bool wasActive = pm.IsActive;
        var dlg = new AddEditPmDialog(pm, vm.GetAllPms()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        if (wasActive && dlg.Result!.IsActive == false
            && !ResolveAssignmentsBeforeChange(vm, pm, isDeleting: false))
        {
            // User cancelled the reassignment prompt — force this save to keep the PM active
            // rather than silently deactivating someone who still has assignments.
            dlg.Result.IsActive = true;
        }
        vm.UpdatePm(dlg.Result!);
    }

    // ── Delete ──

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PM pm) return;

        if (!ResolveAssignmentsBeforeChange(vm, pm, isDeleting: true)) return;

        var result = MessageBox.Show(
            $"Delete {pm.FullName}?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) vm.DeletePm(pm);
    }

    // ── Template download ──

    private void BtnDownloadTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new SaveFileDialog
        {
            Title      = "Save PM Import Template",
            FileName   = "PM_Import_Template",
            DefaultExt = ".xlsx",
            Filter     = "Excel Files|*.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var pms = vm.GetAllPms();
            new PmImportService().GenerateTemplate(dlg.FileName, pms);

            var msg = pms.Count > 0
                ? $"Template saved with {pms.Count} current PM record(s).\n\nEdit or add rows in the \"PM Bulk Import\" sheet, then use Import Excel to reload."
                : "Template saved (no PMs in database yet).\n\nFill in the \"PM Bulk Import\" sheet and use Import Excel to load it.";
            MessageBox.Show(msg, "Template Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save template:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Excel import ──

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new OpenFileDialog
        {
            Title  = "Select PM Import File",
            Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var service = new PmImportService();
            var (pms, errors) = service.ParsePms(dlg.FileName);

            if (pms.Count == 0 && errors.Count == 0)
            {
                MessageBox.Show("No data rows found in the file.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var summary = $"Found {pms.Count} valid PM(s) to import.";
            if (errors.Count > 0)
                summary += $"\n\n{errors.Count} row(s) will be skipped:\n" + string.Join("\n", errors);

            if (pms.Count == 0)
            {
                MessageBox.Show(summary, "Nothing to Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(summary + "\n\nProceed?",
                "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            int added = vm.BulkAddPms(pms);
            MessageBox.Show($"Successfully added {added} PM(s).", "Done",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Search ──

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        Vm?.Search(TxtSearch.Text);
    }
}
