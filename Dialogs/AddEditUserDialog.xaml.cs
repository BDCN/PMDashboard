using System.Windows;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

public partial class AddEditUserDialog : Window
{
    private readonly UserService _userService = new();

    public AddEditUserDialog()
    {
        InitializeComponent();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var role = (CmbRole.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "User";

        var error = _userService.CreateUser(
            TxtEmail.Text, TxtFirstName.Text, TxtLastName.Text, role, PwdInitial.Password);

        if (error != null)
        {
            TxtError.Text       = error;
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        AuditService.Log("Create", "User", details: $"Created user {TxtEmail.Text.Trim()} ({role})");
        DialogResult = true;
    }
}
