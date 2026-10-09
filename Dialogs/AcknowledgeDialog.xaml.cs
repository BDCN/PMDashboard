using System.Windows;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

/// <summary>
/// A warning the user must explicitly acknowledge (tick the box) before continuing — used for
/// assigning a PM outside their team and for splitting a cluster/campus across sub-regions or
/// regions. The confirm button stays disabled until the box is ticked.
/// </summary>
public partial class AcknowledgeDialog : Window
{
    public AcknowledgeDialog(string title, string heading, string message, string note, string acknowledgement, string confirmText)
    {
        InitializeComponent();
        Title           = title;
        TxtHeading.Text = heading;
        TxtMessage.Text = message;
        TxtNote.Text    = note;
        TxtNote.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
        TxtAck.Text     = acknowledgement;
        BtnConfirm.Content = confirmText;
    }

    private void ChkAck_Changed(object sender, RoutedEventArgs e) => BtnConfirm.IsEnabled = ChkAck.IsChecked == true;
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    // ── The two uses ──

    /// <summary>Asks before assigning a PM outside the team that covers the place.</summary>
    public static bool ConfirmOutsideTeam(Window? owner, string reason) =>
        new AcknowledgeDialog(
            "Assign Outside the Team",
            "This PM isn't on the team for this location",
            reason,
            "You can still make this assignment. It will be recorded in the audit log as an acknowledged exception " +
            "and listed on Teams → Conflicts so it stays visible.",
            "I understand this assignment is outside the PM's team, and I want to make it anyway.",
            "Assign Anyway") { Owner = owner }.ShowDialog() == true;

    /// <summary>Asks before splitting a cluster/campus across sub-regions or regions.</summary>
    public static bool ConfirmSplit(Window? owner, string what, string from, string to, bool crossesRegion) =>
        new AcknowledgeDialog(
            crossesRegion ? "Split Across Regions" : "Split Across Sub-regions",
            crossesRegion ? $"Move {what} into another region?" : $"Split {what} off to another sub-region?",
            $"{what} currently follows {from}. After this it will be in {to}" +
            (crossesRegion ? " — a different region and Director." : ".") +
            " Only PMs on the new team will be suggested for it, and its current assignments stay as they are " +
            "(any that are now outside their team show on Teams → Conflicts).",
            "Splits are allowed when there's a business reason for them, but they make coverage harder to follow — " +
            "the Org Chart, Teams page and Sites page show split parts so they're easy to spot.",
            $"I understand {what} will be split from its parent and managed by a different team.",
            "Split It") { Owner = owner }.ShowDialog() == true;

    /// <summary>
    /// Runs an assignment; if the team rule blocks it, shows the warning and — once acknowledged —
    /// runs it again with the out-of-team exception allowed (and audited). Every assignment throws
    /// before changing anything, so the retry starts from a clean state.
    /// </summary>
    public static bool RunAssignment(Window? owner, Action assign)
    {
        try { assign(); return true; }
        catch (TeamRuleException ex)
        {
            if (!ConfirmOutsideTeam(owner, ex.Message)) return false;
            using (TeamRules.AllowOutsideTeam()) assign();
            return true;
        }
    }
}
