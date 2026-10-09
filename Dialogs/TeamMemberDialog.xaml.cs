using System.Windows;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

/// <summary>
/// Add/edit a Director, PE or PA (<see cref="Result"/>). Only the fields for the role are shown.
/// A Platform Manager's team (sub-regions) is edited with PmSubRegionsDialog instead.
/// </summary>
public partial class TeamMemberDialog : Window
{
    // Public: WPF bindings (DisplayMemberPath) can't read members of a non-public type.
    public sealed record Choice(int? Id, string Label);

    private readonly string      _role;
    private readonly TeamMember? _existing;

    public TeamMember? Result      { get; private set; }

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
        else
        {
            ChkActive.Visibility = Visibility.Collapsed;
            _autoEmail = true;   // new person: suggest firstname.lastname@iemfg.com as the name is typed
        }
        TxtEmailHint.Visibility = _autoEmail ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => TxtFirst.Focus();
    }

    // ── Email suggestion (new people only) ──

    public const string EmailDomain = "iemfg.com";
    private bool _autoEmail;        // keep filling the email from the name
    private bool _settingEmail;     // our own update, not the user typing

    /// <summary>"Mary-Ann O'Neil" → "mary-ann.oneil@iemfg.com" (letters, digits and hyphens only).</summary>
    public static string SuggestEmail(string first, string last)
    {
        static string Part(string s) => new string(s.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        var f = Part(first); var l = Part(last);
        return f == "" && l == "" ? "" : $"{string.Join(".", new[] { f, l }.Where(x => x != ""))}@{EmailDomain}";
    }

    private void Name_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_autoEmail || TxtEmail == null) return;
        _settingEmail = true;
        TxtEmail.Text = SuggestEmail(TxtFirst.Text, TxtLast.Text);
        _settingEmail = false;
    }

    private void TxtEmail_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_settingEmail || _existing != null) return;
        // The user typed in the email: stop overwriting it — unless they cleared it, then resume.
        _autoEmail = string.IsNullOrWhiteSpace(TxtEmail.Text);
        if (_autoEmail) Name_TextChanged(sender, e);
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
