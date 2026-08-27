using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

/// <summary>VM row for equipment-type checkbox list.</summary>
public class EquipmentTypeCheckbox
{
    public int    CategoryId   { get; init; }
    public string DisplayName  { get; init; } = "";
    public double Weight       { get; init; }
    public bool   IsSelected   { get; set; }
}

public partial class AddEditRompDialog : Window
{
    private readonly Romp? _existing;

    /// <summary>Saved ROMP result — set on confirmed save.</summary>
    public Romp? Result { get; private set; }

    /// <summary>Selected equipment category IDs after save.</summary>
    public IReadOnlyList<int> SelectedEquipmentCategoryIds { get; private set; } = Array.Empty<int>();

    public AddEditRompDialog(Romp? existing, List<Site> sites)
    {
        InitializeComponent();
        _existing = existing;

        // Status dropdown — dynamic from StatusConfig
        CboStatus.ItemsSource = StatusConfig.All.ToList();

        // Sites dropdown
        CboSite.ItemsSource = sites;
        CboSite.SelectionChanged += CboSite_SelectionChanged;

        // Equipment checkboxes — non-excluded categories only
        LoadEquipmentCheckboxes(existing);

        if (existing != null)
        {
            TxtTitle.Text         = "Edit ROMP";
            TxtName.Text          = existing.Name;
            CboSite.SelectedItem  = sites.FirstOrDefault(s => s.Id == existing.SiteId);
            CboStatus.SelectedItem = existing.Status;
        }
        else
        {
            CboSite.SelectedIndex   = 0;
            CboStatus.SelectedIndex = 0;
        }
    }

    private void LoadEquipmentCheckboxes(Romp? existing)
    {
        // Build set of already-assigned category IDs for pre-checking
        HashSet<int> assigned = new();
        if (existing != null)
        {
            using var db = new AppDbContext();
            assigned = db.RompEquipmentTypes
                         .Where(rt => rt.RompId == existing.Id)
                         .Select(rt => rt.EquipmentCategoryId)
                         .ToHashSet();
        }

        using var db2 = new AppDbContext();
        var categories = db2.EquipmentCategories
                            .AsNoTracking()
                            .Where(c => !c.IsExcluded)
                            .OrderBy(c => c.SortOrder)
                            .ToList();

        EquipmentList.ItemsSource = categories.Select(c => new EquipmentTypeCheckbox
        {
            CategoryId  = c.Id,
            DisplayName = $"{c.Name}  (×{c.Weight:F1})",
            Weight      = c.Weight,
            IsSelected  = assigned.Contains(c.Id),
        }).ToList();
    }

    private void CboSite_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboSite.SelectedItem is Site site && site.Location != null)
            TxtRegionHint.Text = $"Region: {site.Location.GeographicalLocation} (inherited from location)";
        else
            TxtRegionHint.Text = "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            TxtError.Text = "ROMP Name is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        if (CboSite.SelectedItem is not Site selectedSite)
        {
            TxtError.Text = "Please select a parent site.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        var status = CboStatus.SelectedItem as string
                     ?? StatusConfig.All.FirstOrDefault()
                     ?? "Ordered";

        Result = new Romp
        {
            Id     = _existing?.Id ?? 0,
            Name   = TxtName.Text.Trim(),
            SiteId = selectedSite.Id,
            Status = status,
        };

        SelectedEquipmentCategoryIds = (EquipmentList.ItemsSource as IEnumerable<EquipmentTypeCheckbox> ?? [])
            .Where(c => c.IsSelected)
            .Select(c => c.CategoryId)
            .ToList();

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
