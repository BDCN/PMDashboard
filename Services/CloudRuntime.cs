using MySqlConnector;

namespace PMDashboard.Services;

/// <summary>
/// Whether Cloud mode is currently serving reads and writes directly from MariaDB.
///
/// In Cloud mode the cloud database is authoritative: every AppDbContext talks to MariaDB,
/// and the local SQLite file is only a mirror kept for offline access. That is the opposite
/// of the original design, where all app code read and wrote local SQLite for speed and a
/// background service reconciled the two — which allowed each machine to diverge silently.
///
/// If the server cannot be reached at startup the app falls back to the local mirror so it
/// still opens, and says so on the Configuration page rather than failing outright.
/// </summary>
public static class CloudRuntime
{
    /// <summary>True when contexts should use MariaDB. False in Local/Network mode, or when the probe failed.</summary>
    public static bool IsCloudActive { get; private set; }

    /// <summary>True when Cloud mode is configured but the server could not be reached.</summary>
    public static bool IsUsingLocalFallback { get; private set; }

    public static string LastError { get; private set; } = "";

    /// <summary>
    /// Tests the configured cloud connection and latches the result for this run.
    ///
    /// Deliberately synchronous and called once at startup, before any context is created:
    /// OnConfiguring must not have to make a network decision per context, and must never
    /// re-enter this (the probe uses MySqlConnection directly, not AppDbContext).
    /// </summary>
    public static void Probe()
    {
        IsCloudActive        = false;
        IsUsingLocalFallback = false;
        LastError            = "";

        if (!DatabaseConfig.IsCloud) return;

        var cs = DatabaseConfig.CloudConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
        {
            IsUsingLocalFallback = true;
            LastError            = "No cloud connection string configured.";
            return;
        }

        try
        {
            using var conn = new MySqlConnection(cs);
            conn.Open();
            IsCloudActive = true;
        }
        catch (Exception ex)
        {
            IsUsingLocalFallback = true;
            LastError            = ex.Message;
        }
    }

    /// <summary>
    /// Drops to the local mirror for the rest of the session after a cloud call fails
    /// mid-run, so one dropped connection doesn't leave every later query throwing.
    /// </summary>
    public static void DemoteToLocal(string reason)
    {
        if (!IsCloudActive) return;
        IsCloudActive        = false;
        IsUsingLocalFallback = true;
        LastError            = reason;
    }

    public static string StatusText =>
        !DatabaseConfig.IsCloud   ? "Local database"
        : IsCloudActive           ? "Cloud (MariaDB) — reads and writes go to the server; local file is a mirror"
                                  : $"OFFLINE — using local mirror. Cloud unreachable: {LastError}";
}
