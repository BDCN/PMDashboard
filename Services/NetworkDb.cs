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
    /// Null when this Windows account can SAVE to the database at <paramref name="path"/>, otherwise
    /// a message saying what's wrong. Reading working is not enough — verified 2026-11-17:
    ///  • file read-only for this account (read-only attribute / read-only share or NTFS access) →
    ///    opens fine, every save fails "attempt to write a readonly database" (SQLite 8) — seen on a
    ///    new user's first save (their password change);
    ///  • can't create files in the folder → every save fails "unable to open database file"
    ///    (SQLite 14), since the rollback journal (SAT.db-journal) is created next to the database.
    /// Tested by a real page write inside a transaction that is rolled back, so nothing changes.
    /// (A no-op UPDATE is NOT a valid test: SQLite writes nothing and needs no journal for it.)
    /// </summary>
    public static string? CheckWritable(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).IsReadOnly)
                return ReadOnlyHelp(path, "The database file is marked read-only.");
        }
        catch { /* can't read attributes — the write test below decides */ }

        try
        {
            using var conn = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWrite) + ";Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt64(cmd.ExecuteScalar());
            // Rewriting the header's user_version with its own value is a real page write;
            // the rollback undoes it.
            cmd.CommandText = $"BEGIN IMMEDIATE; PRAGMA user_version = {version}; ROLLBACK;";
            cmd.ExecuteNonQuery();
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return null; } // busy/locked: someone else is saving — that's fine
        catch (SqliteException ex) when (IsNoWriteAccess(ex))
        {
            return ReadOnlyHelp(path, $"Test save failed: {ex.Message}");
        }
        catch { return null; } // anything else is not a permissions problem — don't block on it
    }

    /// <summary>SQLITE_READONLY (8) / SQLITE_CANTOPEN (14) / SQLITE_PERM (3) — this account can't write the file or its folder.</summary>
    public static bool IsNoWriteAccess(SqliteException ex) => ex.SqliteErrorCode is 3 or 8 or 14;

    public static string ReadOnlyHelp(string path, string? detail = null) =>
        $"SAT can read the shared database but can't save to it:\n{path}\n\n" +
        (detail != null ? detail + "\n\n" : "") +
        "Ask IT / the share's owner to give your Windows account \"Modify\" permission on the database " +
        "file and its folder (both the share permission and the folder's Security tab), and to make sure " +
        "the file isn't marked read-only. Until then you can look at data, but changes can't be saved.";

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
