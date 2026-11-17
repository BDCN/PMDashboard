using System.Windows;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

/// <summary>
/// Shown only on first run, when the Users table is empty — the first real person sets their
/// own email and password directly, so no placeholder credential ever exists in source.
/// </summary>
public partial class CreateAdminAccountDialog : Window
{
    private readonly UserService _users = new();

    public CreateAdminAccountDialog()
    {
        InitializeComponent();
        if (NetworkDb.IsShared) JoinTeamPanel.Visibility = Visibility.Collapsed;
    }

    // A new SAT is starting on the chosen database — closing this dialog ends this one.
    private void BtnConnectDb_Click(object sender, RoutedEventArgs e)
    {
        if (DatabaseConnectFlow.ConnectToNetwork(this)) DialogResult = false;
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(TxtFirstName.Text) || string.IsNullOrWhiteSpace(TxtLastName.Text))
        {
            ShowError("First name and last name are required.");
            return;
        }
        if (PwdNew.Password != PwdConfirm.Password)
        {
            ShowError("Passwords do not match.");
            return;
        }

        var error = _users.CreateUser(
            TxtEmail.Text, TxtFirstName.Text, TxtLastName.Text, "SuperAdmin",
            PwdNew.Password, requireChangeOnFirstLogin: false);

        if (error != null)
        {
            ShowError(error);
            return;
        }

        AuditService.Log("Create", "User", details: $"First-run admin account created: {TxtEmail.Text.Trim()}");
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        TxtError.Text       = message;
        TxtError.Visibility = Visibility.Visible;
    }
}
