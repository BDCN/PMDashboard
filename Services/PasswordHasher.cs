using System.Security.Cryptography;
using System.Text;

namespace PMDashboard.Services;

/// <summary>
/// PBKDF2-SHA256 password hashing, shared by the admin gate (AdminConfig) and user accounts.
///
/// Format: pbkdf2${iterations}${saltBase64}${hashBase64} — self-describing, so the iteration
/// count can be raised later without invalidating existing hashes.
/// </summary>
public static class PasswordHasher
{
    private const int    Iterations = 100_000;
    private const int    SaltBytes  = 16;
    private const int    HashBytes  = 32;
    private const string Prefix     = "pbkdf2";

    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time verify. False for an empty candidate or a missing/malformed hash.</summary>
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        try
        {
            var salt     = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual   = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
