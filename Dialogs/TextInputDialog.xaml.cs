using System.Windows;
using System.Windows.Input;

namespace PMDashboard.Dialogs;

public partial class TextInputDialog : Window
{
    public string Value { get; private set; } = "";

    public TextInputDialog(string heading, string prompt, string initialValue = "")
    {
        InitializeComponent();
        TxtHeading.Text = heading;
        TxtPrompt.Text  = prompt;
        TxtValue.Text   = initialValue;
        Title           = heading;
        Loaded += (_, _) => { TxtValue.Focus(); TxtValue.SelectAll(); };
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => TryConfirm();
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TxtValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryConfirm();
    }

    private void TryConfirm()
    {
        if (string.IsNullOrWhiteSpace(TxtValue.Text))
        {
            TxtError.Visibility = Visibility.Visible;
            TxtValue.Focus();
            return;
        }
        Value = TxtValue.Text.Trim();
        DialogResult = true;
    }
}
