using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PMDashboard.Data;

namespace PMDashboard.Services;

public sealed record SyncOutcome(bool Ok, bool Offline, int Pushed, int Pulled, int DeletedHere, int DeletedThere,
                                 string? Error, string? ChangeSummary)
{
    public static SyncOutcome OfflineResult(string why) => new(false, true, 0, 0, 0, 0, why, null);
}

/// <summary>
/// Network mode, local-first (v1.13.0): every page reads and writes this computer's working copy
/// (<see cref="DatabaseConfig.NetworkCachePath"/>), and this service keeps it in step with the
/// shared network file — every <see cref="IntervalSeconds"/> s, a few seconds after any save, at
/// startup and on exit. Page switches never touch the network; only the changes move.
///
/// One sync = ONE transaction on one connection: working copy as "main", shared file ATTACHed as
/// "net", BEGIN IMMEDIATE (locks both — another computer's sync waits a second or two):
///   1. deletions in   — net's DeletedRows log since our pull cursor → applied here (Tombstones.Apply)
///   2. deletions out  — our DeletedRows since our push cursor → applied there, restamped with the
///                       shared clock so everyone's pull cursor sees them
///   3. changes out    — rows in our SyncDirty list (filled by INSERT/UPDATE triggers on the working
///                       copy) → upserted there with LastModified = the shared clock's next stamp
///   4. changes in     — net rows with LastModified > our pull cursor that we don't already have
///   5. cursors        — pull cursor = shared clock; dirty list cleared
///
/// Why a shared clock ("Clock" in net.SyncState, only ever advanced under the lock): every row a
/// sync writes to the network is stamped max(now, Clock + 1 ms), so network stamps only increase
/// and a pull cursor can never skip a row because some PC's clock is slow (the Cloud-sync bug
/// fixed 2026-10). Why a dirty list rather than "LastModified > cursor": an edit whose save was
/// waiting on the lock, or an edit made from a screen loaded before the last sync, could carry an
/// older timestamp than the cursor and silently never upload.
///
/// Conflicts: if the network copy changed since our last pull AND that arrival is later than our
/// edit, theirs wins (ours is overwritten in step 4); otherwise ours wins. Deletions are the same
/// last-writer-wins rule (Tombstones). Ids can't collide: every computer creates rows in its own
/// Id range (IdAllocator, slot claimed in the shared file in step 0).
/// </summary>
public static class NetworkSync
{
    public const int IntervalSeconds = 30;
    private const string Fmt = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    /// <summary>Re-checks local deletions this far back each sync — covers a delete whose
    /// transaction was waiting on the lock while a sync ran. Re-applying one is a no-op.</summary>
    private static readonly TimeSpan DeleteOverlap = TimeSpan.FromMinutes(5);

    private static readonly string[] ExcludedConfigKeys = { "ImportLock" };
    private static readonly string[] ExcludedConfigPrefixes = { "IdSlot.", "ClientVersion." };
    private static readonly HashSet<string> IgnoredAuditActions =
        new(StringComparer.OrdinalIgnoreCase) { "Login", "AutoLogin", "LoginFailed", "ChangePassword" };

    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly HashSet<string> _infraDone = new();
    private static Timer? _timer, _debounce;

    public static bool IsEnabled => DatabaseConfig.UsesLocalCache;
    public static DateTime? LastSyncUtc { get; private set; }
    public static bool IsOffline { get; private set; }
    public static string? LastError { get; private set; }
    public static bool IsSyncing { get; private set; }

    /// <summary>Status changed (last sync, offline, error) — raised on a background thread.</summary>
    public static event Action? StatusChanged;

    private static Tables AllTables => new(SyncService.IntPkTables.Append("AppConfigs").ToArray());
    private sealed record Tables(string[] Names);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public static void Start()
    {
        if (!IsEnabled) return;
        Stop();
        AppDbContext.LocalSaved += OnLocalSaved;
        _timer = new Timer(_ => SyncInBackground(), null,
                           TimeSpan.FromSeconds(IntervalSeconds), TimeSpan.FromSeconds(IntervalSeconds));
    }

    public static void Stop()
    {
        AppDbContext.LocalSaved -= OnLocalSaved;
        _timer?.Dispose();    _timer = null;
        _debounce?.Dispose(); _debounce = null;
    }

