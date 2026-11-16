using Microsoft.Data.Sqlite;
using PMDashboard.Data;

namespace PMDashboard.Services;

/// <summary>
/// Network mode's equivalent of Cloud mode's "someone else changed something" notification.
/// Everyone reads/writes the same file, so there is nothing to sync — but pages that are already
/// on screen don't know another computer just saved, and would keep showing what they loaded.
///
/// Every <see cref="IntervalSeconds"/> it checks SQLite's <c>PRAGMA data_version</c> on one
/// long-lived connection (changes whenever ANY other connection commits to the file — a cheap
/// check that reads no table data over the network). When it moved, the new AuditEntries rows
/// say who did what: rows from other computers → "3 changes by Sarah Chen". Changes with no
/// audit row are attributed to another computer only if this process hasn't saved anything
/// itself since the last check (<see cref="AppDbContext.LastSaveUtc"/>). The result goes out
/// through <see cref="CloudSyncService.PublishExternalChange"/>, so the existing subscribers
/// (MainWindow toast, Workload reload, MainViewModel's current-page refresh) all just work.
/// </summary>
public sealed class NetworkChangeWatcher : IDisposable
{
    public const int IntervalSeconds = 20;

    // Account activity, not data changes — not worth a toast or a page reload.
    private static readonly string[] IgnoredActions = ["Login", "AutoLogin", "LoginFailed", "ChangePassword"];

    private readonly object _gate = new();
    private Timer? _timer;
    private SqliteConnection? _conn;
    private long _dataVersion = -1;
    private long _lastAuditId = -1;
    private DateTime _lastCheckUtc = DateTime.UtcNow;
    private int _busy;

    /// <summary>Starts watching if SAT is on the shared network file right now, otherwise stops.
    /// Called at startup and whenever Travel Sync switches files.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            StopCore();
            if (!NetworkDb.IsShared) return;
            _timer = new Timer(_ => Tick(), null,
                TimeSpan.FromSeconds(IntervalSeconds), TimeSpan.FromSeconds(IntervalSeconds));
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // previous check still running (slow share)
        var checkStartedUtc = DateTime.UtcNow;
        try
        {
            if (_conn == null)
            {
                _conn = new SqliteConnection(
                    NetworkDb.ConnectionString(DatabaseConfig.NetworkDbPath, SqliteOpenMode.ReadOnly) + ";Pooling=False");
                _conn.Open();
                _dataVersion = -1;
            }

            var version = Convert.ToInt64(Scalar("PRAGMA data_version"));
            if (_dataVersion < 0)
            {
                // First check on this connection: just take the baseline.
                _dataVersion = version;
                _lastAuditId = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(Id), 0) FROM AuditEntries"));
                return;
            }
            if (version == _dataVersion) return;
            _dataVersion = version;

            var savedHere = AppDbContext.LastSaveUtc >= _lastCheckUtc;
            var (anyNew, foreign) = ReadNewAuditEntries();

            string? summary = null;
            if (foreign.Count > 0)
            {
                summary = string.Join(", ", foreign
                    .GroupBy(f => f)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} change{(g.Count() == 1 ? "" : "s")} by {g.Key}"));
            }
            else if (!anyNew && !savedHere)
            {
                summary = "Changes saved from another computer";
            }

            if (summary != null)
                App.CloudSync.PublishExternalChange(summary);
        }
        catch
        {
            // Share blipped, file briefly locked, etc. — drop the connection and start over next tick.
            try { _conn?.Dispose(); } catch { }
            _conn = null;
        }
        finally
        {
            _lastCheckUtc = checkStartedUtc;
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Audit rows added since the last check: whether there were any at all, and the
    /// display name behind each one written by another computer (account activity excluded).</summary>
    private (bool AnyNew, List<string> Foreign) ReadNewAuditEntries()
    {
        var foreign = new List<string>();
        bool anyNew = false;
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT Id, MachineName, Action, COALESCE(NULLIF(UserName, ''), NULLIF(UserEmail, ''), MachineName)
            FROM AuditEntries WHERE Id > $last ORDER BY Id
            """;
        cmd.Parameters.AddWithValue("$last", _lastAuditId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            anyNew = true;
            _lastAuditId = Math.Max(_lastAuditId, rd.GetInt64(0));
            var machine = rd.IsDBNull(1) ? "" : rd.GetString(1);
            var action  = rd.IsDBNull(2) ? "" : rd.GetString(2);
            if (string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
            if (IgnoredActions.Contains(action, StringComparer.OrdinalIgnoreCase)) continue;
            foreign.Add(rd.IsDBNull(3) ? "another computer" : rd.GetString(3));
        }
        return (anyNew, foreign);
    }

    private object? Scalar(string sql)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void StopCore()
    {
        _timer?.Dispose();
        _timer = null;
        try { _conn?.Dispose(); } catch { }
        _conn = null;
        _dataVersion = -1;
    }

    public void Dispose()
    {
        lock (_gate) StopCore();
    }
}
