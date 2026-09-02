using System.Windows;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class RowHistoryDialog : Window
{
    /// <summary>Display shape for one audit row — timestamps are stored UTC, shown local.</summary>
    public sealed record HistoryRow(DateTime TimestampLocal, string UserDisplay, string Action,
                                    string Details, string MachineName);

    public RowHistoryDialog(string entityType, int entityId, string rowLabel)
    {
        InitializeComponent();

        TxtHeading.Text = $"History — {rowLabel}";

        var entries = AuditService.ForEntity(entityType, entityId);
        HistoryGrid.ItemsSource = entries.Select(a => new HistoryRow(
            a.TimestampUtc.ToLocalTime(),
            string.IsNullOrWhiteSpace(a.UserName) ? a.UserEmail : $"{a.UserName} <{a.UserEmail}>",
            a.Action,
            a.Details,
            a.MachineName)).ToList();

        TxtSubtitle.Text = $"{entries.Count} recorded change(s) for {entityType} #{entityId}.";

        if (entries.Count == 0)
        {
            HistoryGrid.Visibility = Visibility.Collapsed;
            TxtEmpty.Visibility    = Visibility.Visible;
            // Be explicit rather than implying the row was never touched: auditing began when
            // this feature was added, and bulk imports are recorded as one entry for the run
            // rather than one per row.
            TxtEmpty.Text = "No history recorded for this row yet.\n\n"
                          + "Auditing records changes made from this point onward. Bulk imports "
                          + "are logged once for the whole run rather than per row, so they will "
                          + "not appear here.";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
