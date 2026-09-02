using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PMDashboard.Services;

/// <summary>
/// Admin-gate password, stored as a PBKDF2 hash in the AppConfigs table under
/// ConfigKeys.AdminPasswordHash.
///
/// It lives in the database rather than in a per-machine file because AppConfigs
/// already round-trips through every CloudSyncService method — so the password is
/// set once and reaches every machine, instead of each machine needing the file
/// hand-copied to it. The previous %APPDATA%\PMDashboard\adminconfig.json holder
/// had no default in source and no UI that ever wrote it, which meant a fresh
/// machine was silently locked out of Source Data and Database for good.
///
/// A machine with no password configured is bootstrapped through the dialog's
/// "set password" mode rather than by a default value in source.
///
/// Note this is a convenience gate, not a security boundary: anyone with write
/// access to the SQLite file can replace the hash. It exists to stop accidental
/// changes to Source Data / Database, not to resist an attacker.
/// </summary>
public static class AdminConfig
{
    private const int    Iterations = 100_000;
    private const int    SaltBytes  = 16;
    private const int    HashBytes  = 32;
    private const string Prefix     = "pbkdf2";

    /// <summary>Legacy per-machine file, read once to migrate an existing password into the DB.</summary>
    private static readonly string _legacyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PMDashboard", "adminconfig.json");

    /// <summary>True once a password has been set. When false the gate cannot be unlocked.</summary>
    public static bool IsConfigured { get; private set; }

    /// <summary>
    /// Reads the stored hash. Must be called AFTER EnsureCreated() and after the
    /// LastModified migrations, since it queries AppConfigs.
    /// </summary>
    public static void Load()
    {
        var stored = new ConfigService().Get(ConfigKeys.AdminPasswordHash, "");

        if (string.IsNullOrWhiteSpace(stored))
        {
            // One-time migration off the old per-machine file, so a machine that
            // already had a working password keeps it.
            var legacy = ReadLegacyFile();
            if (!string.IsNullOrEmpty(legacy))
            {
                SetPassword(legacy);
                return;
            }
        }

        IsConfigured = !string.IsNullOrWhiteSpace(stored);
    }

    /// <summary>Hashes and stores a new admin password. Syncs to other machines via AppConfigs.</summary>
    public static void SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Admin password cannot be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        new ConfigService().Set(
            ConfigKeys.AdminPasswordHash,
            $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");

        IsConfigured = true;
    }

    /// <summary>
    /// Constant-time check of a candidate password. Always false when no password is
    /// configured or the candidate is empty — an unconfigured gate must never open.
    /// </summary>
    public static bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;

        var stored = new ConfigService().Get(ConfigKeys.AdminPasswordHash, "");
        if (string.IsNullOrWhiteSpace(stored)) return false;

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
            return false; // malformed stored value — treat as no match
        }
    }

    private static string ReadLegacyFile()
    {
        if (!File.Exists(_legacyFile)) return "";
        try
        {
            var obj = JsonSerializer.Deserialize<LegacyDto>(File.ReadAllText(_legacyFile));
            return obj?.Password ?? "";
        }
        catch
        {
            return "";
        }
    }

    private class LegacyDto
    {
        public string? Password { get; set; }
    }
}
