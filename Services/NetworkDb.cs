using System.IO;
using Microsoft.Data.Sqlite;

namespace PMDashboard.Services;

/// <summary>
/// Rules for using a SQLite file that several computers open at once over a network share
/// (Network mode). Two SQLite facts drive everything here:
///
/// 1. WAL journal mode does not work over a network filesystem — it relies on shared memory
///    (the "-shm" file) that only works between processes on the same computer. EF Core creates
///    new databases in WAL mode, so a file copied from a local install arrives on the share in
///    WAL mode. Network files must use the classic rollback journal (journal_mode=DELETE).
/// 2. A plain File.Copy of a database that is open (here, or by anyone on the share) can capture
///    a half-written file, and silently leaves out anything still in a "-wal" file. Copies are
///    made with SQLite's online backup API instead, which always produces a consistent snapshot.
/// </summary>
public static class NetworkDb
{
    /// <summary>Seconds a command waits for another computer's write lock before failing. A
    /// large import on one machine holds the write lock for a while; 30 s (the default) was too
    /// short for someone else saving a single edit at the same time.</summary>
    public const int BusyTimeoutSeconds = 120;

    /// <summary>True when this computer is reading/writing the shared network file right now
    /// (Network mode and not in Travel Mode).</summary>
    public static bool IsShared =>
        DatabaseConfig.IsNetwork && !DatabaseConfig.IsTravelMode
        && !string.IsNullOrWhiteSpace(DatabaseConfig.NetworkDbPath);

    public static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) =>
        new SqliteConnectionStringBuilder
        {
            DataSource     = path,
            Mode           = mode,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();

    /// <summary>Null when the network database can be opened, otherwise a message saying why not.</summary>
    public static string? CheckReachable(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                return $"The network folder can't be reached:\n{dir}\n\nCheck that you're connected to the office network / VPN and the share is available.";
            if (!File.Exists(path))
                return $"The network database file doesn't exist:\n{path}";
            using var conn = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWrite));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            cmd.ExecuteScalar();
            return null;
        }
        catch (Exception ex)
        {
            return $"The network database can't be opened:\n{path}\n\n{ex.Message}";
        }
    }

    /// <summary>
    /// Switches the file to the rollback journal if it's in WAL mode. Returns the journal mode
    /// now in effect ("delete" on success). Leaving WAL needs nobody else to have the file open,
    /// so on a busy share this can return "wal" — the caller warns and it's retried next start.
    /// </summary>
    public static string EnsureRollbackJournal(string path)
    {
        using var conn = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWrite) + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var mode = (cmd.ExecuteScalar() as string ?? "").ToLowerInvariant();
        if (mode != "wal") return mode;
        try
        {
            cmd.CommandText = "PRAGMA journal_mode = DELETE";
            return (cmd.ExecuteScalar() as string ?? "").ToLowerInvariant();
        }
        catch (SqliteException)
        {
            return "wal"; // another computer has it open — try again later
        }
    }

    /// <summary>
    /// Copies a SQLite database with the online backup API (a consistent snapshot, including
    /// anything still in a "-wal" file, even while others are writing to the source).
    /// <paramref name="forNetwork"/> → the copy is switched to the rollback journal (see class doc).
    /// </summary>
    public static void CopyDatabase(string sourcePath, string destPath, bool forNetwork)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Pooled connections would keep the destination open (and could be holding a stale view
        // of it) — release them before overwriting it.
        SqliteConnection.ClearAllPools();

        using (var src = new SqliteConnection(ConnectionString(sourcePath, SqliteOpenMode.ReadOnly) + ";Pooling=False"))
        using (var dst = new SqliteConnection(ConnectionString(destPath) + ";Pooling=False"))
        {
            src.Open();
            dst.Open();
            src.BackupDatabase(dst);
            using var cmd = dst.CreateCommand();
            cmd.CommandText = forNetwork ? "PRAGMA journal_mode = DELETE" : "PRAGMA journal_mode";
            cmd.ExecuteScalar();
        }
        SqliteConnection.ClearAllPools();
    }
}
