using System.Windows;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

/// <summary>Add or rename a sub-region (e.g. "East 1" in East). Names must be unique.</summary>
public partial class SubRegionDialog : Window
{
    private readonly SubRegion?           _existing;
    private readonly IReadOnlyList<string> _takenNames;

    public SubRegion? Result { get; private set; }

    public SubRegionDialog(SubRegion? existing, IReadOnlyList<string> regions, IReadOnlyList<string> takenNames, string? defaultRegion = null)
    {
        InitializeComponent();
        _existing   = existing;
        _takenNames = takenNames;
        TxtTitle.Text = existing == null ? "Add Sub-region" : "Edit Sub-region";
        Title = TxtTitle.Text;
        CboRegion.ItemsSource = regions;
        CboRegion.Text = existing?.Region ?? defaultRegion ?? regions.FirstOrDefault() ?? "";
        TxtName.Text = existing?.Name ?? "";
        Loaded += (_, _) => TxtName.Focus();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var name   = TxtName.Text.Trim();
        var region = CboRegion.Text.Trim();
        string? error =
            name == ""   ? "Enter a name." :
            region == "" ? "Choose or type a region." :
            _takenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(n, _existing?.Name, StringComparison.OrdinalIgnoreCase))
                         ? $"There's already a sub-region called {name}." : null;
        if (error != null)
        {
            TxtError.Text = error;
            TxtError.Visibility = Visibility.Visible;
            return;
        }
        Result = new SubRegion { Id = _existing?.Id ?? 0, Name = name, Region = region, SortOrder = _existing?.SortOrder ?? 0 };
        DialogResult = true;
    }
}
