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

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Excel File",
            Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

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

            if (Vm is not { } vm) return;
            var result = vm.UpsertOrders(orders);
            MessageBox.Show(
                $"Import complete.\n\n" +
                $"Orders:  {result.Added} added,  {result.Updated} updated,  {result.Skipped} unchanged\n" +
                (result.DuplicatesSkipped > 0
                    ? $"Duplicate rows skipped:  {result.DuplicatesSkipped}\n"
                    : "") +
                $"Locations created:  {result.LocationsCreated}\n" +
                $"Sites created:  {result.SitesCreated}\n" +
                $"ROMPs created:  {result.RompsCreated}",
                "Done", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        Vm?.Search(TxtSearch.Text);
    }
}
