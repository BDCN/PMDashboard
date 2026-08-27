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
    }

    // ── Inline row save ──

    private void PmGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not PM pm) return;

        // Dispatch to Background priority so WPF finishes committing the row
        // before we call UpdatePm (which calls Load() and replaces the collection).
        Dispatcher.BeginInvoke(new Action(() => vm.UpdatePm(pm)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Add PM (dialog still used for new entries) ──

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditPmDialog(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.AddPm(dlg.Result!);
    }

    // ── Delete ──

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not PM pm) return;
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
