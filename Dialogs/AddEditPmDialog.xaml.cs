using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

public partial class AddEditPmDialog : Window
{
    private readonly PM? _existing;
    public PM? Result { get; private set; }

    public AddEditPmDialog(PM? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing != null)
        {
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
        }
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

        Result = new PM
        {
            Id                   = _existing?.Id ?? 0,
            FirstName            = TxtFirstName.Text.Trim(),
            LastName             = TxtLastName.Text.Trim(),
            HomeOffice           = (CboHomeOffice.SelectedItem  as ComboBoxItem)?.Content?.ToString() ?? "",
            GeographicalLocation = (CboGeoLocation.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "",
            DataName             = TxtDataName.Text.Trim(),
            Capacity             = capacity,
            IdealUtilization     = util,
            PmMultiplier         = multiplier,
            IsActive             = ChkActive.IsChecked == true,
        };

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
