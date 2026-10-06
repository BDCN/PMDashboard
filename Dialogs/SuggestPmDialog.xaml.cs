using System.Windows;
using System.Windows.Input;
using PMDashboard.Data.Models;
using PMDashboard.ViewModels;

namespace PMDashboard.Dialogs;

public partial class SuggestPmDialog : Window
{
    public PM? SelectedPm { get; private set; }

    public SuggestPmDialog(ForecastTimelineRow forRow, List<ForecastTimelineViewModel.PmSuggestion> suggestions)
    {
        InitializeComponent();
        TxtSubject.Text = $"{forRow.CampusName} — {forRow.RoomType}  ({forRow.Region})\n" +
                           $"{forRow.StartDate:MMM d, yyyy} → {forRow.EndDate:MMM d, yyyy}";
        CandidateGrid.ItemsSource = suggestions;

        CandidateGrid.SelectionChanged += (_, _) =>
            BtnAssign.IsEnabled = CandidateGrid.SelectedItem is ForecastTimelineViewModel.PmSuggestion;
    }

    private void CandidateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CandidateGrid.SelectedItem is ForecastTimelineViewModel.PmSuggestion) Assign();
    }

    private void BtnAssign_Click(object sender, RoutedEventArgs e) => Assign();
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Assign()
    {
        if (CandidateGrid.SelectedItem is not ForecastTimelineViewModel.PmSuggestion s) return;
        SelectedPm   = s.Pm;
        DialogResult = true;
    }
}
