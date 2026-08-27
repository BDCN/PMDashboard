using System.IO;
using System.Text.Json;

namespace PMDashboard.Services;

/// <summary>
/// Admin-gate password, stored in %APPDATA%\PMDashboard\adminconfig.json -
/// same out-of-source-control pattern as DatabaseConfig, and for the same
/// reason: this used to be a hardcoded literal in PasswordDialog.xaml.cs,
/// which meant it would have shipped straight into git history the moment
/// this project was pushed to GitHub. No default value ships in source;
/// an unconfigured machine has no admin password until Save() is called
/// once (e.g. from a first-run setup step), and TryConfirm() in
/// PasswordDialog must never treat an empty configured password as a match.
/// </summary>
public static class AdminConfig
{
    private static readonly string _configDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PMDashboard");
    private static readonly string _configFile =
        Path.Combine(_configDir, "adminconfig.json");

    public static string Password { get; private set; } = "";

    public static void Load()
    {
        if (!File.Exists(_configFile)) return;
        try
        {
            var json = File.ReadAllText(_configFile);
            var obj  = JsonSerializer.Deserialize<Dto>(json);
            Password = obj?.Password ?? "";
        }
        catch { /* corrupt file - fall back to unconfigured */ }
    }

    public static void Save(string password)
    {
        Password = password;
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configFile, JsonSerializer.Serialize(
            new Dto { Password = password },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private class Dto
    {
        public string? Password { get; set; }
    }
}
