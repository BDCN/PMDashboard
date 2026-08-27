using System.Windows;
using System.Windows.Input;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class PasswordDialog : Window
{
    public PasswordDialog() => InitializeComponent();

    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => TryConfirm();
    private void BtnCancel_Click(object sender, RoutedEventArgs e)   => DialogResult = false;

    private void PwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryConfirm();
    }

    private void TryConfirm()
    {
        if (!string.IsNullOrEmpty(AdminConfig.Password) && PwdBox.Password == AdminConfig.Password)
        {
            DialogResult = true;
        }
        else
        {
            TxtError.Visibility = Visibility.Visible;
            PwdBox.Clear();
            PwdBox.Focus();
        }
    }
}