    /// <summary>A save happened — sync a few seconds later (a burst of edits goes in one trip).</summary>
    private static void OnLocalSaved() => RequestSync();

    public static void RequestSync(int delayMs = 3000)
    {
        if (!IsEnabled) return;
        var t = new Timer(_ => SyncInBackground(), null, delayMs, Timeout.Infinite);
        Interlocked.Exchange(ref _debounce, t)?.Dispose();
    }

    private static void SyncInBackground()
    {
        try { SyncNow(waitMs: 0); } catch { /* recorded in LastError */ }
    }

    public static Task<SyncOutcome> SyncNowAsync() => Task.Run(() => SyncNow(waitMs: Timeout.Infinite));

    /// <summary>Runs one sync now (or waits for the one in progress, then runs). waitMs = 0 →
    /// skip if one is already running. Never throws; the outcome says what happened.</summary>
    public static SyncOutcome SyncNow(int waitMs = Timeout.Infinite)
    {
        if (!IsEnabled) return new SyncOutcome(true, false, 0, 0, 0, 0, null, null);
        if (!_gate.Wait(waitMs)) return new SyncOutcome(true, false, 0, 0, 0, 0, null, null);
        try
        {
            IsSyncing = true;
            SyncOutcome outcome;
            try
            {
                outcome = RunSync(DatabaseConfig.NetworkCachePath, DatabaseConfig.NetworkDbPath, claimSlot: true);
            }
            catch (SqliteException ex) when (NetworkDb.IsNoWriteAccess(ex))
            {
                outcome = new SyncOutcome(false, false, 0, 0, 0, 0,
                    "Can't save to the shared database (Windows permissions) — changes stay on this computer.", null);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                outcome = new SyncOutcome(false, false, 0, 0, 0, 0, "The shared database is busy — will retry.", null);
            }
            catch (IOException ex)
            {
                outcome = SyncOutcome.OfflineResult(ex.Message);
            }
            catch (Exception ex)
            {
                outcome = new SyncOutcome(false, false, 0, 0, 0, 0, ex.Message, null);
            }

            IsOffline = outcome.Offline;
            LastError = outcome.Ok ? null : outcome.Error;
            if (outcome.Ok) LastSyncUtc = DateTime.UtcNow;
            if (outcome.Pushed > 0 || outcome.Pulled > 0 || outcome.DeletedHere > 0 || outcome.DeletedThere > 0)
                IdAllocator.ResetCounters();
            if (outcome.ChangeSummary is { } summary)
                App.CloudSync.PublishExternalChange(summary);   // toast + page refresh, as before
            return outcome;
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
            try { StatusChanged?.Invoke(); } catch { }
        }
    }

    // ── Working copy ──────────────────────────────────────────────────────────

    /// <summary>The working copy exists and mirrors the configured network file.</summary>
    public static bool CacheReady() =>
        File.Exists(DatabaseConfig.NetworkCachePath) &&
        string.Equals(ReadLocalState(DatabaseConfig.NetworkCachePath, "Local.MirrorOf"),
                      DatabaseConfig.NetworkDbPath, StringComparison.OrdinalIgnoreCase);

    private static string? ReadLocalState(string cachePath, string key)
    {
        try
        {
            using var c = new SqliteConnection(NetworkDb.ConnectionString(cachePath, SqliteOpenMode.ReadOnly) + ";Pooling=False");
            c.Open();
            if (!TableExists(c, "main", "SyncState")) return null;
            return GetState(c, "main", key);
        }
        catch { return null; }
    }

