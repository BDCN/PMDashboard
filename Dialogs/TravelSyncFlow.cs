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
        if (r.Success) AfterSwitch(owner);
        return r.Success;
    }

    /// <summary>Back from the trip: SAT now works on its synced working copy of the network file
    /// (NetworkSync), which is set up at startup — so restart into it.</summary>
    private static void AfterSwitch(Window? owner)
    {
        Box(owner, "SAT will now restart to finish switching back to the shared database.", "Sync After Travel",
            MessageBoxButton.OK, MessageBoxImage.Information);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); }
        catch { /* the user can start it again themselves */ }
        Application.Current.Shutdown();
    }

    private static MessageBoxResult Box(Window? owner, string text, string title, MessageBoxButton b, MessageBoxImage i) =>
        owner != null ? MessageBox.Show(owner, text, title, b, i) : MessageBox.Show(text, title, b, i);

    private static void Show(Window? owner, SyncResult r, string title) =>
        Box(owner, r.Message, title, MessageBoxButton.OK,
            r.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

    internal static SyncResult RunWithProgress(Window? owner, string message, Func<SyncResult> work)
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
