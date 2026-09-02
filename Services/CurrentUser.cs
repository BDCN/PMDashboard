using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Who is signed in for this run. Set by the login flow, read by AuditService and any screen
/// that gates on role. Session-only: nothing here is persisted, so closing the app signs out.
/// </summary>
public static class CurrentUser
{
    public static User? User { get; private set; }

    public static bool   IsSignedIn   => User != null;
    public static bool   IsSuperAdmin => User?.IsSuperAdmin == true;
    public static string Email        => User?.Email ?? "test";
    public static string DisplayName  => User?.DisplayName ?? "test";

    public static void SignIn(User user)  => User = user;
    public static void SignOut()          => User = null;

    /// <summary>Refreshes the cached copy after the signed-in user edits their own profile.</summary>
    public static void Refresh(User user)
    {
        if (User != null && User.Id == user.Id) User = user;
    }
}
