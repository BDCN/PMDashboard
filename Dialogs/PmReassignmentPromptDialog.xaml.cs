using System.Windows;
using PMDashboard.ViewModels;

namespace PMDashboard.Dialogs;

public enum PmReassignmentChoice { Reassign, Unassign, Cancel }

/// <summary>
/// Shown when deactivating or deleting a PM who still holds campus assignments and/or
/// individual ROMP overrides — asks whether to move those to another PM or clear them, rather
/// than silently leaving dangling assignments (or, for delete, an orphaned reference).
/// </summary>
public partial class PmReassignmentPromptDialog : Window
{
    public PmReassignmentChoice Choice { get; private set; } = PmReassignmentChoice.Cancel;

    public PmReassignmentPromptDialog(string pmName, PmAssignmentSummary summary, bool isDeleting)
    {
        InitializeComponent();

        TxtTitle.Text = isDeleting
            ? $"Delete {pmName}?"
            : $"Deactivate {pmName}?";

        var parts = new List<string>();
        if (summary.CampusCount > 0)
            parts.Add($"{summary.CampusCount} site{(summary.CampusCount == 1 ? "" : "s")}");
        if (summary.RompOverrideCount > 0)
            parts.Add($"{summary.RompOverrideCount} individual ROMP override{(summary.RompOverrideCount == 1 ? "" : "s")}");

        TxtSummary.Text = $"{pmName} currently has {string.Join(" and ", parts)} assigned. " +
            (isDeleting
                ? "Choose what happens to those before deleting — leaving them assigned to a deleted PM isn't recoverable."
                : "Choose what happens to those, or cancel to keep this PM active.");
    }

    private void BtnReassign_Click(object sender, RoutedEventArgs e)
    {
        Choice = PmReassignmentChoice.Reassign;
        DialogResult = true;
    }

    private void BtnUnassign_Click(object sender, RoutedEventArgs e)
    {
        Choice = PmReassignmentChoice.Unassign;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = PmReassignmentChoice.Cancel;
        DialogResult = false;
    }
}
