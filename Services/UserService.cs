using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>Account lookup, authentication and profile maintenance.</summary>
public class UserService
{
    /// <summary>Seeded super-admin address. The password is set once and must be changed at first login.</summary>
    public const string SeedSuperAdminEmail = "charles.bell@iemfg.com";

    /// <summary>
    /// True when the authoritative database has no Users table and accounts are being served
    /// from the local mirror instead.
    ///
    /// A cloud database created from an older setup script has no Users/AuditEntries table.
    /// Letting that throw killed the app during startup - before the user could reach
    /// Config -> Database to copy the script that creates them. Degrading to the local file
    /// keeps the app usable so the schema can actually be fixed.
    /// </summary>
    public static bool UsingLocalFallback { get; private set; }

    public static string LastSchemaError { get; private set; } = "";

    /// <summary>
    /// Decides once per run whether accounts come from the authoritative database or the
    /// mirror. Called before the first account access.
    /// </summary>
    public static void ProbeAccountStore()
    {
        UsingLocalFallback = false;
        LastSchemaError    = "";
        try
        {
            using var db = Db();
            _ = db.Users.Any();
        }
        catch (Exception ex)
        {
            UsingLocalFallback = true;
            LastSchemaError    = ex.Message;
        }
    }

    /// <summary>Context for account access, honouring the fallback decided above.</summary>
    private static AppDbContext Db() =>
        UsingLocalFallback ? new AppDbContext { ForceLocal = true } : new AppDbContext();

    /// <summary>
    /// Creates the super admin if no account exists yet.
    ///
    /// The seed password is a known placeholder, so the account is created with
    /// MustChangePassword set and the login flow refuses to continue until it is replaced.
    /// </summary>
    public void EnsureSeedUser(string seedPassword)
    {
        using var db = Db();
        if (db.Users.Any()) return;

        db.Users.Add(new User
        {
            Email              = SeedSuperAdminEmail,
            FirstName          = "Charles",
            LastName           = "Bell",
            PasswordHash       = PasswordHasher.Hash(seedPassword),
            Role               = "SuperAdmin",
            IsActive           = true,
            MustChangePassword = true,
        });
        db.SaveChanges();
    }

    public User? FindByEmail(string email)
    {
        using var db = Db();
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
        using var db = Db();
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

        using var db = Db();
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

        using var db = Db();
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
        using var db = Db();
        return db.Users.AsNoTracking().OrderBy(u => u.Email).ToList();
    }
}
