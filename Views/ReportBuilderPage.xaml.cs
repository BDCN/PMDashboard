using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class ReportBuilderPage : UserControl
{
    private ReportBuilderViewModel? Vm => DataContext as ReportBuilderViewModel;
    private bool _suppressListEvents;

    public ReportBuilderPage()
    {
        InitializeComponent();
        Loaded += ReportBuilderPage_Loaded;
        DataContextChanged += ReportBuilderPage_DataContextChanged;
    }

    private void ReportBuilderPage_Loaded(object sender, RoutedEventArgs e)
    {
        RebuildColumnPicker();
        RebuildResultsColumns();
        RestoreListBoxSelections();
    }

    // Rebuilding the dynamic column picker/grid only from button click handlers is fragile —
    // any path that changes Mode/SelectedColumnKeys without remembering to call the rebuild
    // (e.g. loading a saved report) leaves the grid bound to stale column keys. Driving the
    // rebuild off the VM's own PropertyChanged instead makes it unconditional.
    private void ReportBuilderPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ReportBuilderViewModel oldVm) oldVm.PropertyChanged -= Vm_PropertyChanged;
        if (e.NewValue is ReportBuilderViewModel newVm) newVm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportBuilderViewModel.AvailableFields))
        {
            RebuildColumnPicker();
            RebuildResultsColumns();
            RestoreListBoxSelections();
        }
        else if (e.PropertyName == nameof(ReportBuilderViewModel.SelectedColumnKeys))
        {
            RebuildColumnPicker();
            RebuildResultsColumns();
        }
    }

    // ── Mode switch ──────────────────────────────────────────────────────────

    private void RbModeOrders_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Mode = ReportMode.Orders;
    }

    private void RbModeSitesRomps_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Mode = ReportMode.SitesRomps;
    }

    // ── Dynamic column picker ───────────────────────────────────────────────

    private void RebuildColumnPicker()
    {
        if (Vm is not { } vm) return;
        ColumnPickerPanel.Children.Clear();
        foreach (var field in vm.AvailableFields)
        {
            var cb = new CheckBox
            {
                Content    = field.DisplayName,
                Tag        = field.Key,
                IsChecked  = vm.SelectedColumnKeys.Contains(field.Key),
                FontSize   = 12,
                Margin     = new Thickness(0, 0, 0, 6),
            };
            cb.Checked   += ColumnCheckBox_Changed;
            cb.Unchecked += ColumnCheckBox_Changed;
            ColumnPickerPanel.Children.Add(cb);
        }
    }

    private void ColumnCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not CheckBox { Tag: string key } cb) return;
        vm.ToggleColumn(key, cb.IsChecked == true);
        RebuildResultsColumns();
    }

    // ── Dynamic results grid columns ────────────────────────────────────────

    private void RebuildResultsColumns()
    {
        if (Vm is not { } vm) return;
        ResultsGrid.Columns.Clear();
        foreach (var key in vm.SelectedColumnKeys)
        {
            var field = vm.AvailableFields.FirstOrDefault(f => f.Key == key);
            if (field == null) continue;

            var binding = new Binding($"Values[{field.Key}]")
            {
                Mode           = BindingMode.OneWay,
                TargetNullValue = "",
            };
            if (field.FieldType == ReportFieldType.Date)
                binding.StringFormat = "yyyy-MM-dd";

            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header  = field.DisplayName,
                Binding = binding,
            });
        }
    }

    // ── Multi-select filter ListBoxes ───────────────────────────────────────

    private void LocationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents || Vm is not { } vm) return;
        vm.SetSelectedLocations(LocationsListBox.SelectedItems.Cast<Location>());
    }

    private void SitesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents || Vm is not { } vm) return;
        vm.SetSelectedSites(SitesListBox.SelectedItems.Cast<Site>());
    }

    private void PmsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents || Vm is not { } vm) return;
        vm.SetSelectedPms(PmsListBox.SelectedItems.Cast<PM>());
    }

    private void RompStatusesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents || Vm is not { } vm) return;
        vm.SetSelectedRompStatuses(RompStatusesListBox.SelectedItems.Cast<string>());
    }

    private void OrderStatusesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents || Vm is not { } vm) return;
        vm.SetSelectedOrderStatuses(OrderStatusesListBox.SelectedItems.Cast<string>());
    }

    private void BtnClearLocations_Click(object sender, RoutedEventArgs e)     => LocationsListBox.SelectedItems.Clear();
    private void BtnClearSites_Click(object sender, RoutedEventArgs e)         => SitesListBox.SelectedItems.Clear();
    private void BtnClearPms_Click(object sender, RoutedEventArgs e)           => PmsListBox.SelectedItems.Clear();
    private void BtnClearRompStatuses_Click(object sender, RoutedEventArgs e)  => RompStatusesListBox.SelectedItems.Clear();
    private void BtnClearOrderStatuses_Click(object sender, RoutedEventArgs e) => OrderStatusesListBox.SelectedItems.Clear();

    private void RestoreListBoxSelections()
    {
        if (Vm is not { } vm) return;
        _suppressListEvents = true;

        LocationsListBox.SelectedItems.Clear();
        foreach (var item in LocationsListBox.Items)
            if (item is Location l && vm.GetSelectedLocationIds().Contains(l.Id))
                LocationsListBox.SelectedItems.Add(item);

        SitesListBox.SelectedItems.Clear();
        foreach (var item in SitesListBox.Items)
            if (item is Site s && vm.GetSelectedSiteIds().Contains(s.Id))
                SitesListBox.SelectedItems.Add(item);

        PmsListBox.SelectedItems.Clear();
        foreach (var item in PmsListBox.Items)
            if (item is PM p && vm.GetSelectedPmIds().Contains(p.Id))
                PmsListBox.SelectedItems.Add(item);

        RompStatusesListBox.SelectedItems.Clear();
        foreach (var item in RompStatusesListBox.Items)
            if (item is string st && vm.GetSelectedRompStatuses().Contains(st))
                RompStatusesListBox.SelectedItems.Add(item);

        OrderStatusesListBox.SelectedItems.Clear();
        foreach (var item in OrderStatusesListBox.Items)
            if (item is string st && vm.GetSelectedOrderStatuses().Contains(st))
                OrderStatusesListBox.SelectedItems.Add(item);

        _suppressListEvents = false;
    }

    // ── Run report ───────────────────────────────────────────────────────────

    private void BtnRunReport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // Orders is now 100,000+ rows and growing — an unfiltered run loads all of it into
        // memory. Ask before doing that by accident rather than silently eating minutes/RAM.
        if (vm.Mode == ReportMode.Orders && vm.DateFrom == null && vm.DateTo == null)
        {
            var confirm = MessageBox.Show(
                "No date filter is set. This will load every Order in the database — currently " +
                "100,000+ rows — which can be slow and use significant memory.\n\nContinue anyway?",
                "No Date Filter", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        vm.RunQuery();
    }

    // ── Chart controls ──────────────────────────────────────────────────────

    private void RbAggCount_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.ChartAggregateOp = "Count";
    }

    private void RbAggSum_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.ChartAggregateOp = "Sum";
    }

    private void ChartGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Vm?.ApplyChartScale(Math.Max(0, e.NewSize.Width - 150));
    }

    // ── Saved reports ────────────────────────────────────────────────────────

    private void BtnLoadSavedReport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.SelectedSavedReport == null)
        {
            MessageBox.Show("Please select a saved report first.", "No Report Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        vm.LoadSavedReport(vm.SelectedSavedReport);
        RebuildColumnPicker();
        RebuildResultsColumns();
        RestoreListBoxSelections();
    }

    private void BtnSaveAsNew_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new TextInputDialog("Save Report", "Enter a name for this report.")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        vm.SaveAsNew(dlg.Value);
    }

    private void BtnUpdateSavedReport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.SelectedSavedReport == null)
        {
            MessageBox.Show("Please select a saved report first.", "No Report Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        vm.UpdateExisting(vm.SelectedSavedReport);
    }

    private void BtnRenameSavedReport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.SelectedSavedReport == null)
        {
            MessageBox.Show("Please select a saved report first.", "No Report Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new TextInputDialog("Rename Report", "Enter a new name.", vm.SelectedSavedReport.Name)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        vm.RenameSavedReport(vm.SelectedSavedReport, dlg.Value);
    }

    private void BtnDeleteSavedReport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.SelectedSavedReport == null)
        {
            MessageBox.Show("Please select a saved report first.", "No Report Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show($"Delete \"{vm.SelectedSavedReport.Name}\"?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        vm.DeleteSavedReport(vm.SelectedSavedReport);
    }

    // ── Export ───────────────────────────────────────────────────────────────

    private void BtnExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.Results.Count == 0)
        {
            MessageBox.Show("Run a report first — there are no results to export.", "Nothing to Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title      = "Export Report to Excel",
            FileName   = "Report",
            DefaultExt = ".xlsx",
            Filter     = "Excel Files|*.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            vm.ExportToExcel(dlg.FileName);
            MessageBox.Show("Report exported successfully.", "Export Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not export report:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
