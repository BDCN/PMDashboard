using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

/// <summary>
/// The whole Travel Sync interaction — confirm, run with a blocking "please wait" dialog, show
/// the result, then switch the running app over to the other file. Shared by the sidebar's
/// Travel control (any signed-in user) and Configuration → Database (Super Admins).
///
/// The work runs on a background thread so the window keeps painting, but the dialog is modal
/// on purpose: an edit saved while the merge is running would land in the file SAT is about to
/// stop using, and be lost.
/// </summary>
public static class TravelSyncFlow
{
    /// <summary>Sync Before Travel. True when SAT is now in Travel Mode.</summary>
    public static bool GoOffline(Window? owner)
    {
        if (!DatabaseConfig.IsNetwork || DatabaseConfig.IsTravelMode) return false;

        var confirm = Box(owner,
            "Copy the network database to this computer so you can keep working without the office network?\n\n" +
            "• Until you sync back, other people won't see your changes, and you won't see theirs.\n" +
            "• When you're back on the network, click \"Sync After Travel\" (bottom of the sidebar).",
            "Go Offline (Sync Before Travel)", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return false;

        var path = DatabaseConfig.NetworkDbPath;
        var r = RunWithProgress(owner, "Copying the network database to this computer…",
                                () => SyncService.SyncBeforeTravel(path));
        Show(owner, r, "Sync Before Travel");
        if (r.Success) AfterSwitch("✈ Travel Mode on — you're working on this computer's copy.");
        return r.Success;
    }

    /// <summary>Sync After Travel. True when SAT is back on the network database.</summary>
    public static bool SyncBack(Window? owner)
    {
        if (!DatabaseConfig.IsNetwork || !DatabaseConfig.IsTravelMode) return false;

        var confirm = Box(owner,
            "Merge your changes from this trip into the network database?\n\n" +
            "• Records you added, changed or deleted are applied to the network, and everyone else's changes come back to you.\n" +
            "• When the same record was changed in both places, the most recent change wins (a delete counts as a change).\n" +
            "• Both databases are backed up first.\n\n" +
            "You need to be on the office network / VPN.",
            "Sync After Travel", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return false;

        var path = DatabaseConfig.NetworkDbPath;
        var r = RunWithProgress(owner, "Merging your changes with the network database…",
                                () => SyncService.SyncAfterTravel(path));
        Show(owner, r, "Sync After Travel");
        if (r.Success) AfterSwitch("Back on the network database — your trip's changes are merged.");
        return r.Success;
    }

    /// <summary>SAT now reads a different file: reload the app-lifetime caches, start/stop
    /// watching the share, update the banner/sidebar, and make open pages reload (the
    /// DataChanged path — it also shows <paramref name="toast"/>).</summary>
    private static void AfterSwitch(string toast)
    {
        try { StatusConfig.Reload(); ScoreConfig.Reload(); } catch { /* next start reloads anyway */ }
        App.NetworkWatcher.Restart();
        App.ShowTravelBanner();
        App.CloudSync.PublishExternalChange(toast);
    }

    private static MessageBoxResult Box(Window? owner, string text, string title, MessageBoxButton b, MessageBoxImage i) =>
        owner != null ? MessageBox.Show(owner, text, title, b, i) : MessageBox.Show(text, title, b, i);

    private static void Show(Window? owner, SyncResult r, string title) =>
        Box(owner, r.Message, title, MessageBoxButton.OK,
            r.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

    private static SyncResult RunWithProgress(Window? owner, string message, Func<SyncResult> work)
    {
        var done = false;
        var dlg = new Window
        {
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(1),
            Content = new StackPanel
            {
                Margin = new Thickness(28, 22, 28, 22),
                Width = 360,
                Children =
                {
                    new TextBlock { Text = message, FontSize = 13, FontWeight = FontWeights.SemiBold,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
                                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) },
                    new TextBlock { Text = "This can take a minute on a large database. Please don't close SAT.",
                                    FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) },
                    new ProgressBar { IsIndeterminate = true, Height = 6 },
                },
            },
        };
        dlg.Closing += (_, e) => { if (!done) e.Cancel = true; };   // no Alt+F4 mid-merge

        SyncResult result = new(false, "Sync did not run.");
        dlg.Loaded += async (_, _) =>
        {
            try { result = await Task.Run(work); }
            catch (Exception ex) { result = new SyncResult(false, $"Sync failed:\n{ex.Message}"); }
            done = true;
            dlg.Close();
        };
        dlg.ShowDialog();
        return result;
    }
}
