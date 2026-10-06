using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class AddEditSiteDialog : Window
{
    private readonly Site? _existing;
    public Site?   Result          { get; private set; }
    public string? BulkRompStatus  { get; private set; } // null = no change

    public AddEditSiteDialog(Site? existing, List<Location> locations)
    {
        InitializeComponent();
        _existing = existing;

        // Populate bulk ROMP status dropdown: "No change" + statuses available to assign
        // (disabled statuses are excluded — this is a prospective bulk-assignment picker, not a
        // display of current state)
        var bulkItems = new List<string> { "No change" };
        bulkItems.AddRange(StatusConfig.Selectable);
        CboBulkRompStatus.ItemsSource   = bulkItems;
        CboBulkRompStatus.SelectedIndex = 0; // default: No change

        CboLocation.ItemsSource      = locations;
        CboLocation.SelectionChanged += CboLocation_SelectionChanged;

        if (existing != null)
        {
            TxtTitle.Text = "Edit Site";
            TxtName.Text  = existing.Name;
            CboLocation.SelectedItem  = locations.Find(l => l.Id == existing.LocationId);
            BulkRompSection.Visibility = Visibility.Visible; // only show for edits
            CloseSiteSection.Visibility = Visibility.Visible; // only show for edits
            ChkCloseAll.IsChecked = existing.IsClosed; // reflect current state — unchecking reopens it
            ShowCurrentStatuses(existing.Id, existing.StatusWhenEmpty);
        }
        else
        {
            CboLocation.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Shows a quick "what's here right now" breakdown next to the bulk-status picker — a campus
    /// can have ROMPs on several different statuses at once, so there's no single "current
    /// status" to pre-select the way AddEditRompDialog does for one ROMP; this gives the same
    /// visibility before you overwrite it. Ordered by StatusConfig.All so it reads consistently
    /// with every other status list in the app, not just whatever order GroupBy happens to return.
    /// </summary>
    private void ShowCurrentStatuses(int siteId, string? statusWhenEmpty)
    {
        using var db = new AppDbContext();
        var counts = db.Romps.Where(r => r.SiteId == siteId)
            .GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        if (counts.Count == 0)
        {
            // No ROMPs to summarize — but a zero-ROMP campus can still have a stand-in status
            // (Site.StatusWhenEmpty) driving its color/classification; say so if set.
            if (!string.IsNullOrEmpty(statusWhenEmpty))
            {
                TxtCurrentStatuses.Text = $"Currently: no ROMPs — acting as \"{statusWhenEmpty}\"";
                TxtCurrentStatuses.Visibility = Visibility.Visible;
            }
            return;
        }

        var parts = StatusConfig.All
            .Where(s => counts.ContainsKey(s))
            .Select(s => $"{counts[s]} {s}")
            .Concat(counts.Keys.Except(StatusConfig.All, System.StringComparer.OrdinalIgnoreCase)
                .Select(s => $"{counts[s]} {s}")); // defensive — a status removed from config but still on a ROMP

        var total = counts.Values.Sum();
        TxtCurrentStatuses.Text = $"Currently: {string.Join(", ", parts)}  ({total} ROMP{(total == 1 ? "" : "s")} total)";
        TxtCurrentStatuses.Visibility = Visibility.Visible;
    }

    private void CboLocation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboLocation.SelectedItem is Location loc && !string.IsNullOrEmpty(loc.GeographicalLocation))
            TxtRegionHint.Text = $"Region: {loc.GeographicalLocation} (inherited from cluster)";
        else
            TxtRegionHint.Text = "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (CboLocation.SelectedItem is not Location selectedLocation)
        {
            TxtError.Text = "Please select a cluster.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            TxtError.Text = "Site Name is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        bool wasClosed = _existing?.IsClosed ?? false;
        bool nowClosed = ChkCloseAll.IsChecked == true;

        if (nowClosed && !wasClosed)
        {
            // Newly closing — confirm. (Already-closed staying checked, or unchecking to
            // reopen, needs no confirmation — only the act of hiding it does.)
            var confirm = MessageBox.Show(
                $"Close all ROMPs at \"{TxtName.Text.Trim()}\" and remove this site from the list?\n\n" +
                "Everything stays in the database for reporting and scoring history — use \"Show Completed\" " +
                "on the Sites list to find it again.",
                "Confirm Close Site", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            BulkRompStatus = StatusConfig.Complete.FirstOrDefault();
        }
        else
        {
            // Read bulk ROMP status — null means no change
            var selected = CboBulkRompStatus.SelectedItem as string;
            BulkRompStatus = (selected == "No change" || string.IsNullOrEmpty(selected)) ? null : selected;
        }

        Result = new Site
        {
            Id              = _existing?.Id ?? 0,
            Name            = TxtName.Text.Trim(),
            LocationId      = selectedLocation.Id,
            AssignedPmId    = _existing?.AssignedPmId,    // preserve PM assignment — SetValues copies ALL scalars
            IsClosed        = nowClosed,
            StatusWhenEmpty = _existing?.StatusWhenEmpty, // preserve too — UpdateSite overwrites it only when BulkRompStatus is actually set
        };

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