    /// <summary>
    /// (Re)creates the working copy from the network file (backup API — a consistent snapshot
    /// even while others write). The shared clock is made to exist first, so the copy's pull
    /// cursor is on it. Replaces any previous working copy (it mirrored a different file).
    /// </summary>
    internal static void SeedCache(string netPath, string cachePath)
    {
        using (var net = new SqliteConnection(NetworkDb.ConnectionString(netPath, SqliteOpenMode.ReadWrite) + ";Pooling=False"))
        {
            net.Open();
            Exec(net, "BEGIN IMMEDIATE");
            EnsureStateTable(net, "main");
            if (GetState(net, "main", "Clock") == null) SetState(net, "main", "Clock", Stamp(DateTime.UtcNow));
            Exec(net, "COMMIT");
        }

        var tmp = cachePath + ".new";
        foreach (var f in new[] { tmp, tmp + "-journal" }) if (File.Exists(f)) File.Delete(f);
        NetworkDb.CopyDatabase(netPath, tmp, forNetwork: true);   // rollback journal: see InstallInfra

        using (var c = new SqliteConnection(NetworkDb.ConnectionString(tmp, SqliteOpenMode.ReadWrite) + ";Pooling=False"))
        {
            c.Open();
            EnsureStateTable(c, "main");
            var clock = GetState(c, "main", "Clock") ?? Stamp(DateTime.UtcNow);
            SetState(c, "main", "Local.MirrorOf", netPath);
            SetState(c, "main", "Local.PullCursor", clock);
            SetState(c, "main", "Local.PushCursor", Stamp(DateTime.UtcNow));
            Exec(c, "DELETE FROM main.SyncState WHERE \"Key\" = 'Local.Applying'");
            InstallLocalTriggers(c);
            if (TableExists(c, "main", "SyncDirty")) Exec(c, "DELETE FROM main.SyncDirty");
        }

        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { cachePath, cachePath + "-journal", cachePath + "-wal", cachePath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
        File.Move(tmp, cachePath);
        lock (_infraDone) _infraDone.Clear();
    }

    // ── One sync ──────────────────────────────────────────────────────────────

    internal static SyncOutcome RunSync(string cachePath, string netPath, bool claimSlot)
    {
        if (!File.Exists(netPath)) return SyncOutcome.OfflineResult($"Can't reach {netPath}");
        if (!File.Exists(cachePath)) throw new InvalidOperationException("The working copy is missing — restart SAT.");

        using var conn = new SqliteConnection(NetworkDb.ConnectionString(cachePath, SqliteOpenMode.ReadWrite) + ";Pooling=False");
        conn.Open();
        Exec(conn, "PRAGMA foreign_keys = OFF");   // Tombstones.Apply handles cascades itself
        using (var att = conn.CreateCommand())
        {
            att.CommandText = "ATTACH DATABASE @p AS net";
            att.Parameters.AddWithValue("@p", netPath);
            att.ExecuteNonQuery();
        }

        Exec(conn, "BEGIN IMMEDIATE");
        try
        {
            InstallInfra(conn, cachePath + "|" + netPath);
            if (claimSlot) IdAllocator.EnsureNetworkSlotClaimed(conn, "net", netPath);
            SetState(conn, "main", "Local.Applying", "1");   // our own writes below aren't "dirty"

            var localNow   = DateTime.UtcNow;
            var clockText  = GetState(conn, "net", "Clock") ?? Stamp(localNow);
            var stamp      = Stamp(Max(localNow, ParseStamp(clockText).AddMilliseconds(1)));
            var pullCursor = GetState(conn, "main", "Local.PullCursor") ?? clockText;
            var pushCursor = GetState(conn, "main", "Local.PushCursor") ?? "0000";
            var sqliteNow  = (string)Scalar(conn, "SELECT strftime('%Y-%m-%d %H:%M:%f', 'now')")!;

            // 1. Deletions in. 2. Deletions out (then restamp the tombstones we wrote there).
            var deletedHere  = Tombstones.Apply(conn, "net", "main", pullCursor, null);
            var deletedThere = Tombstones.Apply(conn, "main", "net", pushCursor, null);
            if (deletedThere > 0)
                Exec(conn, $"UPDATE net.{Tombstones.TableName} SET DeletedUtc = '{stamp}' WHERE DeletedUtc >= '{sqliteNow}'");

            var summary = NewAuditSummary(conn, pullCursor);

            int pushed = 0, pulled = 0;
            foreach (var table in AllTables.Names)
            {
                if (!TableExists(conn, "main", table) || !TableExists(conn, "net", table)) continue;
                var pk   = table == "AppConfigs" ? "Key" : "Id";
                var cols = SharedColumns(conn, table);
                if (!cols.Contains("LastModified", StringComparer.OrdinalIgnoreCase)) continue;
                pushed += PushTable(conn, table, pk, cols, stamp, pullCursor);
                pulled += PullTable(conn, table, pk, cols, pullCursor);
            }

            var clockAfter = clockText;
            if (pushed > 0 || deletedThere > 0)
            {
                SetState(conn, "net", "Clock", stamp);
                clockAfter = stamp;
            }
            SetState(conn, "main", "Local.PullCursor", clockAfter);
            SetState(conn, "main", "Local.PushCursor", Stamp(localNow - DeleteOverlap));
            Exec(conn, "DELETE FROM main.SyncDirty");
            Exec(conn, "DELETE FROM main.SyncState WHERE \"Key\" = 'Local.Applying'");
            Exec(conn, "COMMIT");

            string? change = null;
            if (pulled > 0 || deletedHere > 0)
                change = summary ?? $"{pulled + deletedHere} change(s) synced from the shared database";
            return new SyncOutcome(true, false, pushed, pulled, deletedHere, deletedThere, null, change);
        }
        catch
        {
            try { Exec(conn, "ROLLBACK"); } catch { }
            throw;
        }
    }

    /// <summary>Step 3: our dirty rows → network, unless the network copy is a newer edit by
    /// someone else (changed since our last pull, and later than ours). Pushed rows get the shared
    /// clock's stamp on both sides.</summary>
    private static int PushTable(SqliteConnection conn, string table, string pk, List<string> cols, string stamp, string pullCursor)
    {
        var keyMatch = pk == "Id" ? "CAST(m.Id AS TEXT)" : "m.\"Key\"";
        Exec(conn, "CREATE TEMP TABLE IF NOT EXISTS push (k PRIMARY KEY)");
        Exec(conn, "DELETE FROM temp.push");
        var n = Exec(conn, $"""
            INSERT INTO temp.push (k)
            SELECT m."{pk}" FROM main."{table}" m
            WHERE {keyMatch} IN (SELECT RowKey FROM main.SyncDirty WHERE TableName = '{table}'){ConfigFilter(table, "m")}
              AND NOT EXISTS (SELECT 1 FROM net."{table}" n
                              WHERE n."{pk}" = m."{pk}" AND n.LastModified > '{pullCursor}' AND n.LastModified > m.LastModified){PairGuard(table, "m", "net")}
            """);
        if (n == 0) return 0;

        var colList = string.Join(", ", cols.Select(c => $"\"{c}\""));
        var selList = string.Join(", ", cols.Select(c => c.Equals("LastModified", StringComparison.OrdinalIgnoreCase) ? $"'{stamp}'" : $"s.\"{c}\""));
        var setList = string.Join(", ", cols.Where(c => c != pk).Select(c => $"\"{c}\" = excluded.\"{c}\""));
        Exec(conn, $"""
            INSERT INTO net."{table}" ({colList})
            SELECT {selList} FROM main."{table}" s WHERE s."{pk}" IN (SELECT k FROM temp.push)
            ON CONFLICT("{pk}") DO UPDATE SET {setList}
            """);
        Exec(conn, $"UPDATE main.\"{table}\" SET LastModified = '{stamp}' WHERE \"{pk}\" IN (SELECT k FROM temp.push)");
        return n;
    }

    /// <summary>Step 4: network rows changed since our pull cursor that we don't have as-is.</summary>
    private static int PullTable(SqliteConnection conn, string table, string pk, List<string> cols, string pullCursor)
    {
        var colList = string.Join(", ", cols.Select(c => $"\"{c}\""));
        var selList = string.Join(", ", cols.Select(c => $"s.\"{c}\""));
        var setList = string.Join(", ", cols.Where(c => c != pk).Select(c => $"\"{c}\" = excluded.\"{c}\""));
        return Exec(conn, $"""
            INSERT INTO main."{table}" ({colList})
            SELECT {selList} FROM net."{table}" s
            WHERE s.LastModified > '{pullCursor}'
              AND NOT EXISTS (SELECT 1 FROM main."{table}" d WHERE d."{pk}" = s."{pk}" AND d.LastModified = s.LastModified){ConfigFilter(table, "s")}{PairGuard(table, "s", "main")}
            ON CONFLICT("{pk}") DO UPDATE SET {setList}
            """);
    }

    /// <summary>Per-machine AppConfig rows never sync (import lock, Id slots, version reports).</summary>
    private static string ConfigFilter(string table, string alias) =>
        table != "AppConfigs" ? "" :
        string.Concat(ExcludedConfigKeys.Select(k => $" AND {alias}.\"Key\" <> '{k}'")) +
        string.Concat(ExcludedConfigPrefixes.Select(p => $" AND {alias}.\"Key\" NOT LIKE '{p}%'"));

    /// <summary>RompEquipmentTypes' (RompId, EquipmentCategoryId) is UNIQUE: skip a pair the other
    /// side already has under another Id rather than fail the whole sync on it.</summary>
    private static string PairGuard(string table, string alias, string otherSchema) =>
        table != "RompEquipmentTypes" ? "" :
        $" AND NOT EXISTS (SELECT 1 FROM {otherSchema}.\"RompEquipmentTypes\" x WHERE x.RompId = {alias}.RompId " +
        $"AND x.EquipmentCategoryId = {alias}.EquipmentCategoryId AND x.Id <> {alias}.Id)";

    /// <summary>"3 changes by Sarah Chen" from the audit rows other computers wrote since our pull cursor.</summary>
    private static string? NewAuditSummary(SqliteConnection conn, string pullCursor)
    {
        if (!TableExists(conn, "net", "AuditEntries")) return null;
        var counts = new Dictionary<string, int>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(NULLIF(s.UserName, ''), s.UserEmail, 'someone'), s.Action FROM net.AuditEntries s
            WHERE s.LastModified > '{pullCursor}' AND COALESCE(s.MachineName, '') <> @m
              AND NOT EXISTS (SELECT 1 FROM main.AuditEntries d WHERE d.Id = s.Id)
            """;
        cmd.Parameters.AddWithValue("@m", Environment.MachineName);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            if (IgnoredAuditActions.Contains(rd.IsDBNull(1) ? "" : rd.GetString(1))) continue;
            var who = rd.IsDBNull(0) ? "someone" : rd.GetString(0);
            counts[who] = counts.GetValueOrDefault(who) + 1;
        }
        if (counts.Count == 0) return null;
        return string.Join(", ", counts.OrderByDescending(kv => kv.Value)
                                       .Select(kv => $"{kv.Value} change{(kv.Value == 1 ? "" : "s")} by {kv.Key}"));
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    /// <summary>
    /// Once per process per (working copy, network file): state tables, "last changed" indexes on
    /// both sides (so steps 3–4 never scan whole tables over the network), deletion logs, the
    /// working copy's dirty-tracking triggers, and any table/column the working copy has that the
    /// network file doesn't yet (a newer SAT added it) — data in a column only one side has would
    /// otherwise never sync.
    /// </summary>
    private static void InstallInfra(SqliteConnection conn, string pairKey)
    {
        lock (_infraDone) if (_infraDone.Contains(pairKey)) return;

        EnsureStateTable(conn, "main");
        EnsureStateTable(conn, "net");
        MirrorSchema(conn);
        foreach (var t in AllTables.Names)
            foreach (var schema in new[] { "main", "net" })
                if (TableExists(conn, schema, t) && Columns(conn, schema, t).Contains("LastModified", StringComparer.OrdinalIgnoreCase))
                    Exec(conn, $"CREATE INDEX IF NOT EXISTS {schema}.\"IX_Sync_{t}_LastModified\" ON \"{t}\" (LastModified)");
        Tombstones.EnsureInstalled(conn, "main");
        Tombstones.EnsureInstalled(conn, "net");
        InstallLocalTriggers(conn);

        lock (_infraDone) _infraDone.Add(pairKey);
    }

    /// <summary>Re-run the plumbing checks on the next sync (after startup's schema upgrade added
    /// tables/columns that need triggers, indexes or a counterpart in the shared file).</summary>
    public static void ResetInfra()
    {
        lock (_infraDone) _infraDone.Clear();
    }

    private static void EnsureStateTable(SqliteConnection conn, string schema) =>
        Exec(conn, $"CREATE TABLE IF NOT EXISTS {schema}.SyncState (\"Key\" TEXT NOT NULL PRIMARY KEY, Value TEXT)");

    /// <summary>INSERT/UPDATE triggers on the WORKING COPY only: record the row as dirty unless the
    /// write is the sync itself (Local.Applying set inside the sync's transaction).</summary>
    private static void InstallLocalTriggers(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS main.SyncDirty (
                TableName TEXT NOT NULL, RowKey TEXT NOT NULL, PRIMARY KEY (TableName, RowKey)) WITHOUT ROWID
            """);
        EnsureStateTable(conn, "main");
        foreach (var t in AllTables.Names)
        {
            if (!TableExists(conn, "main", t)) continue;
            var key = t == "AppConfigs" ? "NEW.\"Key\"" : "CAST(NEW.Id AS TEXT)";
            foreach (var (ev, name) in new[] { ("INSERT", "ins"), ("UPDATE", "upd") })
                Exec(conn, $"""
                    CREATE TRIGGER IF NOT EXISTS main."trg_sync_{name}_{t}" AFTER {ev} ON "{t}"
                    WHEN NOT EXISTS (SELECT 1 FROM SyncState WHERE "Key" = 'Local.Applying')
                    BEGIN
                        INSERT OR IGNORE INTO SyncDirty (TableName, RowKey) VALUES ('{t}', {key});
                    END
                    """);
        }
    }

