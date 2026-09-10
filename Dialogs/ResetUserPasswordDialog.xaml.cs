using System.Windows;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class ResetUserPasswordDialog : Window
{
    private readonly UserService _userService = new();
    private readonly int _userId;

    public ResetUserPasswordDialog(int userId, string userDisplayName)
    {
        InitializeComponent();
        _userId = userId;
        TxtSubtitle.Text = $"Set a new password for {userDisplayName}. They'll use it to sign in next time.";
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (PwdNew.Password != PwdConfirm.Password)
        {
            ShowError("Passwords do not match.");
            return;
        }

        var error = _userService.AdminResetPassword(_userId, PwdNew.Password);
        if (error != null)
        {
            ShowError(error);
            return;
        }

        AuditService.Log("ResetPassword", "User", _userId, "Admin reset a user's password");
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        TxtError.Text       = message;
        TxtError.Visibility = Visibility.Visible;
    }
}
