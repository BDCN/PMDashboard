using System.Windows;

namespace PMDashboard.Dialogs;

public partial class AddEditRuleDialog : Window
{
    public string RuleName        { get; private set; } = "";
    public string RuleDescription { get; private set; } = "";
    public int    RuleWeight      { get; private set; } = 1;

    public AddEditRuleDialog() => InitializeComponent();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            TxtError.Text = "Rule name is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        if (!int.TryParse(TxtWeight.Text, out var w) || w < 1)
        {
            TxtError.Text = "Weight must be a whole number ≥ 1.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        RuleName        = TxtName.Text.Trim();
        RuleDescription = TxtDescription.Text.Trim();
        RuleWeight      = w;
        DialogResult    = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
