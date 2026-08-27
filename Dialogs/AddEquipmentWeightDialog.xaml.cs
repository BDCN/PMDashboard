using System.Windows;

namespace PMDashboard.Dialogs;

public partial class AddEquipmentWeightDialog : Window
{
    public string EwName      { get; private set; } = "";
    public string EwKeyword   { get; private set; } = "";
    public int    EwWeight    { get; private set; }
    public bool   EwExcluded  { get; private set; }

    public AddEquipmentWeightDialog() => InitializeComponent();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        var name    = TxtName.Text.Trim();
        var keyword = TxtKeyword.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            TxtError.Text = "Display name is required."; TxtError.Visibility = Visibility.Visible; return;
        }
        if (string.IsNullOrWhiteSpace(keyword))
        {
            TxtError.Text = "Keyword is required."; TxtError.Visibility = Visibility.Visible; return;
        }
        if (!int.TryParse(TxtWeight.Text, out int weight) || weight < 0)
        {
            TxtError.Text = "Weight must be a non-negative integer."; TxtError.Visibility = Visibility.Visible; return;
        }

        EwName    = name;
        EwKeyword = keyword;
        EwWeight  = weight;
        EwExcluded = ChkIsExcluded.IsChecked == true;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
