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
            var service    = new ExcelImportService();
            var sourceType = new ConfigService().Get(ConfigKeys.SourceDataType, "VADATA");
            var orders = sourceType switch
            {
                "Tableau"   => service.ImportTableauOrders(dlg.FileName),
                "OpenLines" => service.ImportOpenLinesOrders(dlg.FileName),
                _           => service.ImportOrders(dlg.FileName),
            };
            if (orders.Count == 0)
            {
                MessageBox.Show("No data rows found in the file.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Found {orders.Count} rows. New rows will be added; changed rows will be updated; unchanged rows will be skipped.\n\nLocations, Sites, and ROMPs will be created automatically from Job Name.",
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
