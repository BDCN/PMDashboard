using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class LoginDialog : Window
{
    private readonly UserService _users = new();

    public LoginDialog()
    {
        InitializeComponent();
        TxtVersion.Text = AppVersion.Short;
        TxtVersion.ToolTip = AppVersion.Full;
        TxtDatabase.Text = "Data: " + DatabaseConnectFlow.Describe();
        if (NetworkDb.IsShared) TxtDatabase.ToolTip = DatabaseConfig.NetworkDbPath;
        BtnConnectDb.Content = NetworkDb.IsShared ? "Change network database…" : "Connect to network database…";
        BtnConnectDb.Visibility = DatabaseConfig.IsTravelMode ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) =>
        {
            // Pre-fill from a saved-but-currently-inert auto-login (e.g. it's enabled but the
            // account now has MustChangePassword set, so App.xaml.cs fell back to showing this
            // dialog) — saves re-typing the email, and keeps the checkbox reflecting reality.
            if (DevAutoLogin.TryGetSavedEmail() is { } savedEmail)
            {
                TxtEmail.Text = savedEmail;
                ChkSkipLogin.IsChecked = true;
                PwdBox.Focus();
            }
            else
            {
                TxtEmail.Focus();
            }
        };
    }

    private void BtnSignIn_Click(object sender, RoutedEventArgs e) => TrySignIn();
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // A new SAT is starting on the chosen database — closing this dialog unsigned-in ends this one.
    private void BtnConnectDb_Click(object sender, RoutedEventArgs e)
    {
        if (DatabaseConnectFlow.ConnectToNetwork(this)) DialogResult = false;
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TrySignIn();
    }

    private async void TrySignIn()
    {
        var email = TxtEmail.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(PwdBox.Password))
        {
            ShowError("Enter your email address and password.");
            return;
        }

        var user = _users.Authenticate(email, PwdBox.Password);

        // A user created on another machine may not have reached this machine's local cache yet
        // if cloud sync is still doing its initial catch-up — give it one bounded chance to
        // finish and retry, rather than an immediate "incorrect" that's really just "not synced
        // here yet". Skipped entirely once InitialPullDone, so this never slows down the common
        // case of a genuinely wrong password.
        if (user == null && CloudSyncService.IsAvailable && !App.CloudSync.InitialPullDone)
        {
            SetChecking(true);
            await App.CloudSync.WaitForInitialPullAsync(TimeSpan.FromSeconds(10));
            SetChecking(false);
            user = _users.Authenticate(email, PwdBox.Password);
        }

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
        AuditService.Log("Login", "User", user.Id, $"Signed in as {user.Email} — SAT {AppVersion.Short}");

        if (ChkSkipLogin.IsChecked == true)
            DevAutoLogin.Enable(user.Email);
        else if (DevAutoLogin.IsEnabled)
            DevAutoLogin.Disable(); // user explicitly logged in through the form — honor an unchecked box

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        TxtError.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        TxtError.Text        = message;
        TxtError.Visibility  = Visibility.Visible;
        PwdBox.Clear();
        PwdBox.Focus();
    }

    private void SetChecking(bool checking)
    {
        BtnSignIn.IsEnabled = !checking;
        TxtEmail.IsEnabled  = !checking;
        PwdBox.IsEnabled    = !checking;
        if (checking)
        {
            TxtError.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            TxtError.Text        = "Checking for recent account updates…";
            TxtError.Visibility  = Visibility.Visible;
        }
        else
        {
            TxtError.Visibility = Visibility.Collapsed;
        }
    }
}
