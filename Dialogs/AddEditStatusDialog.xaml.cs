using System.Windows;

namespace PMDashboard.Dialogs;

public partial class AddEditStatusDialog : Window
{
    public string StatusName      { get; private set; } = "";
    public bool   IsComplete      { get; private set; }
    public bool   CountsAsActive  { get; private set; }

    public AddEditStatusDialog() => InitializeComponent();

    private void ChkIsComplete_Checked(object sender, RoutedEventArgs e)
    {
        // A "complete" status logically shouldn't also count as active
        ChkCountsAsActive.IsChecked = false;
        ChkCountsAsActive.IsEnabled = false;
    }

    private void ChkIsComplete_Unchecked(object sender, RoutedEventArgs e)
    {
        ChkCountsAsActive.IsEnabled = true;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        var name = TxtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TxtError.Text = "Status name is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }
        StatusName     = name;
        IsComplete     = ChkIsComplete.IsChecked == true;
        CountsAsActive = ChkCountsAsActive.IsChecked == true;
        DialogResult   = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
