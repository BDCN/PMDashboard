using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class OrdersPage : UserControl
{
    private OrdersViewModel? Vm => DataContext as OrdersViewModel;

    public OrdersPage()
    {
        InitializeComponent();
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Excel File",
            Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        if (Vm is not { } vm) return;

        try
        {
            // Reading a large workbook takes a while — keep the window responsive.
            var sourceType = new ConfigService().Get(ConfigKeys.SourceDataType, "VADATA");
            var file       = dlg.FileName;
            OrderSheetParse parse;
            vm.IsImporting     = true;
            vm.ImportingStatus = "Reading the Excel file…";
            try
            {
                // Columns come from Configuration → Source Data → Edit Column Mapping.
                parse = await Task.Run(() => new ExcelImportService().Import(file, sourceType));
            }
            finally
            {
                vm.IsImporting     = false;
                vm.ImportingStatus = "";
            }

            var mappingHint = "\n\nCheck the column mapping in Configuration → Source Data → Edit Column Mapping " +
                              $"(source type: {OrderColumnMapping.SourceLabel(sourceType)}).";
            if (!parse.CanImport)
            {
                MessageBox.Show(
                    "This file can't be imported — required columns weren't found:\n\n• " +
                    string.Join("\n• ", parse.MissingRequired) + mappingHint,
                    "Import — Columns Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var orders = parse.Orders;
            if (orders.Count == 0)
            {
                MessageBox.Show("No data rows found in the file.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Show where the key fields came from and what Job Name turns into, so a wrong
            // mapping is obvious BEFORE anything is saved.
            var confirm = MessageBox.Show(
                $"Found {orders.Count:N0} rows ({OrderColumnMapping.SourceLabel(sourceType)}).\n\n" +
                $"Job Name:  column {parse.Describe("JobName")}\n" +
                $"Customer Order:  column {parse.Describe("CustomerOrder")}\n" +
                $"Order Line:  column {parse.Describe("OrdLine")}\n\n" +
                OrdersViewModel.PreviewJobNames(orders) +
                (parse.MissingOptional.Count > 0
                    ? "\n\nNot in this file (left empty):\n• " + string.Join("\n• ", parse.MissingOptional.Take(8)) +
                      (parse.MissingOptional.Count > 8 ? $"\n• …and {parse.MissingOptional.Count - 8} more" : "")
                    : "") +
                "\n\nNew rows will be added, changed rows updated, unchanged rows skipped. Import now?" +
                "\n(If the clusters/sites above look wrong, choose No and fix the column mapping.)",
                "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var (success, message) = await vm.ImportWithLockAsync(orders);
            if (!success)
            {
                MessageBox.Show(message ?? "Import could not be started.", "Import Locked",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            // On success, the "Last Import" card on the page shows the results — no popup needed.
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Clear all imported order records? This cannot be undone.",
            "Confirm Clear", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            Vm?.ClearAll();
    }
}
