using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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

        // Populate bulk ROMP status dropdown: "No change" + all configured statuses
        var bulkItems = new List<string> { "No change" };
        bulkItems.AddRange(StatusConfig.All);
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
        }
        else
        {
            CboLocation.SelectedIndex = 0;
        }
    }

    private void CboLocation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboLocation.SelectedItem is Location loc && !string.IsNullOrEmpty(loc.GeographicalLocation))
            TxtRegionHint.Text = $"Region: {loc.GeographicalLocation} (inherited from location)";
        else
            TxtRegionHint.Text = "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (CboLocation.SelectedItem is not Location selectedLocation)
        {
            TxtError.Text = "Please select a location.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            TxtError.Text = "Site Name is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        Result = new Site
        {
            Id           = _existing?.Id ?? 0,
            Name         = TxtName.Text.Trim(),
            LocationId   = selectedLocation.Id,
            AssignedPmId = _existing?.AssignedPmId, // preserve PM assignment — SetValues copies ALL scalars
        };

        // Read bulk ROMP status — null means no change
        var selected = CboBulkRompStatus.SelectedItem as string;
        BulkRompStatus = (selected == "No change" || string.IsNullOrEmpty(selected)) ? null : selected;

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
