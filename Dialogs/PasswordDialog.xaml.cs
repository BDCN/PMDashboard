using System.Windows;
using System.Windows.Input;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public enum PasswordDialogMode
{
    /// <summary>Check an entered password against the stored hash.</summary>
    Verify,
    /// <summary>Set the first admin password on a system that has none.</summary>
    SetInitial,
    /// <summary>Replace the existing admin password (caller has already unlocked).</summary>
    Change,
}

public partial class PasswordDialog : Window
{
    private readonly PasswordDialogMode _mode;

    private bool IsSetting => _mode is PasswordDialogMode.SetInitial or PasswordDialogMode.Change;

    public PasswordDialog() : this(PasswordDialogMode.Verify) { }

    public PasswordDialog(PasswordDialogMode mode)
    {
        _mode = mode;
        InitializeComponent();
        ApplyMode();
    }

    private void ApplyMode()
    {
        switch (_mode)
        {
            case PasswordDialogMode.SetInitial:
                Title              = "Set Admin Password";
                TxtIcon.Text       = "🔑";
                TxtHeading.Text    = "Set Admin Password";
                TxtSubtitle.Text   = "No admin password has been set yet. Choose one to protect the "
                                   + "Source Data and Database settings. It is stored with your data, "
                                   + "so it applies on every machine.";
                TxtPwdLabel.Text   = "New password";
                ConfirmPanel.Visibility = Visibility.Visible;
                BtnConfirm.Content = "Set Password";
                break;

            case PasswordDialogMode.Change:
                Title              = "Change Admin Password";
                TxtIcon.Text       = "🔑";
                TxtHeading.Text    = "Change Admin Password";
                TxtSubtitle.Text   = "Enter a new admin password. It is stored with your data, so the "
                                   + "change applies on every machine once they sync.";
                TxtPwdLabel.Text   = "New password";
                ConfirmPanel.Visibility = Visibility.Visible;
                BtnConfirm.Content = "Change Password";
                break;

            default:
                Title              = "Admin Password Required";
                TxtSubtitle.Text   = "Enter the admin password to change the Source Data or Database settings.";
                ConfirmPanel.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => TryConfirm();
    private void BtnCancel_Click(object sender, RoutedEventArgs e)   => DialogResult = false;

    private void PwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryConfirm();
    }

    private void TryConfirm()
    {
        if (IsSetting) TrySetPassword();
        else           TryVerify();
    }

    private void TryVerify()
    {
        if (AdminConfig.Verify(PwdBox.Password))
        {
            DialogResult = true;
            return;
        }

        // Distinguish "wrong password" from "no password exists yet", which was
        // previously indistinguishable and made an unconfigured machine look like
        // it was rejecting a correct password.
        ShowError(AdminConfig.IsConfigured
            ? "Incorrect password — try again."
            : "No admin password has been set. Use \"Set Admin Password\" to create one.");
    }

    private void TrySetPassword()
    {
        var pwd = PwdBox.Password;

        if (string.IsNullOrWhiteSpace(pwd))
        {
            ShowError("Password cannot be empty.");
            return;
        }
        if (pwd != ConfirmBox.Password)
        {
            ShowError("Passwords do not match.");
            ConfirmBox.Clear();
            ConfirmBox.Focus();
            return;
        }

        AdminConfig.SetPassword(pwd);
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
