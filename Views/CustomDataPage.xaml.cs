using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class CustomDataPage : UserControl
{
    private static readonly CustomCellValueConverter CellConverter = new();

    private CustomDataViewModel? Vm => DataContext as CustomDataViewModel;

    public CustomDataPage()
    {
        InitializeComponent();
        Loaded += CustomDataPage_Loaded;
        DataContextChanged += CustomDataPage_DataContextChanged;
    }

    private void CustomDataPage_Loaded(object sender, RoutedEventArgs e) => RebuildColumns();

    // Rebuild grid columns whenever the VM's static field list or dynamic column list changes —
    // covers mode switches, filter reruns, and anything done through ManageColumnsDialog (same
    // VM instance), without every mutation site needing to remember to call the rebuild itself.
    private void CustomDataPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CustomDataViewModel oldVm) oldVm.PropertyChanged -= Vm_PropertyChanged;
        if (e.NewValue is CustomDataViewModel newVm) newVm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomDataViewModel.StaticFields) or nameof(CustomDataViewModel.DynamicColumns))
            RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (Vm is not { } vm) return;
        DataGridCustom.Columns.Clear();

        // Static columns — read-only, reflect real Order/ROMP data already in the database.
        foreach (var field in vm.StaticFields)
        {
            var binding = new Binding($"StaticValues[{field.Key}]") { Mode = BindingMode.OneWay };
            if (field.FieldType == ReportFieldType.Date)
                binding.StringFormat = "yyyy-MM-dd";
            DataGridCustom.Columns.Add(new DataGridTextColumn
            {
                Header       = field.DisplayName,
                Binding      = binding,
                IsReadOnly   = true,
            });
        }

        // Dynamic columns — editable, user-defined.
        foreach (var col in vm.DynamicColumns)
        {
            switch (col.ColumnType)
            {
                case "Bool":
                    DataGridCustom.Columns.Add(new DataGridCheckBoxColumn
                    {
                        Header  = col.Name,
                        Binding = new Binding($"DynamicValues[{col.Key}]")
                        {
                            Mode = BindingMode.TwoWay,
                            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                            Converter = CellConverter,
                            ConverterParameter = col.ColumnType,
                        },
                    });
                    break;

                case "Date":
                    DataGridCustom.Columns.Add(BuildDateColumn(col));
                    break;

                default: // Text, Number
                    DataGridCustom.Columns.Add(new DataGridTextColumn
                    {
                        Header  = col.Name,
                        Binding = new Binding($"DynamicValues[{col.Key}]")
                        {
                            Mode = BindingMode.TwoWay,
                            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                            Converter = CellConverter,
                            ConverterParameter = col.ColumnType,
                        },
                    });
                    break;
            }
        }
    }

    private DataGridTemplateColumn BuildDateColumn(CustomColumn col)
    {
        string bindingPath = $"DynamicValues[{col.Key}]";

        var cellTemplate = new DataTemplate();
        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new Binding(bindingPath)
        {
            Converter = CellConverter, ConverterParameter = "Date", StringFormat = "{0:yyyy-MM-dd}",
        });
        textFactory.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 0, 0));
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        cellTemplate.VisualTree = textFactory;

        var editTemplate = new DataTemplate();
        var dateFactory = new FrameworkElementFactory(typeof(DatePicker));
        dateFactory.SetBinding(DatePicker.SelectedDateProperty, new Binding(bindingPath)
        {
            Mode = BindingMode.TwoWay, Converter = CellConverter, ConverterParameter = "Date",
        });
        dateFactory.SetValue(DatePicker.BorderThicknessProperty, new Thickness(0));
        editTemplate.VisualTree = dateFactory;

        return new DataGridTemplateColumn
        {
            Header = col.Name,
            CellTemplate = cellTemplate,
            CellEditingTemplate = editTemplate,
        };
    }

    // ── Row lifecycle — only dynamic columns are ever actually editable ────────

    private void DataGridCustom_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not CustomDataRow row) return;
        vm.SaveRow(row);
    }

    // ── Mode + filter ────────────────────────────────────────────────────────

    private void RbModeOrders_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Mode = ReportMode.Orders;
    }

    private void RbModeSitesRomps_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Mode = ReportMode.SitesRomps;
    }

    private void BtnApplyFilter_Click(object sender, RoutedEventArgs e) => Vm?.ApplyFilter();

    // ── Setup screen ─────────────────────────────────────────────────────────

    private void BtnManageColumns_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new ManageColumnsDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
