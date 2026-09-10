using System.Windows;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class ChangePasswordDialog : Window
{
    private readonly UserService _users = new();
    private readonly User _user;
    private readonly bool _mustChange;

    public ChangePasswordDialog(User user, bool mustChange = false)
    {
        InitializeComponent();
        _user       = user;
        _mustChange = mustChange;

        if (mustChange)
        {
            TxtHeading.Text  = "Set a New Password";
            TxtSubtitle.Text = "This account is on a temporary password and cannot be used " +
                               "until you choose a new one. Minimum 8 characters.";
            BtnCancel.Content = "Sign Out";
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (PwdNew.Password != PwdConfirm.Password)
        {
            ShowError("New passwords do not match.");
            return;
        }

        var error = _users.ChangePassword(_user.Id, PwdCurrent.Password, PwdNew.Password);
        if (error != null)
        {
            ShowError(error);
            return;
        }

        AuditService.Log("ChangePassword", "User", _user.Id,
            _mustChange ? "Temporary password replaced" : "Password changed");
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        TxtError.Text       = message;
        TxtError.Visibility = Visibility.Visible;
        PwdNew.Clear();
        PwdConfirm.Clear();
        PwdNew.Focus();
    }
}
