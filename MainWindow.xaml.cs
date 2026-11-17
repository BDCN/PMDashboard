using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _sidebarCollapsed;
    private DispatcherTimer? _toastTimer;

    public MainWindow()
    {
        _vm = new MainViewModel();  // must be set before InitializeComponent so event handlers are safe
        InitializeComponent();
        DataContext = _vm;

        Loaded += (_, _) => { FitToWorkArea(); RefreshCurrentUser(); UpdateSyncIndicator(); };

        // Keep the sidebar highlight on whichever page is showing — including pages reached from
        // another page rather than the sidebar (e.g. Coverage Planner → "Model settings" opens
        // Configuration). PM Detail has no sidebar entry and is reached from Workload, so it
        // keeps Workload highlighted.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.CurrentPage)) return;
            RadioButton? nav = _vm.CurrentPage switch
            {
                WorkloadViewModel or PmDetailViewModel => NavWorkload,
                AssignmentViewModel       => NavAssignment,
                SitesViewModel            => NavSites,
                PmMaintenanceViewModel    => NavPm,
                TeamsViewModel            => NavTeams,
                OrgChartViewModel         => NavOrgChart,
                ReportBuilderViewModel    => NavReportBuilder,
                CustomDataViewModel       => NavCustomData,
                OrdersViewModel           => NavOrders,
                ForecastPlanningViewModel => NavForecastPlanning,
                ForecastTimelineViewModel => NavForecastTimeline,
                CoveragePlannerViewModel  => NavCoveragePlanner,
                UserManagementViewModel   => NavUserManagement,
                ConfigViewModel           => NavConfig,
                _                         => null,
            };
            if (nav != null && nav.IsChecked != true) nav.IsChecked = true;
        };

        // App-lifetime subscriptions to the background sync service — never unsubscribed,
        // since MainWindow itself lives for the life of the app.
        App.CloudSync.DataChanged     += OnCloudDataChanged;
        App.CloudSync.StatusChanged   += OnCloudSyncStatusForLastUpdated;
        App.CloudSync.StatusChanged   += OnCloudSyncStatusForIndicator;
        App.CloudSync.ProgressChanged += OnCloudSyncProgress;
        App.CloudSync.VersionWarningChanged += () => Dispatcher.InvokeAsync(UpdateVersionBanner);

        Title                 = $"SAT - Site Assignment Tracker  {AppVersion.Short}";
        TxtAppVersion.Text    = AppVersion.Short;
        TxtAppVersion.ToolTip = AppVersion.Full;
        UpdateVersionBanner();
        UpdateTravelPanel();
        NetworkSync.StatusChanged += () => Dispatcher.InvokeAsync(UpdateTravelPanel);
    }

    private void UpdateVersionBanner()
    {
        if (App.CloudSync.NewerVersionInUse is { } newer)
        {
            TxtUpdateBanner.Text =
                $"A newer version of SAT is in use ({newer}). This computer is running {AppVersion.Short} — " +
                "please install the latest version so changes from other computers sync correctly.";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        else if (DatabaseConfig.IsNetwork && DatabaseConfig.IsTravelMode)
        {
            // Easy to forget you're on a laptop copy — and until it's merged back, nobody else
            // sees anything changed here (and you don't see their changes).
            TxtUpdateBanner.Text =
                "✈ Travel Mode — you're working on this computer's own copy of the database. Other people won't see " +
                "your changes (and you won't see theirs) until you're back on the office network and run " +
                "Configuration → Database → Sync After Travel.";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        else
            UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>Re-evaluates the header banner and the sidebar Travel control — called after
    /// Travel Sync switches files.</summary>
    public void RefreshBanner()
    {
        UpdateVersionBanner();
        UpdateTravelPanel();
    }

    /// <summary>Sidebar database control (every user): where SAT's data is right now, and the
    /// actions that make sense from there.</summary>
    private void UpdateTravelPanel()
    {
        TravelPanel.Visibility = Visibility.Visible;
        BtnConnectDb.Visibility = Visibility.Visible;
        BtnTravel.Visibility    = Visibility.Visible;
        if (!DatabaseConfig.IsNetwork || string.IsNullOrWhiteSpace(DatabaseConfig.NetworkDbPath))
        {
            TxtTravelIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            TxtTravelState.Text      = "This computer only (not shared)";
            BtnTravel.Visibility     = Visibility.Collapsed;
            BtnConnectDb.Content     = "⇄ Connect to network database…";
            BtnConnectDb.ToolTip     = "Use the team's shared SAT database on the network.";
            return;
        }
        BtnConnectDb.Content = "⇄ Change network database…";
        BtnConnectDb.ToolTip = DatabaseConfig.NetworkDbPath;
        if (DatabaseConfig.IsTravelMode)
        {
            BtnConnectDb.Visibility = Visibility.Collapsed; // sync back first
            TxtTravelIcon.Foreground  = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            TxtTravelState.Text       = DatabaseConfig.TravelStartedUtc is { } since
                ? $"Travel Mode — offline copy since {since.ToLocalTime():MMM d, h:mm tt}"
                : "Travel Mode — offline copy";
            BtnTravel.Content         = "⇅ Sync After Travel…";
            BtnTravel.ToolTip         = "Back on the office network? Merge your changes into the network database.";
        }
        else
        {
            // Background sync (Services/NetworkSync) — this computer works on its own copy.
            var last = NetworkSync.LastSyncUtc is { } t ? $" · synced {t.ToLocalTime():h:mm tt}" : "";
            if (NetworkSync.IsSyncing && NetworkSync.LastSyncUtc == null)
            {
                TxtTravelIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD));
                TxtTravelState.Text      = "Shared database · syncing…";
            }
            else if (NetworkSync.IsOffline)
            {
                TxtTravelIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                TxtTravelState.Text      = "Offline — saving on this computer; sends when the network is back" + last;
            }
            else if (NetworkSync.LastError is { } err)
            {
                TxtTravelIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                TxtTravelState.Text      = err + last;
            }
            else
            {
                TxtTravelIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                TxtTravelState.Text      = "Shared database" + last;
            }
            TxtTravelState.ToolTip = DatabaseConfig.NetworkDbPath;
            BtnTravel.Content      = NetworkSync.IsSyncing ? "⇅ Syncing…" : "⇅ Sync now";
            BtnTravel.IsEnabled    = !NetworkSync.IsSyncing;
            BtnTravel.ToolTip      = "Send this computer's changes and fetch everyone else's now (it also happens every 30 seconds).";
        }
    }

    private async void BtnTravel_Click(object sender, RoutedEventArgs e)
    {
        if (DatabaseConfig.IsTravelMode) { Dialogs.TravelSyncFlow.SyncBack(this); RefreshBanner(); return; }
        BtnTravel.IsEnabled = false;
        BtnTravel.Content   = "⇅ Syncing…";
        await NetworkSync.SyncNowAsync();
        UpdateTravelPanel();
    }

    private void BtnConnectDb_Click(object sender, RoutedEventArgs e)
    {
        // True → a new SAT is already starting on the chosen database; this one closes.
        if (Dialogs.DatabaseConnectFlow.ConnectToNetwork(this))
            Application.Current.Shutdown();
    }

    private void BtnDismissUpdateBanner_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    /// <summary>Non-distracting cross-user notification — see requirement 4 (CloudSyncService.DataChanged).</summary>
    private void OnCloudDataChanged(string summary) =>
        Dispatcher.InvokeAsync(() => ShowToast(summary));

    /// <summary>
    /// Fires on every StatusChanged, but only updates the label on a genuine sync completion —
    /// LastSyncTime is set solely by SyncTick's and PushNowAndWaitAsync's success paths, not by
    /// the per-save "Pushed N record(s)" notices or error/connecting messages.
    /// </summary>
    private void OnCloudSyncStatusForLastUpdated(string _)
    {
        if (App.CloudSync.LastSyncTime is not { } when) return;
        Dispatcher.InvokeAsync(() => SetLastUpdated(when));
    }

    private void OnCloudSyncStatusForIndicator(string _) => Dispatcher.InvokeAsync(UpdateSyncIndicator);
    private void OnCloudSyncProgress()                   => Dispatcher.InvokeAsync(UpdateSyncIndicator);

    /// <summary>
    /// Sidebar "syncing…" indicator — see MainWindow.xaml's SyncIndicator for layout. Hidden
    /// entirely outside Cloud mode. While CloudSyncService.IsSyncing (the initial pull every
    /// launch does, or the one-time-per-machine full push — see its ProgressChanged doc for why
    /// ordinary ticks don't populate progress), shows a percent + bar + ETA; otherwise shows a
    /// compact "Synced HH:mm:ss" line.
    /// </summary>
    private void UpdateSyncIndicator()
    {
        if (!CloudSyncService.IsAvailable)
        {
            SyncIndicator.Visibility = Visibility.Collapsed;
            return;
        }
        SyncIndicator.Visibility = Visibility.Visible;

        if (App.CloudSync.IsSyncing)
        {
            SyncActivePanel.Visibility = Visibility.Visible;
            SyncIdlePanel.Visibility   = Visibility.Collapsed;

            if (App.CloudSync.SyncPercent is { } percent)
            {
                TxtSyncPercent.Text = $"{percent:0}%";
                ColFill.Width = new GridLength(percent, GridUnitType.Star);
                ColRest.Width = new GridLength(100 - percent, GridUnitType.Star);
            }
            else
            {
                TxtSyncPercent.Text = "";
                ColFill.Width = new GridLength(0, GridUnitType.Star);
                ColRest.Width = new GridLength(100, GridUnitType.Star);
            }

            TxtSyncEta.Text = App.CloudSync.SyncEta is { } eta ? $"~{FormatEta(eta)} remaining" : "";
        }
        else
        {
            SyncActivePanel.Visibility = Visibility.Collapsed;
            SyncIdlePanel.Visibility   = Visibility.Visible;
            TxtSyncIdle.Text = App.CloudSync.LastSyncTime is { } when
                ? $"Synced {when:HH:mm:ss}"
                : "Connecting…";
        }
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalSeconds < 1)   return "a few seconds";
        if (eta.TotalMinutes < 1)   return $"{eta.Seconds}s";
        if (eta.TotalHours < 1)     return $"{eta.Minutes}m {eta.Seconds}s";
        return $"{(int)eta.TotalHours}h {eta.Minutes}m";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitToWorkArea();  // size before first render, so there is no visible resize flash
    }

    /// <summary>
    /// Keep the window inside the desktop work area (screen minus taskbar).
    ///
    /// The declared 1400x820 default is in device-independent units, so on a smaller or
    /// display-scaled desktop it can exceed the available area. CenterScreen then centres
    /// that oversized window at negative Left/Top, pushing the title bar — and the
    /// minimise/maximise/close buttons on it — off the top and left edges where they
    /// cannot be reached or dragged back.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        if (WindowState != WindowState.Normal) return;  // maximised/minimised manage their own bounds

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
        TxtCurrentUserRole.Text = CurrentUser.IsSuperAdmin ? "Super Admin"
            : CurrentUser.IsDirector ? "Director" : "Signed in";
    }

    private void BtnProfile_Click(object sender, RoutedEventArgs e)
    {
        new Dialogs.UserProfileDialog { Owner = this }.ShowDialog();
        RefreshCurrentUser();
    }

    /// <summary>
    /// Manual "sync right now" — runs the same push+pull cycle as the periodic timer, just
    /// on demand, for troubleshooting ("did my upload actually reach the cloud yet?") instead
    /// of waiting out the ~30s tick. Button stays clickable during the sync; the result shows
    /// up via the usual StatusChanged → "Synced HH:mm:ss (↑N ↓M)" text once it finishes.
    /// </summary>
    private async void BtnSyncNow_Click(object sender, RoutedEventArgs e) => await App.CloudSync.SyncNowAsync();

    /// <summary>
    /// Small, non-modal, auto-dismissing notification — deliberately not a MessageBox or
    /// anything that steals focus. See requirement 4 (CloudSyncService.DataChanged).
    /// </summary>
    public void ShowToast(string message)
    {
        TxtToast.Text        = message;
        ToastBorder.Visibility = Visibility.Visible;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) =>
        {
            ToastBorder.Visibility = Visibility.Collapsed;
            _toastTimer?.Stop();
        };
        _toastTimer.Start();
    }

    /// <summary>"Last updated: HH:mm" — refreshed whenever a sync pull/push completes.</summary>
    public void SetLastUpdated(DateTime whenLocal) =>
        TxtLastUpdated.Text = $"Last updated: {whenLocal:HH:mm}";

    private void NavPm_Click(object sender, RoutedEventArgs e)       => _vm.NavigateToPm();
    private void NavTeams_Click(object sender, RoutedEventArgs e)    => _vm.NavigateToTeams();
    private void NavOrgChart_Click(object sender, RoutedEventArgs e) => _vm.NavigateToOrgChart();
    private void NavSites_Click(object sender, RoutedEventArgs e)    => _vm.NavigateToSites();
    private void NavOrders_Click(object sender, RoutedEventArgs e)   => _vm.NavigateToOrders();
    private void NavForecastPlanning_Click(object sender, RoutedEventArgs e) => _vm.NavigateToForecastPlanning();
    private void NavForecastTimeline_Click(object sender, RoutedEventArgs e) => _vm.NavigateToForecastTimeline();
    private void NavCoveragePlanner_Click(object sender, RoutedEventArgs e) => _vm.NavigateToCoveragePlanner();
    private void NavReportBuilder_Click(object sender, RoutedEventArgs e) => _vm.NavigateToReportBuilder();
    private void NavCustomData_Click(object sender, RoutedEventArgs e) => _vm.NavigateToCustomData();
    private void NavWorkload_Click(object sender, RoutedEventArgs e) => _vm.NavigateToWorkload();
    private void NavConfig_Click(object sender, RoutedEventArgs e)      => _vm.NavigateToConfig();
    private void NavUserManagement_Click(object sender, RoutedEventArgs e) => _vm.NavigateToUserManagement();
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
