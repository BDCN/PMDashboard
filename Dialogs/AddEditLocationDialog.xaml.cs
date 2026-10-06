using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class AddEditLocationDialog : Window
{
    private readonly Location? _existing;
    public Location? Result         { get; private set; }
    public string?   BulkRompStatus { get; private set; } // null = no change
    public bool      CloseAllSites  { get; private set; } // sets Site.IsClosed on every site here too

    public AddEditLocationDialog(Location? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing != null)
        {
            TxtTitle.Text = "Edit Cluster";
            TxtName.Text  = existing.Name;
            SelectRegion(existing.GeographicalLocation);
            BulkRompSection.Visibility      = Visibility.Visible; // only show for edits
            CloseLocationSection.Visibility = Visibility.Visible; // only show for edits

            // Bulk ROMP status dropdown: "No change" + statuses available to assign, same
            // pattern as AddEditSiteDialog's campus-level version — disabled statuses excluded
            // since this is a prospective bulk assignment, not a display of current state.
            var bulkItems = new List<string> { "No change" };
            bulkItems.AddRange(StatusConfig.Selectable);
            CboBulkRompStatus.ItemsSource   = bulkItems;
            CboBulkRompStatus.SelectedIndex = 0;

            ShowCurrentStatuses(existing.Id);
        }
        else
        {
            CboRegion.SelectedIndex = 0;
        }
    }

    /// <summary>Same idea as AddEditSiteDialog's version, aggregated across every ROMP at every
    /// Campus under this cluster instead of just one campus — plus any zero-ROMP campus standing
    /// in for itself via Site.StatusWhenEmpty, counted the same way a real ROMP would be.</summary>
    private void ShowCurrentStatuses(int locationId)
    {
        using var db = new AppDbContext();
        var counts = db.Romps.Where(r => r.Site.LocationId == locationId)
            .GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        var emptyStandIns = db.Sites
            .Where(s => s.LocationId == locationId && !s.Romps.Any() && s.StatusWhenEmpty != null)
            .GroupBy(s => s.StatusWhenEmpty!)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var (status, count) in emptyStandIns)
            counts[status] = counts.GetValueOrDefault(status) + count;

        if (counts.Count == 0) return; // no ROMPs and no stand-ins — nothing to summarize

        var parts = StatusConfig.All
            .Where(s => counts.ContainsKey(s))
            .Select(s => $"{counts[s]} {s}")
            .Concat(counts.Keys.Except(StatusConfig.All, System.StringComparer.OrdinalIgnoreCase)
                .Select(s => $"{counts[s]} {s}")); // defensive — a status removed from config but still referenced

        var total = counts.Values.Sum();
        TxtCurrentStatuses.Text = $"Currently: {string.Join(", ", parts)}  ({total} total — ROMPs plus any empty site standing in for itself)";
        TxtCurrentStatuses.Visibility = Visibility.Visible;
    }

    // Auto-lookup when the user leaves the code field
    private void TxtName_LostFocus(object sender, RoutedEventArgs e)
    {
        var code = TxtName.Text.Trim();
        if (code.Length < 2) return;

        var info = AirportLookup.Lookup(code);
        if (info != null)
        {
            SelectRegion(info.GeoRegion);
            TxtAirportHint.Text       = $"✓  {info.Name} ({info.StateOrCountry}) — auto-classified as {info.GeoRegion}";
            TxtAirportHint.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)); // green
        }
        else
        {
            TxtAirportHint.Text       = "⚠  Unknown code — please verify the region below";
            TxtAirportHint.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber
        }
    }

    private void SelectRegion(string region)
    {
        foreach (ComboBoxItem item in CboRegion.Items)
        {
            if (item.Content?.ToString() == region)
            {
                CboRegion.SelectedItem = item;
                return;
            }
        }
        CboRegion.SelectedIndex = 0;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        var name = TxtName.Text.Trim().ToUpper();

        if (string.IsNullOrEmpty(name))
        {
            TxtError.Text = "Cluster code is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        var region = (CboRegion.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "East";

        // Unrecognized code — require an explicit override rather than silently saving
        // whatever region happens to be selected (previously defaulted to East unnoticed).
        if (AirportLookup.Lookup(name) == null)
        {
            var proceed = MessageBox.Show(
                $"\"{name}\" is not a recognized airport code.\n\n" +
                $"It will be saved with region: {region}.\n\n" +
                "Continue anyway?",
                "Unrecognized Airport Code", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }

        // Bulk ROMP status — only present/relevant on an edit (dropdown is null for a new cluster).
        if (CboBulkRompStatus?.SelectedItem is string selectedStatus && selectedStatus != "No change")
            BulkRompStatus = selectedStatus;

        if (ChkCloseAll.IsChecked == true)
        {
            var confirm = MessageBox.Show(
                $"Close all sites at \"{name}\" and remove this cluster, its sites, and their ROMPs from their lists?\n\n" +
                "Everything stays in the database for reporting and scoring history — use \"Show Completed\" " +
                "to find them again.",
                "Confirm Close Cluster", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            // Closing wins over whatever the status dropdown had — every ROMP ends up Complete
            // either way, and CloseAllSites also marks every Site closed, which the plain status
            // dropdown alone doesn't do.
            BulkRompStatus = StatusConfig.Complete.FirstOrDefault();
            CloseAllSites  = true;
        }

        Result = new Location { Id = _existing?.Id ?? 0, Name = name, GeographicalLocation = region };
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