    /// <summary>Adds tables/columns the working copy has and the network file lacks.</summary>
    private static void MirrorSchema(SqliteConnection conn)
    {
        foreach (var t in AllTables.Names)
        {
            if (!TableExists(conn, "main", t)) continue;
            if (!TableExists(conn, "net", t))
            {
                var sql = (string?)Scalar(conn, $"SELECT sql FROM main.sqlite_master WHERE type = 'table' AND name = '{t}'");
                if (sql == null) continue;
                Exec(conn, Regex.Replace(sql, @"^\s*CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?(""?\w+""?)",
                                         m => $"CREATE TABLE IF NOT EXISTS net.{m.Groups[2].Value}", RegexOptions.IgnoreCase));
                continue;
            }
            var netCols = Columns(conn, "net", t).ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA main.table_info(\"{t}\")";
            var missing = new List<string>();
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var name = rd.GetString(1);
                    if (netCols.Contains(name)) continue;
                    var type    = rd.IsDBNull(2) ? "" : rd.GetString(2);
                    var notNull = !rd.IsDBNull(3) && rd.GetInt64(3) == 1;
                    var dflt    = rd.IsDBNull(4) ? null : rd.GetString(4);
                    if (notNull && dflt == null)
                        dflt = type.Contains("INT", StringComparison.OrdinalIgnoreCase) || type.Contains("REAL", StringComparison.OrdinalIgnoreCase) ? "0" : "''";
                    missing.Add($"ALTER TABLE net.\"{t}\" ADD COLUMN \"{name}\" {type}{(notNull ? " NOT NULL" : "")}{(dflt != null ? " DEFAULT " + dflt : "")}");
                }
            foreach (var alter in missing) Exec(conn, alter);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>EF Core's SQLite DateTime text format, so EF reads stamps back and string order =
    /// time order. Never a whole second (that format would then end in a bare ".").</summary>
    internal static string Stamp(DateTime utc)
    {
        if (utc.Ticks % TimeSpan.TicksPerSecond == 0) utc = utc.AddTicks(1);
        return utc.ToString(Fmt, CultureInfo.InvariantCulture);
    }

    private static DateTime ParseStamp(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTime.UtcNow;

    private static string? GetState(SqliteConnection conn, string schema, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Value FROM {schema}.SyncState WHERE \"Key\" = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetState(SqliteConnection conn, string schema, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {schema}.SyncState (\"Key\", Value) VALUES (@k, @v) ON CONFLICT(\"Key\") DO UPDATE SET Value = excluded.Value";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string schema, string table) =>
        Convert.ToInt64(Scalar(conn, $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type = 'table' AND name = '{table}'")) > 0;

    private static List<string> Columns(SqliteConnection conn, string schema, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info(\"{table}\")";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) cols.Add(rd.GetString(1));
        return cols;
    }

    private static List<string> SharedColumns(SqliteConnection conn, string table)
    {
        var net = Columns(conn, "net", table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Columns(conn, "main", table).Where(net.Contains).ToList();
    }

    private static int Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
