using System.Windows;
using System.Windows.Media;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class UserProfileDialog : Window
{
    private readonly UserService _users = new();

    public UserProfileDialog()
    {
        InitializeComponent();

        if (CurrentUser.User is not { } u) { Close(); return; }

        TxtFirstName.Text = u.FirstName;
        TxtLastName.Text  = u.LastName;
        TxtEmail.Text     = u.Email;
        TxtRole.Text      = u.IsSuperAdmin ? "Super Admin" : "User";
        TxtLastLogin.Text = u.LastLoginUtc.HasValue
            ? $"Last sign-in: {u.LastLoginUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "First sign-in";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentUser.User is not { } u) return;

        var before = $"{u.FirstName} {u.LastName} <{u.Email}>";
        var error  = _users.UpdateProfile(u.Id, TxtFirstName.Text, TxtLastName.Text, TxtEmail.Text);
        if (error != null)
        {
            ShowStatus(error, isError: true);
            return;
        }

        AuditService.Log("Update", "User", u.Id,
            $"Profile: {before} -> {TxtFirstName.Text.Trim()} {TxtLastName.Text.Trim()} <{TxtEmail.Text.Trim()}>");
        ShowStatus("Profile saved.", isError: false);
    }

    private void BtnChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentUser.User is not { } u) return;
        var dlg = new ChangePasswordDialog(u) { Owner = this };
        if (dlg.ShowDialog() == true) ShowStatus("Password changed.", isError: false);
    }

    private void ShowStatus(string message, bool isError)
    {
        TxtStatus.Text       = message;
        TxtStatus.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : Color.FromRgb(0x16, 0xA3, 0x4A));
        TxtStatus.Visibility = Visibility.Visible;
    }
}
