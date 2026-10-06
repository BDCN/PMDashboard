using System.Windows;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

/// <summary>
/// Add/edit a Director, PE or PA (<see cref="Result"/>), or — in PM mode — choose which PE a
/// Platform Manager reports to (<see cref="SelectedPeId"/>; the PM's own details live in PM
/// Maintenance). Only the fields for the role are shown.
/// </summary>
public partial class TeamMemberDialog : Window
{
    // Public: WPF bindings (DisplayMemberPath) can't read members of a non-public type.
    public sealed record Choice(int? Id, string Label);

    private readonly string      _role;
    private readonly TeamMember? _existing;

    public TeamMember? Result      { get; private set; }
    public int?        SelectedPeId { get; private set; }

    /// <summary>Director / PE / PA.</summary>
    public TeamMemberDialog(string role, TeamMember? existing, IReadOnlyList<string> regions,
                            IReadOnlyList<SubRegion> subRegions, IReadOnlyList<PM> pms)
    {
        InitializeComponent();
        _role = role;
        _existing = existing;
        var roleName = RoleName(role);
        TxtTitle.Text = (existing == null ? "Add " : "Edit ") + roleName;
        Title = TxtTitle.Text;

        switch (role)
        {
            case TeamRoles.Director:
                TxtHint.Text = "A Director controls a region. Type a new region name to add one.";
                PanelRegion.Visibility = Visibility.Visible;
                CboRegion.ItemsSource = regions;
                CboRegion.Text = existing?.Region ?? "";
                break;
            case TeamRoles.Pe:
                TxtHint.Text = "A PE controls a sub-region. PMs who report to them can only be assigned to clusters in it.";
                PanelSubRegion.Visibility = Visibility.Visible;
                var subs = new[] { new Choice(null, "— none yet —") }
                    .Concat(subRegions.Select(s => new Choice(s.Id, $"{s.Name}  ({s.Region})"))).ToList();
                CboSubRegion.ItemsSource = subs;
                CboSubRegion.SelectedItem = subs.FirstOrDefault(c => c.Id == existing?.SubRegionId) ?? subs[0];
                break;
            case TeamRoles.Pa:
                TxtHint.Text = "A PA supports one Platform Manager.";
                PanelPm.Visibility = Visibility.Visible;
                var pmChoices = new[] { new Choice(null, "— none yet —") }
                    .Concat(pms.Select(p => new Choice(p.Id, p.FullName))).ToList();
                CboPm.ItemsSource = pmChoices;
                CboPm.SelectedItem = pmChoices.FirstOrDefault(c => c.Id == existing?.PmId) ?? pmChoices[0];
                break;
        }

        if (existing != null)
        {
            TxtFirst.Text = existing.FirstName;
            TxtLast.Text  = existing.LastName;
            TxtEmail.Text = existing.Email;
            TxtPhone.Text = existing.Phone;
            ChkActive.IsChecked = existing.IsActive;
        }
        else ChkActive.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => TxtFirst.Focus();
    }

    /// <summary>PM mode: pick the PE a Platform Manager reports to.</summary>
    public TeamMemberDialog(PM pm, IReadOnlyList<TeamMember> pes)
    {
        InitializeComponent();
        _role = TeamRoles.Pm;
        TxtTitle.Text = $"Team for {pm.FullName}";
        Title = TxtTitle.Text;
        TxtHint.Text = "Platform Manager — name and capacity are edited in PM Maintenance.";
        PanelPerson.Visibility = Visibility.Collapsed;
        ChkActive.Visibility   = Visibility.Collapsed;
        PanelPe.Visibility     = Visibility.Visible;
        var choices = new[] { new Choice(null, "— no team —") }
            .Concat(pes.Select(p => new Choice(p.Id, p.SubRegion != null ? $"{p.FullName}  ({p.SubRegion.Name})" : $"{p.FullName}  (no sub-region)")))
            .ToList();
        CboPe.ItemsSource = choices;
        CboPe.SelectedItem = choices.FirstOrDefault(c => c.Id == pm.PeId) ?? choices[0];
    }

    public static string RoleName(string role) => role switch
    {
        TeamRoles.Director => "Director",
        TeamRoles.Pe       => "Project Executive (PE)",
        TeamRoles.Pm       => "Platform Manager (PM)",
        TeamRoles.Pa       => "Project Administrator (PA)",
        _                  => role,
    };

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_role == TeamRoles.Pm)
        {
            SelectedPeId = (CboPe.SelectedItem as Choice)?.Id;
            DialogResult = true;
            return;
        }

        string? error = null;
        if (string.IsNullOrWhiteSpace(TxtFirst.Text) && string.IsNullOrWhiteSpace(TxtLast.Text))
            error = "Enter a name.";
        else if (_role == TeamRoles.Director && string.IsNullOrWhiteSpace(CboRegion.Text))
            error = "Choose or type the region this Director controls.";
        if (error != null)
        {
            TxtError.Text = error;
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        Result = new TeamMember
        {
            Id          = _existing?.Id ?? 0,
            Role        = _role,
            FirstName   = TxtFirst.Text.Trim(),
            LastName    = TxtLast.Text.Trim(),
            Email       = TxtEmail.Text.Trim(),
            Phone       = TxtPhone.Text.Trim(),
            Region      = _role == TeamRoles.Director ? CboRegion.Text.Trim() : "",
            SubRegionId = _role == TeamRoles.Pe ? (CboSubRegion.SelectedItem as Choice)?.Id : null,
            PmId        = _role == TeamRoles.Pa ? (CboPm.SelectedItem as Choice)?.Id : null,
            IsActive    = _existing == null || ChkActive.IsChecked == true,
        };
        DialogResult = true;
    }
}
