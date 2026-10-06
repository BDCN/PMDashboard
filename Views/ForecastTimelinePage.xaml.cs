using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.ComponentModel;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

/// <summary>Checkbox-list row for the PM compare popup — needs real change notification since
/// "All"/"None" set IsSelected programmatically and the already-rendered checkboxes must reflect
/// that, not just capture a later user click.</summary>
public partial class SelectablePm : ObservableObject
{
    public PM     Pm          { get; init; } = null!;
    public string DisplayName { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

public partial class SelectableCampus : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

public partial class ForecastTimelinePage : UserControl
{
    // Width of the campus-name column — must match the DataGrid-free row template in XAML.
    private const double NameColumnWidth = 260;

    private ForecastTimelineViewModel? Vm => DataContext as ForecastTimelineViewModel;

    private ObservableCollection<SelectablePm>     _pmChecks      = new();
    private ObservableCollection<SelectableCampus> _campusChecks  = new();

    public ForecastTimelinePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ForecastTimelineViewModel vm)
            RebuildBackground(vm);
    }

    private void ChartGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var barWidth = e.NewSize.Width - NameColumnWidth;
        if (barWidth < 50) return;

        vm.ApplyScale(barWidth);
        RebuildBackground(vm);
    }

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SelectedCluster    = "All";
        vm.SelectedRegion     = "All";
        vm.SelectedAssignment = "All";
        vm.StartDateFilter  = null;
        vm.EndDateFilter    = null;
        vm.ShowPast         = false;
        foreach (var c in _pmChecks) c.IsSelected = false;
        foreach (var c in _campusChecks) c.IsSelected = false;
        vm.SetPmFilter(new List<PM>());
        vm.SetCampusFilter(new List<string>());
    }

    // ── Sort ──
    private void BtnSortNbd_Click(object sender, RoutedEventArgs e)   => Vm?.ToggleNbdSort();
    private void BtnSortPfho_Click(object sender, RoutedEventArgs e)  => Vm?.TogglePfhoSort();
    private void BtnSortClear_Click(object sender, RoutedEventArgs e) => Vm?.ClearSort();

    // ── PM compare popup ──

    private void BtnPmFilter_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // Rebuild each time the popup opens so newly-loaded PMs show up, preserving whatever is
        // currently applied (not just whatever was checked last time the popup happened to be open).
        var currentlySelected = vm.PmOptions.Count > 0
            ? new HashSet<int>(_pmChecks.Where(c => c.IsSelected).Select(c => c.Pm.Id))
            : new HashSet<int>();
        _pmChecks = new ObservableCollection<SelectablePm>(vm.PmOptions.Select(p => new SelectablePm
        {
            Pm          = p,
            DisplayName = p.FullName,
            IsSelected  = currentlySelected.Contains(p.Id),
        }));
        PmCheckList.ItemsSource = _pmChecks;
        PmFilterPopup.IsOpen = true;
    }

    private void PmSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _pmChecks) c.IsSelected = true;
    }

    private void PmSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _pmChecks) c.IsSelected = false;
    }

    private void PmFilterApply_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SetPmFilter(_pmChecks.Where(c => c.IsSelected).Select(c => c.Pm).ToList());
        PmFilterPopup.IsOpen = false;
    }

    // ── Campus scope popup ──

    private void BtnCampusFilter_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var currentlySelected = new HashSet<string>(
            _campusChecks.Where(c => c.IsSelected).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        _campusChecks = new ObservableCollection<SelectableCampus>(vm.CampusOptions.Select(name => new SelectableCampus
        {
            Name       = name,
            IsSelected = currentlySelected.Contains(name),
        }));
        CampusCheckList.ItemsSource = _campusChecks;
        CampusFilterPopup.IsOpen = true;
    }

    private void CampusSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _campusChecks) c.IsSelected = true;
    }

    private void CampusSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _campusChecks) c.IsSelected = false;
    }

    private void CampusFilterApply_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SetCampusFilter(_campusChecks.Where(c => c.IsSelected).Select(c => c.Name).ToList());
        CampusFilterPopup.IsOpen = false;
    }

    private void BtnAddCampuses_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var campuses = vm.GetUnmatchedCampuses();
        if (campuses.Count == 0)
        {
            MessageBox.Show("No unmatched sites found.", "Add Sites",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new AddForecastCampusesDialog(vm, campuses) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        vm.Load();
        RebuildBackground(vm);
        MessageBox.Show($"Added {dlg.AddedCount} site(s).", "Add Sites",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSuggestPm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not ForecastTimelineRow row) return;

        var suggestions = vm.SuggestPms(row);
        if (suggestions.Count == 0)
        {
            MessageBox.Show("No active PMs to suggest.", "Suggest a PM",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SuggestPmDialog(row, suggestions) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.SelectedPm is not { } pm) return;

        vm.AssignPmToForecastRow(row, pm.Id);
        RebuildBackground(vm);
    }

    private void RebuildBackground(ForecastTimelineViewModel vm)
    {
        BackgroundCanvas.Children.Clear();

        var minorBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        var majorBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));

        foreach (var tick in vm.Ticks)
        {
            var line = new Rectangle
            {
                Width            = tick.IsMajor ? 1.5 : 1,
                Height           = 10_000,
                Fill             = tick.IsMajor ? majorBrush : minorBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(line, tick.X);
            BackgroundCanvas.Children.Add(line);
        }
    }
}
