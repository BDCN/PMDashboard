using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class AddEditLocationDialog : Window
{
    private readonly Location? _existing;
    public Location? Result { get; private set; }

    public AddEditLocationDialog(Location? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing != null)
        {
            TxtTitle.Text = "Edit Location";
            TxtName.Text  = existing.Name;
            SelectRegion(existing.GeographicalLocation);
        }
        else
        {
            CboRegion.SelectedIndex = 0;
        }
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
            TxtError.Text = "Location code is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        var region = (CboRegion.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "East";

        // If region wasn't confirmed by lookup, prompt the user to verify
        if (AirportLookup.Lookup(name) == null &&
            string.IsNullOrEmpty((CboRegion.SelectedItem as ComboBoxItem)?.Content?.ToString()))
        {
            TxtError.Text = "Please select the geographical region (East or West).";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        Result = new Location { Id = _existing?.Id ?? 0, Name = name, GeographicalLocation = region };
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
