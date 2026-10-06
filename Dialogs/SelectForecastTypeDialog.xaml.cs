using System.Windows;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

public partial class SelectForecastTypeDialog : Window
{
    public ForecastType? Selected { get; private set; }

    public SelectForecastTypeDialog(List<ForecastType> types, ForecastType? bestGuess, double bestScore, string fileName)
    {
        InitializeComponent();

        TxtMessage.Text = bestGuess != null && bestScore >= 0.6
            ? $"\"{fileName}\" looks like {bestGuess.Name} ({bestScore:P0} of its known columns matched). Confirm or pick a different type."
            : $"Couldn't confidently identify the type for \"{fileName}\". Please pick one.";

        CmbType.ItemsSource = types;
        CmbType.SelectedItem = bestGuess ?? types.FirstOrDefault();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Selected = CmbType.SelectedItem as ForecastType;
        if (Selected == null) return;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
