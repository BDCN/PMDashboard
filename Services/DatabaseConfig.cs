using System.IO;
using System.Text.Json;

namespace PMDashboard.Services;

public enum DatabaseMode { Local, Network, Cloud }

/// <summary>
/// Persistent database connection settings stored in %APPDATA%\PMDashboard\dbconfig.json —
/// outside the app database itself so they can be read before the first DbContext is created.
/// </summary>
public static class DatabaseConfig
{
    private static readonly string _configDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PMDashboard");
    private static readonly string _configFile =
        Path.Combine(_configDir, "dbconfig.json");

    // ── Current values (loaded by Load()) ────────────────────────────────────
    public static DatabaseMode Mode                 { get; private set; } = DatabaseMode.Local;
    public static string       NetworkDbPath        { get; private set; } = "";
    public static string       CloudConnectionString { get; private set; } = "";
    public static bool         IsTravelMode         { get; private set; } = false;

    // ── Computed paths ────────────────────────────────────────────────────────
    public static string LocalDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PMDashboard", "data.db");

    /// <summary>
    /// The SQLite file path the app should use right now.
    /// Network mode while NOT traveling → network file.
    /// Network mode while traveling OR Local mode → local file.
    /// Cloud mode → unused (MySQL connection string is used instead).
    /// </summary>
    public static string ActiveSqlitePath
    {
        get
        {
            if (Mode == DatabaseMode.Network && !IsTravelMode && !string.IsNullOrWhiteSpace(NetworkDbPath))
                return NetworkDbPath;
            return LocalDbPath;
        }
    }

    public static bool IsCloud   => Mode == DatabaseMode.Cloud;
    public static bool IsNetwork => Mode == DatabaseMode.Network;
    public static bool IsLocal   => Mode == DatabaseMode.Local;

    // ── Persistence ───────────────────────────────────────────────────────────

    public static void Load()
    {
        if (!File.Exists(_configFile)) return;
        try
        {
            var json = File.ReadAllText(_configFile);
            var obj  = JsonSerializer.Deserialize<Dto>(json);
            if (obj == null) return;
            Mode                  = obj.Mode;
            NetworkDbPath         = obj.NetworkDbPath        ?? "";
            CloudConnectionString = obj.CloudConnectionString ?? "";
            IsTravelMode          = obj.IsTravelMode;
        }
        catch { /* corrupt file — fall back to Local */ }
    }

    public static void Save()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configFile, JsonSerializer.Serialize(
            new Dto
            {
                Mode                  = Mode,
                NetworkDbPath         = NetworkDbPath,
                CloudConnectionString = CloudConnectionString,
                IsTravelMode          = IsTravelMode,
            },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Apply helpers (call then restart the EF contexts as needed) ───────────

    public static void ApplyLocal()
    {
        Mode         = DatabaseMode.Local;
        IsTravelMode = false;
        Save();
    }

    public static void ApplyNetwork(string path)
    {
        Mode          = DatabaseMode.Network;
        NetworkDbPath = path;
        IsTravelMode  = false;
        Save();
    }

    public static void ApplyCloud(string connectionString)
    {
        Mode                  = DatabaseMode.Cloud;
        CloudConnectionString = connectionString;
        IsTravelMode          = false;
        Save();
    }

    public static void SetTravelMode(bool traveling)
    {
        IsTravelMode = traveling;
        Save();
    }

    private class Dto
    {
        public DatabaseMode Mode                  { get; set; } = DatabaseMode.Local;
        public string?      NetworkDbPath         { get; set; }
        public string?      CloudConnectionString { get; set; }
        public bool         IsTravelMode          { get; set; }
    }
}
