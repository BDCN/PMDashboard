using System.Windows;
using System.Windows.Input;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class LoginDialog : Window
{
    private readonly UserService _users = new();

    public LoginDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtEmail.Focus();
    }

    private void BtnSignIn_Click(object sender, RoutedEventArgs e) => TrySignIn();
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TrySignIn();
    }

    private void TrySignIn()
    {
        var email = TxtEmail.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(PwdBox.Password))
        {
            ShowError("Enter your email address and password.");
            return;
        }

        var user = _users.Authenticate(email, PwdBox.Password);
        if (user == null)
        {
            // One message for unknown address, wrong password and disabled account alike —
            // saying which would tell an attacker whether an address exists.
            ShowError("Email address or password is incorrect.");
            AuditService.Log("LoginFailed", "User", details: $"Attempt for {email}");
            return;
        }

        CurrentUser.SignIn(user);

        // A newly created or admin-reset account cannot proceed on its temporary password.
        if (user.MustChangePassword)
        {
            var change = new ChangePasswordDialog(user, mustChange: true) { Owner = this };
            if (change.ShowDialog() != true)
            {
                CurrentUser.SignOut();
                ShowError("You must set a new password before continuing.");
                return;
            }
        }

        _users.RecordLogin(user.Id);
        AuditService.Log("Login", "User", user.Id, $"Signed in as {user.Email}");
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        TxtError.Text       = message;
        TxtError.Visibility = Visibility.Visible;
        PwdBox.Clear();
        PwdBox.Focus();
    }
}
