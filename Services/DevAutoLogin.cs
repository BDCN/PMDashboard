using System.IO;
using System.Text.Json;

namespace PMDashboard.Services;

/// <summary>
/// Machine-local "skip the login screen" convenience — opted into per-computer via a checkbox on
/// LoginDialog, not something an admin pushes to everyone. Stores only the account's Email (never
/// a password) in a plain JSON file under %APPDATA%\PMDashboard, same plaintext-local-config
/// pattern DatabaseConfig already uses for the cloud connection string.
///
/// Security tradeoff, by design: whoever can log into this Windows user account on this machine
/// gets into the app as the saved account with no password prompt at all. That's acceptable only
/// because it's an explicit, per-machine, self-service opt-in (never set remotely, never synced
/// to the cloud) meant for a developer's own box — never enable this on a shared or production
/// workstation.
/// </summary>
public static class DevAutoLogin
{
    private record Config(string Email);

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PMDashboard", "devautologin.json");

    public static bool IsEnabled => TryGetSavedEmail() != null;

    public static string? TryGetSavedEmail()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath));
            return string.IsNullOrWhiteSpace(cfg?.Email) ? null : cfg.Email;
        }
        catch
        {
            return null; // corrupt/unreadable file — fall back to the normal login screen
        }
    }

    public static void Enable(string email)
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config(email)));
    }

    public static void Disable()
    {
        try { File.Delete(ConfigPath); } catch { /* already gone */ }
    }
}
