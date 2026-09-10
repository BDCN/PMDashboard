using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>Account lookup, authentication, profile maintenance, and admin user management.</summary>
public class UserService
{
    public User? FindByEmail(string email)
    {
        using var db = new AppDbContext();
        return db.Users.AsNoTracking()
                 .FirstOrDefault(u => u.Email.ToLower() == email.Trim().ToLower());
    }

    /// <summary>Verifies credentials. Returns null for unknown email, wrong password or a disabled account.</summary>
    public User? Authenticate(string email, string password)
    {
        var user = FindByEmail(email);
        if (user == null || !user.IsActive) return null;
        return PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }

    public void RecordLogin(int userId)
    {
        using var db = new AppDbContext();
        var u = db.Users.Find(userId);
        if (u == null) return;
        u.LastLoginUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    /// <summary>Updates the profile fields. Returns an error message, or null on success.</summary>
    public string? UpdateProfile(int userId, string firstName, string lastName, string email)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email)) return "Email address is required.";
        if (!email.Contains('@'))             return "Enter a valid email address.";

        using var db = new AppDbContext();
        var u = db.Users.Find(userId);
        if (u == null) return "Account no longer exists.";

        // Email is the username, so it has to stay unique across accounts.
        if (db.Users.Any(o => o.Id != userId && o.Email.ToLower() == email.ToLower()))
            return "Another account already uses that email address.";

        u.FirstName = firstName.Trim();
        u.LastName  = lastName.Trim();
        u.Email     = email;
        db.SaveChanges();

        CurrentUser.Refresh(u);
        return null;
    }

    /// <summary>Changes a password after verifying the current one. Returns an error, or null on success.</summary>
    public string? ChangePassword(int userId, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword)) return "New password is required.";
        if (newPassword.Length < 8)                 return "New password must be at least 8 characters.";

        using var db = new AppDbContext();
        var u = db.Users.Find(userId);
        if (u == null) return "Account no longer exists.";

        if (!PasswordHasher.Verify(currentPassword, u.PasswordHash))
            return "Current password is incorrect.";
        if (PasswordHasher.Verify(newPassword, u.PasswordHash))
            return "New password must be different from the current one.";

        u.PasswordHash       = PasswordHasher.Hash(newPassword);
        u.MustChangePassword = false;
        db.SaveChanges();

        CurrentUser.Refresh(u);
        return null;
    }

    public List<User> AllUsers()
    {
        using var db = new AppDbContext();
        return db.Users.AsNoTracking().OrderBy(u => u.Email).ToList();
    }

    /// <summary>
    /// Creates a new account. requireChangeOnFirstLogin should stay true for the normal admin
    /// case (an admin-assigned temporary password must never stay live) — the one exception is
    /// first-run account creation, where the person is choosing their own real password
    /// directly, not a temporary one someone else assigned them.
    /// Returns an error message, or null on success.
    /// </summary>
    public string? CreateUser(string email, string firstName, string lastName, string role,
        string initialPassword, bool requireChangeOnFirstLogin = true)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email)) return "Email address is required.";
        if (!email.Contains('@'))             return "Enter a valid email address.";
        if (string.IsNullOrWhiteSpace(initialPassword) || initialPassword.Length < 8)
            return "Initial password must be at least 8 characters.";
        if (role != "SuperAdmin" && role != "User") return "Role must be SuperAdmin or User.";

        using var db = new AppDbContext();
        if (db.Users.Any(u => u.Email.ToLower() == email.ToLower()))
            return "An account with that email address already exists.";

        db.Users.Add(new User
        {
            Email               = email,
            FirstName           = firstName.Trim(),
            LastName            = lastName.Trim(),
            PasswordHash        = PasswordHasher.Hash(initialPassword),
            Role                = role,
            IsActive            = true,
            MustChangePassword  = requireChangeOnFirstLogin,
        });
        db.SaveChanges();
        return null;
    }

    /// <summary>
    /// Admin action: sets a new password directly for another user (no knowledge of their old
    /// one required). MustChangePassword is set so the admin-typed password never stays live.
    /// </summary>
    public string? AdminResetPassword(int userId, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            return "New password must be at least 8 characters.";

        using var db = new AppDbContext();
        var u = db.Users.Find(userId);
        if (u == null) return "Account no longer exists.";

        u.PasswordHash       = PasswordHasher.Hash(newPassword);
        u.MustChangePassword = true;
        db.SaveChanges();
        return null;
    }

    /// <summary>Admin action: activates or deactivates an account (no hard delete — keeps audit history intact).</summary>
    public void SetActive(int userId, bool active)
    {
        using var db = new AppDbContext();
        var u = db.Users.Find(userId);
        if (u == null) return;
        u.IsActive = active;
        db.SaveChanges();
    }
}
