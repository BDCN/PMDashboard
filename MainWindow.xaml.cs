using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using PMDashboard.ViewModels;

namespace PMDashboard;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _sidebarCollapsed;

    public MainWindow()
    {
        _vm = new MainViewModel();  // must be set before InitializeComponent so event handlers are safe
        InitializeComponent();
        DataContext = _vm;
    }

    private void NavPm_Click(object sender, RoutedEventArgs e)       => _vm.NavigateToPm();
    private void NavSites_Click(object sender, RoutedEventArgs e)    => _vm.NavigateToSites();
    private void NavOrders_Click(object sender, RoutedEventArgs e)   => _vm.NavigateToOrders();
    private void NavReportBuilder_Click(object sender, RoutedEventArgs e) => _vm.NavigateToReportBuilder();
    private void NavCustomData_Click(object sender, RoutedEventArgs e) => _vm.NavigateToCustomData();
    private void NavWorkload_Click(object sender, RoutedEventArgs e) => _vm.NavigateToWorkload();
    private void NavConfig_Click(object sender, RoutedEventArgs e)      => _vm.NavigateToConfig();
    private void NavAssignment_Click(object sender, RoutedEventArgs e) => _vm.NavigateToAssignment();

    private void CboGeoFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboGeoFilter.SelectedItem is ComboBoxItem item)
            _vm.GeoFilter = item.Content?.ToString() ?? "All";
    }

    private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;

        if (_sidebarCollapsed)
        {
            SidebarContent.Visibility    = Visibility.Collapsed;
            SidebarColumn.Width          = new GridLength(44);
            BtnToggleSidebar.Content     = "»";
            BtnToggleSidebar.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            SidebarContent.Visibility    = Visibility.Visible;
            SidebarColumn.Width          = new GridLength(220);
            BtnToggleSidebar.Content     = "«";
            BtnToggleSidebar.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }
}
