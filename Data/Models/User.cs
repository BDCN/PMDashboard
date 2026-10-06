namespace PMDashboard.Data.Models;

/// <summary>
/// An application user. Email doubles as the username, so it is the natural key even though
/// Id remains the primary key (the sync layer merges on integer Id like every other table).
/// </summary>
public class User
{
    public int    Id           { get; set; }
    public string Email        { get; set; } = "";   // username; compared case-insensitively
    public string FirstName    { get; set; } = "";
    public string LastName     { get; set; } = "";
    public string PasswordHash { get; set; } = "";   // PasswordHasher format; never the plaintext

    /// <summary>
    /// "SuperAdmin" | "Director" | "User". SuperAdmin can manage other accounts and the full
    /// Configuration page. Director can manage other accounts (Manage Users only, not the rest
    /// of Configuration) but cannot create, reset, or deactivate a SuperAdmin account.
    /// </summary>
    public string Role { get; set; } = "User";

    public bool IsActive { get; set; } = true;

    /// <summary>Set on a newly created or reset account; the login flow forces a change before continuing.</summary>
    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginUtc { get; set; }

    public DateTime LastModified { get; set; }

    public string FullName    => $"{FirstName} {LastName}".Trim();
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Email : FullName;
    public bool   IsSuperAdmin => Role == "SuperAdmin";
    public bool   IsDirector   => Role == "Director";
}
