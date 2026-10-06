using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class UserManagementPage : UserControl
{
    private UserManagementViewModel? Vm => DataContext as UserManagementViewModel;

    public UserManagementPage()
    {
        InitializeComponent();
    }

    private void BtnAddUser_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditUserDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.LoadUsers();
    }

    private void BtnResetUserPassword_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not FrameworkElement { DataContext: User user }) return;

        // A Director manages accounts day-to-day, but cannot touch a Super Admin's credentials —
        // that would let a lesser role lock out or hijack the top of the access chain.
        if (CurrentUser.IsDirector && user.IsSuperAdmin)
        {
            MessageBox.Show("Directors cannot reset a Super Admin's password.",
                "Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new ResetUserPasswordDialog(user.Id, user.DisplayName) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void BtnToggleUserActive_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not FrameworkElement { DataContext: User user }) return;

        if (CurrentUser.IsDirector && user.IsSuperAdmin)
        {
            MessageBox.Show("Directors cannot activate or deactivate a Super Admin account.",
                "Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (user.IsActive)
        {
            var confirm = MessageBox.Show(
                $"Deactivate {user.DisplayName}? They will no longer be able to sign in.",
                "Deactivate User", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        vm.SetUserActive(user, !user.IsActive);
    }
}
