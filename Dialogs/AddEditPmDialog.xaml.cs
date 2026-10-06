using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class AddEditPmDialog : Window
{
    private readonly PM?          _existing;
    private readonly List<PM>     _existingPms;
    public PM? Result { get; private set; }

    // Data Name auto-fills from First/Last Name (new PMs only) as "first.last", lowercase, no
    // spaces — but only until the user actually edits it themselves; after that their value
    // wins and typing more of the name no longer overwrites it. _settingDataNameProgrammatically
    // distinguishes our own writes to TxtDataName from a real user edit in its TextChanged handler.
    private bool _dataNameManuallyEdited;
    private bool _settingDataNameProgrammatically;

    /// <summary>
    /// existingPms is used only for the duplicate-name/DataName warning on save; pass an empty
    /// list (or omit) if that check isn't needed (e.g. a caller that doesn't have the list handy).
    /// </summary>
    public AddEditPmDialog(PM? existing, IEnumerable<PM>? existingPms = null)
    {
        InitializeComponent();
        _existing    = existing;
        _existingPms = existingPms?.ToList() ?? new List<PM>();

        if (existing != null)
        {
            // Editing an existing PM — never auto-fill over a Data Name that's already set
            // (and already in use as the Orders/report join key).
            _dataNameManuallyEdited = true;

            TxtTitle.Text          = "Edit PM";
            TxtFirstName.Text      = existing.FirstName;
            TxtLastName.Text       = existing.LastName;
            TxtDataName.Text       = existing.DataName;
            TxtCapacity.Text       = existing.Capacity.ToString("G");
            TxtIdealUtil.Text      = existing.IdealUtilization.ToString("G");
            TxtPmMultiplier.Text   = existing.PmMultiplier?.ToString() ?? "";
            ChkActive.IsChecked    = existing.IsActive;

            SelectComboItem(CboHomeOffice,  existing.HomeOffice);
            SelectComboItem(CboGeoLocation, existing.GeographicalLocation);
        }
        else
        {
            CboHomeOffice.SelectedIndex  = 0;
            CboGeoLocation.SelectedIndex = 0;
            ChkActive.IsChecked = true; // new PMs are active by default
            TxtPmMultiplier.Text = "10"; // default full capacity

            // Adding a PM shouldn't require stopping to guess numbers: 0 capacity reads as
            // "No Capacity Set" everywhere else in the app (Workload's own KPI card for it),
            // and WorkloadViewModel already treats IdealUtilization <= 0 as 80% implicitly —
            // defaulting to the same value here just makes that visible instead of hidden.
            TxtCapacity.Text  = "0";
            TxtIdealUtil.Text = "80";
        }

        // Site Count Only mode: capacity/score fields are neither shown nor used anywhere in
        // the app, so hiding them here avoids asking for numbers nobody will ever look at.
        // The fields still carry their (default or existing) values under the hood and save
        // unchanged — only their controls are hidden, so nothing else needs to change below.
        if (new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Count")
        {
            CapacityRow.Visibility       = Visibility.Collapsed;
            PmMultiplierPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_dataNameManuallyEdited) return;

        var first = TxtFirstName.Text.Trim();
        var last  = TxtLastName.Text.Trim();
        if (first.Length == 0 && last.Length == 0) return;

        _settingDataNameProgrammatically = true;
        TxtDataName.Text = $"{first}.{last}".Trim('.').ToLowerInvariant().Replace(" ", "");
        _settingDataNameProgrammatically = false;
    }

    private void TxtDataName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingDataNameProgrammatically) return;
        _dataNameManuallyEdited = true; // a real user edit — stop overwriting it from First/Last Name
    }

    private static void SelectComboItem(ComboBox cbo, string value)
    {
        foreach (ComboBoxItem item in cbo.Items)
        {
            if (item.Content?.ToString() == value)
            {
                cbo.SelectedItem = item;
                return;
            }
        }
        cbo.SelectedIndex = 0;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(TxtFirstName.Text) || string.IsNullOrWhiteSpace(TxtLastName.Text))
        {
            TxtError.Text = "First Name and Last Name are required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        if (!double.TryParse(TxtCapacity.Text, out var capacity))
        {
            TxtError.Text = "Capacity must be a valid number.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        if (!double.TryParse(TxtIdealUtil.Text, out var util))
        {
            TxtError.Text = "Ideal Utilization must be a valid number.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        int? multiplier = null;
        var multText = TxtPmMultiplier.Text.Trim();
        if (!string.IsNullOrEmpty(multText))
        {
            if (!int.TryParse(multText, out var m) || m < 1 || m > 10)
            {
                TxtError.Text = "PM Multiplier must be a whole number between 1 and 10, or blank.";
                TxtError.Visibility = Visibility.Visible;
                return;
            }
            multiplier = m;
        }

        var firstName = TxtFirstName.Text.Trim();
        var lastName  = TxtLastName.Text.Trim();
        var dataName  = TxtDataName.Text.Trim();

        // DataName is the join key back to Orders (ProjectManagerDataName) — a typo'd or
        // reused value silently breaks that PM's scoring/reporting with no other warning,
        // so flag it here rather than let it fail invisibly later.
        var dupe = _existingPms.FirstOrDefault(p =>
            p.Id != (_existing?.Id ?? -1) &&
            ((string.Equals(p.FirstName, firstName, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(p.LastName,  lastName,  StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrEmpty(dataName) &&
              string.Equals(p.DataName, dataName, StringComparison.OrdinalIgnoreCase))));

        if (dupe != null)
        {
            var reason = string.Equals(dupe.DataName, dataName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(dataName)
                ? $"Data Name \"{dataName}\""
                : $"the name \"{firstName} {lastName}\"";
            var confirm = MessageBox.Show(
                $"{dupe.FullName} already uses {reason}.\n\n" +
                "Saving anyway will create a second PM record — this can cause Order/Report " +
                "matching to split between the two. Continue?",
                "Possible Duplicate PM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        Result = new PM
        {
            Id                   = _existing?.Id ?? 0,
            FirstName            = firstName,
            LastName             = lastName,
            HomeOffice           = (CboHomeOffice.SelectedItem  as ComboBoxItem)?.Content?.ToString() ?? "",
            GeographicalLocation = (CboGeoLocation.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "",
            DataName             = dataName,
            Capacity             = capacity,
            IdealUtilization     = util,
            PmMultiplier         = multiplier,
            IsActive             = ChkActive.IsChecked == true,
        };

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
