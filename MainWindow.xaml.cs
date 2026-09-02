using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using PMDashboard.Services;
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

        // Re-clamp once WPF has applied WindowStartupLocation="CenterScreen", which
        // sets Left/Top after the source is initialised.
        Loaded += (_, _) => { FitToWorkArea(); RefreshCurrentUser(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitToWorkArea();  // size before first render, so there is no visible resize flash
    }

    /// <summary>
    /// Keep the window inside the desktop work area (screen minus taskbar).
    ///
    /// The declared 1400x820 default is in device-independent units, so on a smaller
    /// or display-scaled desktop it can exceed the available area. CenterScreen then
    /// centres that oversized window at negative Left/Top, pushing the title bar —
    /// and the minimise/maximise/close buttons on it — off the top and left edges
    /// where they cannot be reached or dragged back.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        if (WindowState != WindowState.Normal) return;  // maximised/minimised manage their own bounds

        // The minimums must not exceed the screen themselves, or the size clamp below
        // cannot take effect on a genuinely small display.
        MinWidth  = Math.Min(MinWidth,  area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);

        Width  = Math.Min(Width,  area.Width);
        Height = Math.Min(Height, area.Height);

        // Left/Top are NaN until the window has been positioned; skip the move until then.
        if (!double.IsNaN(Left))
            Left = Math.Min(Math.Max(Left, area.Left), area.Right - Width);
        if (!double.IsNaN(Top))
            Top = Math.Min(Math.Max(Top, area.Top), area.Bottom - Height);
    }

    /// <summary>Shows who is signed in; refreshed after a profile edit changes the name.</summary>
    private void RefreshCurrentUser()
    {
        TxtCurrentUser.Text     = CurrentUser.DisplayName;
        TxtCurrentUserRole.Text = CurrentUser.IsSuperAdmin ? "Super Admin" : "Signed in";
    }

    private void BtnProfile_Click(object sender, RoutedEventArgs e)
    {
        new Dialogs.UserProfileDialog { Owner = this }.ShowDialog();
        RefreshCurrentUser();
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
