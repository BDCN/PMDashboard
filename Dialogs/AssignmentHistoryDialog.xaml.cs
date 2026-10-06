using System.Windows;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;

namespace PMDashboard.Dialogs;

public partial class AssignmentHistoryDialog : Window
{
    public sealed record HistoryRow(
        string Subject, DateTime AssignedLocal, DateTime? UnassignedLocal,
        string AssignedBy, string? UnassignedBy, bool IsActive)
    {
        public string UnassignedText => UnassignedLocal is { } d ? d.ToString("yyyy-MM-dd HH:mm") : "Current";
    }

    private AssignmentHistoryDialog(string heading, string subjectHeader, string subtitle, List<HistoryRow> rows)
    {
        InitializeComponent();

        TxtHeading.Text  = heading;
        TxtSubtitle.Text = subtitle;
        ColSubject.Header = subjectHeader;
        HistoryGrid.ItemsSource = rows;

        if (rows.Count == 0)
        {
            HistoryGrid.Visibility = Visibility.Collapsed;
            TxtEmpty.Visibility    = Visibility.Visible;
            TxtEmpty.Text = "No assignment history recorded yet.\n\n"
                          + "History is recorded going forward from when this feature was added — "
                          + "assignments made before that point won't appear here.";
        }
    }

    /// <summary>All sites this PM has ever been assigned to, most recent first.</summary>
    public static AssignmentHistoryDialog ForPm(int pmId, string pmName)
    {
        using var db = new AppDbContext();
        var rows = db.AssignmentHistories.AsNoTracking()
            .Include(h => h.Site)
            .Where(h => h.PmId == pmId)
            .OrderByDescending(h => h.AssignedAtUtc)
            .ToList()
            .Select(h => new HistoryRow(
                h.Site.Name, h.AssignedAtUtc.ToLocalTime(),
                h.UnassignedAtUtc?.ToLocalTime(), h.AssignedBy, h.UnassignedBy, h.IsActive))
            .ToList();

        return new AssignmentHistoryDialog(
            $"Assignment History — {pmName}", "Site",
            $"{rows.Count} assignment record(s) for {pmName}.", rows);
    }

    /// <summary>Every PM this site has ever been assigned to, most recent first.</summary>
    public static AssignmentHistoryDialog ForSite(int siteId, string siteName)
    {
        using var db = new AppDbContext();
        var rows = db.AssignmentHistories.AsNoTracking()
            .Include(h => h.Pm)
            .Where(h => h.SiteId == siteId)
            .OrderByDescending(h => h.AssignedAtUtc)
            .ToList()
            .Select(h => new HistoryRow(
                h.Pm.FullName, h.AssignedAtUtc.ToLocalTime(),
                h.UnassignedAtUtc?.ToLocalTime(), h.AssignedBy, h.UnassignedBy, h.IsActive))
            .ToList();

        return new AssignmentHistoryDialog(
            $"Assignment History — {siteName}", "PM",
            $"{rows.Count} assignment record(s) for {siteName}.", rows);
    }

    /// <summary>Every PM an individual ROMP has ever been assigned to (overrides only — this
    /// is RompId-scoped, so it does NOT include its Campus's own assignment history).</summary>
    public static AssignmentHistoryDialog ForRomp(int rompId, string rompName)
    {
        using var db = new AppDbContext();
        var rows = db.AssignmentHistories.AsNoTracking()
            .Include(h => h.Pm)
            .Where(h => h.RompId == rompId)
            .OrderByDescending(h => h.AssignedAtUtc)
            .ToList()
            .Select(h => new HistoryRow(
                h.Pm.FullName, h.AssignedAtUtc.ToLocalTime(),
                h.UnassignedAtUtc?.ToLocalTime(), h.AssignedBy, h.UnassignedBy, h.IsActive))
            .ToList();

        return new AssignmentHistoryDialog(
            $"Assignment History — {rompName}", "PM",
            $"{rows.Count} override assignment record(s) for {rompName}.", rows);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
