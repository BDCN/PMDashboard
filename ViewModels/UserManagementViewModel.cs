using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

/// <summary>
/// Its own page (moved out of Configuration) so a Director can reach it directly without also
/// getting access to the rest of Configuration, which stays Super Admin only.
/// </summary>
public partial class UserManagementViewModel : ObservableObject
{
    private readonly UserService _userService = new();

    [ObservableProperty] private ObservableCollection<User> _users = new();

    /// <summary>Gates this whole page — Super Admin or Director, same as the old Manage Users card.</summary>
    public bool CanManageUsers => CurrentUser.CanManageUsers;

    public void Load() => LoadUsers();

    public void LoadUsers() => Users = new ObservableCollection<User>(_userService.AllUsers());

    public void SetUserActive(User user, bool active)
    {
        _userService.SetActive(user.Id, active);
        AuditService.Log(active ? "Activate" : "Deactivate", "User", user.Id, $"{(active ? "Activated" : "Deactivated")} {user.Email}");
        LoadUsers();
    }
}
