using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;

namespace PMDashboard.Services;

public record SyncResult(bool Success, string Message, int Pushed = 0, int Pulled = 0);

/// <summary>
/// Network mode's Travel Sync, between the shared network SQLite file and this computer's local
/// copy (see also <see cref="NetworkDb"/> for the rules of using SQLite over a share).
///
///   SyncBeforeTravel — snapshot the network DB into the local file (SQLite backup API, so it's
///                      consistent even while others are writing), remember each table's max Id
///                      (DatabaseConfig.TravelBaseline), switch to Travel Mode.
///   SyncAfterTravel  — one transaction holding the write lock on BOTH files:
///                      1. Rows created while traveling (Id above the baseline) get fresh Ids
///                         above everything in either file — the office kept creating rows while
///                         you were away, and "max Id + 1" on both sides hands out the SAME Ids,
///                         so matching by Id would make one side's new rows overwrite the other's.
///                         A travel row that is the same thing as a network row by natural key
///                         (an Order's CustomerOrder+OrdLine, a cluster's name, a site's name in
///                         its cluster, …) takes that row's Id instead, so importing the same
///                         spreadsheet on both sides doesn't create duplicates. Foreign keys (and
///                         the few Id references that aren't real FKs) follow.
///                      2. Deletions both ways, from each file's DeletedRows log (see
///                         <see cref="Tombstones"/>): a delete beats an older edit, a newer edit
///                         beats the delete; children follow their parents.
///                      3. Last-writer-wins merge by LastModified, both directions, column by
///                         column name (never SELECT *, so differing column order can't scramble
///                         data, and columns only one side has are left alone).
///                      Both files are backed up first (%LOCALAPPDATA%\PMDashboard\backups).
/// </summary>
public static class SyncService
{
    // Integer-PK tables in FK-safe order (parents before children) — renumbering a parent must
    // rewrite its children's FK columns before the children's natural keys are compared.
    internal static readonly string[] IntPkTables =
    [
        "SubRegions",
        "PMs",
        "TeamMembers",
        "Locations",
        "Campuses",
        "EquipmentCategories",
        "EquipmentKeywords",
        "RompStatuses",
        "AssignmentRules",
        "Sites",
        "Romps",
        "RompEquipmentTypes",
        "AssignmentHistories",
        "ForecastTypes",
        "ForecastFields",
        "ForecastHeaderMappings",
        "Orders",
        "SavedReports",
        "CustomColumns",
        "CustomRows",
        "Users",
        "AuditEntries",
        "ForecastImports",
        "ForecastRows",
    ];

    /// <summary>Columns that identify "the same thing" independently of Id, for tables where both
    /// sides can plausibly create the same record (imports auto-create clusters/sites/ROMPs).
    /// Tables not listed are matched by Id only. lower(x) = case-insensitive.</summary>
    private static readonly Dictionary<string, string[]> _naturalKeys = new()
    {
        ["Locations"]          = ["Name"],
        ["Campuses"]           = ["LocationId", "Name"],
        ["Sites"]              = ["LocationId", "Name"],
        ["Romps"]              = ["SiteId", "Name"],
        ["RompEquipmentTypes"] = ["RompId", "EquipmentCategoryId"],
        ["Orders"]             = ["CustomerOrder", "OrdLine"],
        ["CustomRows"]         = ["Mode", "EntityId"],
        ["Users"]              = ["lower(Email)"],
    };

    // ── Before travel ─────────────────────────────────────────────────────────

    /// <summary>
    /// Download: snapshot the network DB into the local file and switch to Travel Mode.
    /// </summary>
    public static SyncResult SyncBeforeTravel(string networkPath)
    {
        if (DatabaseConfig.IsTravelMode)
            return new SyncResult(false, "Already in Travel Mode. Use \"Sync After Travel\" first.");
        if (NetworkDb.CheckReachable(networkPath) is { } problem)
            return new SyncResult(false, problem);

        try
        {
            var localPath = DatabaseConfig.LocalDbPath;
            if (File.Exists(localPath))
                BackupFile(localPath, "local-before-travel");   // whatever was there is about to be replaced

            NetworkDb.CopyDatabase(networkPath, localPath, forNetwork: false);

            var baseline = new Dictionary<string, long>();
            using (var conn = new SqliteConnection(NetworkDb.ConnectionString(localPath) + ";Pooling=False"))
            {
                conn.Open();
                Tombstones.EnsureInstalled(conn);   // deletions made on the trip must be logged
                foreach (var table in IntPkTables)
                    if (TableExists(conn, "main", table))
                        baseline[table] = Convert.ToInt64(Scalar(conn, $"SELECT COALESCE(MAX(Id), 0) FROM main.\"{table}\""));
            }

            DatabaseConfig.SetTravelMode(true, baseline);
            return new SyncResult(true,
                "The network database has been copied to this computer.\n" +
                "You are now in Travel Mode — SAT uses this computer's copy, and other people won't see your changes yet.\n\n" +
                "When you're back on the office network, go to Configuration → Database and click \"Sync After Travel\" to merge your changes back.");
        }
        catch (Exception ex)
        {
            return new SyncResult(false, $"Sync failed:\n{ex.Message}");
        }
    }

    // ── After travel ──────────────────────────────────────────────────────────

    public static SyncResult SyncAfterTravel(string networkPath)
    {
        var localPath = DatabaseConfig.LocalDbPath;

        // Merging a local file that was never a travel copy (e.g. an old Local-mode database)
        // would push stale rows into the shared database.
        if (!DatabaseConfig.IsTravelMode)
            return new SyncResult(false, "Not in Travel Mode — there's nothing to merge.");
        if (!File.Exists(localPath))
            return new SyncResult(false, "Local database not found.");
        if (NetworkDb.CheckReachable(networkPath) is { } problem)
            return new SyncResult(false, problem);

        try
        {
            SqliteConnection.ClearAllPools();
            BackupFile(localPath,   "local-before-merge");
            BackupFile(networkPath, "network-before-merge");

            int pushed = 0, pulled = 0, renumbered = 0, matched = 0, deletedOnNet = 0, deletedHere = 0;
            var baseline = DatabaseConfig.TravelBaseline;
            var started  = DatabaseConfig.TravelStartedUtc;
            // A day of slack for clock differences between this computer and the office ones.
            var since    = started?.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");

            using (var conn = new SqliteConnection(NetworkDb.ConnectionString(localPath) + ";Pooling=False"))
            {
                conn.Open();
                Execute(conn, "PRAGMA foreign_keys = OFF");   // can't be changed inside a transaction
                Execute(conn, $"ATTACH DATABASE '{Escape(networkPath)}' AS net");
                try
                {
                    // Write lock on both files for the whole merge: nobody in the office can
                    // create a row with an Id we're about to hand out, and a failure part-way
                    // leaves both files exactly as they were.
                    Execute(conn, "BEGIN IMMEDIATE");
                    try
                    {
                        Tombstones.EnsureInstalled(conn, "main");
                        Tombstones.EnsureInstalled(conn, "net");

                        if (baseline != null)
                            (renumbered, matched) = RenumberTravelRows(conn, baseline);

                        // Office deletions → this copy (safe: after the renumbering they can only
                        // match rows that really are shared).
                        deletedHere  = Tombstones.Apply(conn, from: "net",  to: "main", since, maxIdPerTable: null);
                        // Trip deletions → network, only for rows that existed before the trip: a
                        // row created AND deleted on the trip had an Id the office may be using.
                        // Without a baseline (trip started on an older build) that can't be told
                        // apart, so trip deletions aren't sent.
                        if (baseline != null)
                            deletedOnNet = Tombstones.Apply(conn, from: "main", to: "net", since, maxIdPerTable: baseline);

                        foreach (var table in IntPkTables)
                        {
                            if (!TableExists(conn, "main", table) || !TableExists(conn, "net", table)) continue;
                            var cols = SharedColumns(conn, table);
                            if (!cols.Contains("Id") || !cols.Contains("LastModified")) continue;
                            pushed += MergeTable(conn, table, "Id", cols, from: "main", to: "net");
                            pulled += MergeTable(conn, table, "Id", cols, from: "net",  to: "main");
                        }

                        var cfgCols = SharedColumns(conn, "AppConfigs");
                        pushed += MergeTable(conn, "AppConfigs", "Key", cfgCols, from: "main", to: "net");
                        pulled += MergeTable(conn, "AppConfigs", "Key", cfgCols, from: "net",  to: "main");

                        Execute(conn, "COMMIT");
                    }
                    catch
                    {
                        try { Execute(conn, "ROLLBACK"); } catch { /* already rolled back */ }
                        throw;
                    }
                }
                finally
                {
                    try { Execute(conn, "DETACH DATABASE net"); } catch { }
                    try { Execute(conn, "PRAGMA foreign_keys = ON"); } catch { }
                }
            }

            SqliteConnection.ClearAllPools();
            DatabaseConfig.SetTravelMode(false);

            var notes = "";
            if (renumbered + matched > 0)
                notes += $"\n  New records from your trip: {renumbered} given new Ids, {matched} matched to existing records";
            if (baseline == null)
                notes += "\n\nThis trip started on an older version of SAT: new records were matched by Id only, " +
                         "and records you deleted on the trip were not deleted from the network.";
            if (started is { } st && (DateTime.UtcNow - st).TotalDays > Tombstones.KeepDays)
                notes += $"\n\nThis trip lasted over {Tombstones.KeepDays} days — deletions older than that are no longer " +
                         "on record, so a few deleted records may have come back.";
            return new SyncResult(true,
                $"Sync complete.\n" +
                $"  Sent to network      : {pushed} row(s) added/updated, {deletedOnNet} deleted\n" +
                $"  Received from network: {pulled} row(s) added/updated, {deletedHere} deleted" + notes + "\n\n" +
                "Travel Mode is OFF — SAT is using the network database again.",
                Pushed: pushed, Pulled: pulled);
        }
        catch (Exception ex)
        {
            return new SyncResult(false,
                $"Sync failed — nothing was changed, you are still in Travel Mode.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Step 1 of the merge (see class doc): gives every local row created while traveling its
    /// final Id — the matching network row's Id when one exists by natural key, else a fresh Id
    /// above both files' max — and rewrites every reference to it in the local file.
    /// Runs inside the merge transaction. Returns (rows given fresh Ids, rows matched by key).
    /// </summary>
    private static (int Renumbered, int Matched) RenumberTravelRows(SqliteConnection conn, Dictionary<string, long> baseline)
    {
        // Who points at whom, from the EF model (metadata only — no connection is opened).
        using var ctx = new AppDbContext();
        var model = ctx.Model;
        var refsByTable = new Dictionary<string, List<(string Table, string Column)>>(StringComparer.OrdinalIgnoreCase);
        void AddRef(string principal, string table, string column)
        {
            if (!refsByTable.TryGetValue(principal, out var list)) refsByTable[principal] = list = new();
            if (!list.Contains((table, column))) list.Add((table, column));
        }
        foreach (var et in model.GetEntityTypes())
            foreach (var fk in et.GetForeignKeys().Where(f => f.Properties.Count == 1))
                if (fk.PrincipalEntityType.GetTableName() is { } principal && et.GetTableName() is { } dependent)
                    AddRef(principal, dependent, fk.Properties[0].GetColumnName());
        // Id references that aren't declared as FKs in the model.
        AddRef("SubRegions", "PMs", "SubRegionId");

        // AuditEntries.EntityId points at a different table per row, named by EntityType.
        var auditTypeToTable = model.GetEntityTypes()
            .Where(et => et.GetTableName() != null)
            .ToDictionary(et => et.ClrType.Name, et => et.GetTableName()!, StringComparer.OrdinalIgnoreCase);
        auditTypeToTable["Orders"]     = "Orders";  // Custom Data history rows use the mode name
        auditTypeToTable["SitesRomps"] = "Romps";

        int renumbered = 0, matched = 0;
        Execute(conn, "CREATE TEMP TABLE IF NOT EXISTS idmap (OldId INTEGER PRIMARY KEY, NewId INTEGER NOT NULL)");

        foreach (var table in IntPkTables)
        {
            if (!TableExists(conn, "main", table) || !baseline.TryGetValue(table, out var b)) continue;
            if (Convert.ToInt64(Scalar(conn, $"SELECT COUNT(*) FROM main.\"{table}\" WHERE Id > {b}")) == 0) continue;
            bool netHas = TableExists(conn, "net", table);

            Execute(conn, "DELETE FROM temp.idmap");

            // Same record already on the network (by natural key) → use its Id.
            if (netHas && _naturalKeys.TryGetValue(table, out var key))
            {
                var on = string.Join(" AND ", key.Select(k => $"{Qualify(k, "r")} = {Qualify(k, "l")}"));
                Execute(conn, $"""
                    INSERT INTO temp.idmap (OldId, NewId)
                    SELECT l.Id, (SELECT MIN(r.Id) FROM net."{table}" r WHERE {on})
                    FROM main."{table}" l
                    WHERE l.Id > {b}
                      AND EXISTS (SELECT 1 FROM net."{table}" r WHERE {on})
                    """);
                // Never map onto an Id a pre-trip local row still uses, and never two rows onto one Id.
                Execute(conn, $"""
                    DELETE FROM temp.idmap
                    WHERE EXISTS (SELECT 1 FROM main."{table}" x WHERE x.Id = temp.idmap.NewId AND x.Id <= {b})
                       OR NewId IN (SELECT NewId FROM temp.idmap GROUP BY NewId HAVING COUNT(*) > 1)
                    """);
                matched += Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM temp.idmap"));
            }

            // Everything else created on the trip → fresh Ids above both files.
            // Above every Id either file has EVER used — including deleted rows (their DeletedRows
            // entry would otherwise delete the new row) and the AUTOINCREMENT counter.
            var maxNet = netHas ? Convert.ToInt64(Scalar(conn, $"SELECT COALESCE(MAX(Id), 0) FROM net.\"{table}\"")) : 0;
            var maxLoc = Convert.ToInt64(Scalar(conn, $"SELECT COALESCE(MAX(Id), 0) FROM main.\"{table}\""));
            var start  = new[]
            {
                maxNet, maxLoc,
                Tombstones.MaxDeletedId(conn, "main", table),
                netHas ? Tombstones.MaxDeletedId(conn, "net", table) : 0,
                SequenceValue(conn, "main", table),
                netHas ? SequenceValue(conn, "net", table) : 0,
            }.Max() + 1;
            renumbered += Execute(conn, $"""
                INSERT INTO temp.idmap (OldId, NewId)
                SELECT Id, {start} + ROW_NUMBER() OVER (ORDER BY Id) - 1
                FROM main."{table}"
                WHERE Id > {b} AND Id NOT IN (SELECT OldId FROM temp.idmap)
                """);

            Execute(conn, "DELETE FROM temp.idmap WHERE OldId = NewId");
            if (Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM temp.idmap")) == 0) continue;

            // Two steps (via negative Ids) so no intermediate value can collide with a row that
            // hasn't been moved yet.
            Execute(conn, $"UPDATE main.\"{table}\" SET Id = -Id WHERE Id IN (SELECT OldId FROM temp.idmap)");
            Execute(conn, $"UPDATE main.\"{table}\" SET Id = (SELECT NewId FROM temp.idmap WHERE OldId = -main.\"{table}\".Id) WHERE Id < 0");

            if (refsByTable.TryGetValue(table, out var refs))
                foreach (var (refTable, col) in refs)
                {
                    if (!TableExists(conn, "main", refTable) || !Columns(conn, "main", refTable).Contains(col)) continue;
                    Execute(conn, $"""
                        UPDATE main."{refTable}" SET "{col}" = (SELECT NewId FROM temp.idmap WHERE OldId = main."{refTable}"."{col}")
                        WHERE "{col}" IN (SELECT OldId FROM temp.idmap)
                        """);
                }

            if (table == "SubRegions") RewriteIdLists(conn, "PMs", "OtherSubRegionIds");

            if (table is "Orders" or "Romps" && TableExists(conn, "main", "CustomRows"))
                Execute(conn, $"""
                    UPDATE main.CustomRows SET EntityId = (SELECT NewId FROM temp.idmap WHERE OldId = main.CustomRows.EntityId)
                    WHERE Mode = '{(table == "Orders" ? "Orders" : "SitesRomps")}' AND EntityId IN (SELECT OldId FROM temp.idmap)
                    """);

            var auditTypes = auditTypeToTable.Where(kv => kv.Value == table).Select(kv => $"'{kv.Key}'").ToList();
            if (auditTypes.Count > 0 && TableExists(conn, "main", "AuditEntries"))
                Execute(conn, $"""
                    UPDATE main.AuditEntries SET EntityId = (SELECT NewId FROM temp.idmap WHERE OldId = main.AuditEntries.EntityId)
                    WHERE EntityType IN ({string.Join(",", auditTypes)}) AND EntityId IN (SELECT OldId FROM temp.idmap)
                    """);
        }
        Execute(conn, "DROP TABLE IF EXISTS temp.idmap");
        return (renumbered, matched);
    }

    /// <summary>Rewrites a comma-separated Id list column (e.g. PMs.OtherSubRegionIds) through temp.idmap.</summary>
    private static void RewriteIdLists(SqliteConnection conn, string table, string column)
    {
        if (!TableExists(conn, "main", table) || !Columns(conn, "main", table).Contains(column)) return;
        var map = new Dictionary<long, long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT OldId, NewId FROM temp.idmap";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) map[rd.GetInt64(0)] = rd.GetInt64(1);
        }
        var rows = new List<(long Id, string Value)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT Id, \"{column}\" FROM main.\"{table}\" WHERE COALESCE(\"{column}\", '') <> ''";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rows.Add((rd.GetInt64(0), rd.GetString(1)));
        }
        foreach (var (id, value) in rows)
        {
            var rewritten = string.Join(",", value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => long.TryParse(p, out var n) && map.TryGetValue(n, out var m) ? m.ToString() : p));
            if (rewritten == value) continue;
            using var upd = conn.CreateCommand();
            upd.CommandText = $"UPDATE main.\"{table}\" SET \"{column}\" = $v WHERE Id = $id";
            upd.Parameters.AddWithValue("$v", rewritten);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Copies rows from <paramref name="from"/> to <paramref name="to"/> that are missing there
    /// or newer by LastModified. Updates only the named columns (an UPSERT, not INSERT OR
    /// REPLACE — that deletes the row first, resetting any column only <paramref name="to"/>
    /// has to its default).
    /// </summary>
    private static int MergeTable(SqliteConnection conn, string table, string pk, List<string> cols, string from, string to)
    {
        var colList = string.Join(", ", cols.Select(c => $"\"{c}\""));
        var selList = string.Join(", ", cols.Select(c => $"s.\"{c}\""));
        var setList = string.Join(", ", cols.Where(c => c != pk).Select(c => $"\"{c}\" = excluded.\"{c}\""));
        var extraGuard = table == "RompEquipmentTypes"
            // its (RompId, EquipmentCategoryId) pair is UNIQUE — skip a pair the target already
            // has under another Id rather than fail the whole merge on it
            ? $" AND NOT EXISTS (SELECT 1 FROM {to}.\"{table}\" x WHERE x.RompId = s.RompId AND x.EquipmentCategoryId = s.EquipmentCategoryId AND x.Id <> s.Id)"
            : "";
        return Execute(conn, $"""
            INSERT INTO {to}."{table}" ({colList})
            SELECT {selList} FROM {from}."{table}" s
            WHERE (NOT EXISTS (SELECT 1 FROM {to}."{table}" d WHERE d."{pk}" = s."{pk}")
                   OR s.LastModified > (SELECT d.LastModified FROM {to}."{table}" d WHERE d."{pk}" = s."{pk}")){extraGuard}
            ON CONFLICT("{pk}") DO UPDATE SET {setList}
            """);
    }

    // ── Network DB setup ─────────────────────────────────────────────────────

    /// <summary>
    /// Create the network database from this computer's current local database (a consistent
    /// snapshot via the SQLite backup API, switched to the rollback journal — see NetworkDb).
    /// </summary>
    public static SyncResult CreateNetworkDatabase(string networkPath)
    {
        var localPath = DatabaseConfig.LocalDbPath;
        if (!File.Exists(localPath))
            return new SyncResult(false, "Local database not found — launch the app at least once first.");

        try
        {
            if (File.Exists(networkPath))
                return new SyncResult(false,
                    $"A database already exists at:\n{networkPath}\n\nDelete it first if you want to overwrite.");

            NetworkDb.CopyDatabase(localPath, networkPath, forNetwork: true);
            return new SyncResult(true, $"Network database created at:\n{networkPath}");
        }
        catch (Exception ex)
        {
            return new SyncResult(false, $"Could not create network database:\n{ex.Message}");
        }
    }

    // ── Cloud connection test ─────────────────────────────────────────────────

    public static async Task<SyncResult> TestCloudConnectionAsync(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new SyncResult(false, "Connection string is empty.");
        try
        {
            using var conn = new MySqlConnector.MySqlConnection(connectionString);
            await conn.OpenAsync();
            return new SyncResult(true, $"Connection successful.\nServer: {conn.ServerVersion}");
        }
        catch (Exception ex)
        {
            return new SyncResult(false, $"Connection failed:\n{ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static string BackupDirectory => Path.Combine(
        Path.GetDirectoryName(DatabaseConfig.LocalDbPath)!, "backups");

    /// <summary>Snapshot a database into the backups folder before a sync changes it; keeps the
    /// newest 5 of each kind.</summary>
    private static void BackupFile(string dbPath, string kind)
    {
        Directory.CreateDirectory(BackupDirectory);
        var dest = Path.Combine(BackupDirectory, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        NetworkDb.CopyDatabase(dbPath, dest, forNetwork: false);
        foreach (var old in Directory.GetFiles(BackupDirectory, $"{kind}-*.db")
                                     .OrderByDescending(f => f).Skip(5))
            try { File.Delete(old); } catch { }
    }

    /// <summary>The AUTOINCREMENT high-water mark for a table (0 if none).</summary>
    private static long SequenceValue(SqliteConnection conn, string schema, string table)
    {
        if (!TableExists(conn, schema, "sqlite_sequence")) return 0;
        return Convert.ToInt64(Scalar(conn, $"SELECT COALESCE(MAX(seq), 0) FROM {schema}.sqlite_sequence WHERE name = '{Escape(table)}'"));
    }

    private static bool TableExists(SqliteConnection conn, string schema, string table) =>
        Convert.ToInt64(Scalar(conn, $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type = 'table' AND name = '{Escape(table)}'")) > 0;

    private static List<string> Columns(SqliteConnection conn, string schema, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info(\"{table}\")";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) cols.Add(rd.GetString(1));
        return cols;
    }

    /// <summary>Columns present in both files, in the local file's order.</summary>
    private static List<string> SharedColumns(SqliteConnection conn, string table)
    {
        var net = Columns(conn, "net", table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Columns(conn, "main", table).Where(net.Contains).ToList();
    }

    /// <summary>Prefixes a column with a row alias; also handles a wrapped column like lower(Email).</summary>
    private static string Qualify(string keyExpr, string alias) =>
        keyExpr.Contains('(')
            ? keyExpr.Replace("(", $"({alias}.")
            : $"{alias}.\"{keyExpr}\"";

    private static int Execute(SqliteConnection conn, string sql)
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

    private static string Escape(string s) => s.Replace("'", "''");

    // ── MariaDB DDL script ────────────────────────────────────────────────────

    public static string MariaDbDdlScript => """
        -- ================================================================
        -- PMDashboard — MariaDB / MySQL Setup Script
        -- Run this entire script in phpMyAdmin → SQL tab on your server.
        -- After running, enter your connection details in the app and click
        -- "Test Connection".  The app will seed built-in data on first launch.
        -- ================================================================

        CREATE DATABASE IF NOT EXISTS `pmdashboard`
            CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
        USE `pmdashboard`;

        -- AppConfig (key/value store) ────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `AppConfigs` (
            `Key`          VARCHAR(255) NOT NULL,
            `Value`        LONGTEXT     NOT NULL DEFAULT '',
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Key`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Project Managers ───────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `PMs` (
            `Id`                   INT          NOT NULL AUTO_INCREMENT,
            `FirstName`            LONGTEXT     NOT NULL,
            `LastName`             LONGTEXT     NOT NULL,
            `HomeOffice`           LONGTEXT     NOT NULL,
            `GeographicalLocation` LONGTEXT     NOT NULL,
            `DataName`             LONGTEXT     NOT NULL,
            `Capacity`             DOUBLE       NOT NULL DEFAULT 0,
            `IdealUtilization`     DOUBLE       NOT NULL DEFAULT 0,
            `ScoreCapacity`        DOUBLE       NOT NULL DEFAULT 0,
            `PmMultiplier`         INT          NULL,
            `IsActive`             TINYINT(1)   NOT NULL DEFAULT 1,
            `LastModified`         DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                   ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Add PmMultiplier to existing PMs table (safe to run on already-migrated DBs)
        ALTER TABLE `PMs` ADD COLUMN IF NOT EXISTS `PmMultiplier` INT NULL;

        -- Locations (airport codes) ──────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `Locations` (
            `Id`                   INT         NOT NULL AUTO_INCREMENT,
            `Name`                 LONGTEXT    NOT NULL,
            `GeographicalLocation` LONGTEXT    NOT NULL,
            `LastModified`         DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                   ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Sites ──────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `Sites` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `LocationId`   INT         NOT NULL,
            `AssignedPmId` INT         NULL,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_Sites_Locations`
                FOREIGN KEY (`LocationId`) REFERENCES `Locations`(`Id`) ON DELETE CASCADE,
            CONSTRAINT `FK_Sites_PMs`
                FOREIGN KEY (`AssignedPmId`) REFERENCES `PMs`(`Id`) ON DELETE SET NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- ROMP statuses (configurable) ───────────────────────────────────
        CREATE TABLE IF NOT EXISTS `RompStatuses` (
            `Id`             INT         NOT NULL AUTO_INCREMENT,
            `Name`           LONGTEXT    NOT NULL,
            `IsComplete`     TINYINT(1)  NOT NULL DEFAULT 0,
            `CountsAsActive` TINYINT(1)  NOT NULL DEFAULT 0,
            `ScoreWeight`    DOUBLE      NOT NULL DEFAULT 0,
            `SortOrder`      INT         NOT NULL DEFAULT 0,
            `IsBuiltIn`      TINYINT(1)  NOT NULL DEFAULT 0,
            `IsDisabled`     TINYINT(1)  NOT NULL DEFAULT 0,
            `Color`          VARCHAR(9)  NOT NULL DEFAULT '#94A3B8',
            `LastModified`   DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                             ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- ROMPs ──────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `Romps` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `SiteId`       INT         NOT NULL,
            `Status`       LONGTEXT    NOT NULL,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_Romps_Sites`
                FOREIGN KEY (`SiteId`) REFERENCES `Sites`(`Id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Equipment categories ───────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `EquipmentCategories` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `Weight`       DOUBLE      NOT NULL DEFAULT 0,
            `IsExcluded`   TINYINT(1)  NOT NULL DEFAULT 0,
            `IsBuiltIn`    TINYINT(1)  NOT NULL DEFAULT 0,
            `SortOrder`    INT         NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Equipment keywords ─────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `EquipmentKeywords` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `CategoryId`   INT         NOT NULL,
            `Keyword`      LONGTEXT    NOT NULL,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_EquipmentKeywords_Categories`
                FOREIGN KEY (`CategoryId`) REFERENCES `EquipmentCategories`(`Id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Equipment weights (legacy, kept for schema compat) ─────────────
        CREATE TABLE IF NOT EXISTS `EquipmentWeights` (
            `Id`        INT        NOT NULL AUTO_INCREMENT,
            `Name`      LONGTEXT   NOT NULL,
            `Keyword`   LONGTEXT   NOT NULL,
            `Weight`    INT        NOT NULL DEFAULT 0,
            `IsExcluded` TINYINT(1) NOT NULL DEFAULT 0,
            `IsBuiltIn`  TINYINT(1) NOT NULL DEFAULT 0,
            `SortOrder`  INT        NOT NULL DEFAULT 0,
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- ROMP ↔ Equipment junction ───────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `RompEquipmentTypes` (
            `Id`                  INT         NOT NULL AUTO_INCREMENT,
            `RompId`              INT         NOT NULL,
            `EquipmentCategoryId` INT         NOT NULL,
            `LastModified`        DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                  ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            UNIQUE KEY `UQ_RompEquipmentType` (`RompId`, `EquipmentCategoryId`),
            CONSTRAINT `FK_RompEquipmentTypes_Romps`
                FOREIGN KEY (`RompId`) REFERENCES `Romps`(`Id`) ON DELETE CASCADE,
            CONSTRAINT `FK_RompEquipmentTypes_Categories`
                FOREIGN KEY (`EquipmentCategoryId`) REFERENCES `EquipmentCategories`(`Id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Assignment history — survives reassignment and site closure; written only by
        -- AssignmentHistoryService ─────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `AssignmentHistories` (
            `Id`              INT         NOT NULL AUTO_INCREMENT,
            `SiteId`          INT         NOT NULL,
            `PmId`            INT         NOT NULL,
            `AssignedAtUtc`   DATETIME(3) NOT NULL,
            `UnassignedAtUtc` DATETIME(3) NULL,
            `AssignedBy`      LONGTEXT    NOT NULL,
            `UnassignedBy`    LONGTEXT    NULL,
            `LastModified`    DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                              ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_AssignmentHistories_Sites`
                FOREIGN KEY (`SiteId`) REFERENCES `Sites`(`Id`) ON DELETE CASCADE,
            CONSTRAINT `FK_AssignmentHistories_PMs`
                FOREIGN KEY (`PmId`) REFERENCES `PMs`(`Id`) ON DELETE RESTRICT
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Forecast Planning ─────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `ForecastTypes` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Code`         LONGTEXT    NOT NULL,
            `Name`         LONGTEXT    NOT NULL,
            `SortOrder`    INT         NOT NULL DEFAULT 0,
            `IsBuiltIn`    TINYINT(1)  NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        CREATE TABLE IF NOT EXISTS `ForecastFields` (
            `Id`             INT         NOT NULL AUTO_INCREMENT,
            `ForecastTypeId` INT         NULL,
            `Name`           LONGTEXT    NOT NULL,
            `Key`            VARCHAR(50) NOT NULL,
            `FieldType`      VARCHAR(20) NOT NULL DEFAULT 'Text',
            `IsSiteKey`      TINYINT(1)  NOT NULL DEFAULT 0,
            `IsIgnored`      TINYINT(1)  NOT NULL DEFAULT 0,
            `SortOrder`      INT         NOT NULL DEFAULT 0,
            `LastModified`   DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                             ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_ForecastFields_Types`
                FOREIGN KEY (`ForecastTypeId`) REFERENCES `ForecastTypes`(`Id`) ON DELETE RESTRICT
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        CREATE TABLE IF NOT EXISTS `ForecastHeaderMappings` (
            `Id`              INT         NOT NULL AUTO_INCREMENT,
            `ForecastTypeId`  INT         NOT NULL,
            `SourceHeader`    LONGTEXT    NOT NULL,
            `ColumnIndex`     INT         NOT NULL DEFAULT -1,
            `ForecastFieldId` INT         NOT NULL,
            `LastModified`    DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                              ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_ForecastHeaderMappings_Types`
                FOREIGN KEY (`ForecastTypeId`) REFERENCES `ForecastTypes`(`Id`) ON DELETE CASCADE,
            CONSTRAINT `FK_ForecastHeaderMappings_Fields`
                FOREIGN KEY (`ForecastFieldId`) REFERENCES `ForecastFields`(`Id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        CREATE TABLE IF NOT EXISTS `ForecastImports` (
            `Id`             INT         NOT NULL AUTO_INCREMENT,
            `ForecastTypeId` INT         NOT NULL,
            `FileName`       LONGTEXT    NOT NULL,
            `UploadedBy`     LONGTEXT    NOT NULL,
            `UploadedAtUtc`  DATETIME(3) NOT NULL,
            `RowCount`       INT         NOT NULL DEFAULT 0,
            `LastModified`   DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                             ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_ForecastImports_Types`
                FOREIGN KEY (`ForecastTypeId`) REFERENCES `ForecastTypes`(`Id`) ON DELETE RESTRICT
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        CREATE TABLE IF NOT EXISTS `ForecastRows` (
            `Id`               INT         NOT NULL AUTO_INCREMENT,
            `ForecastImportId` INT         NOT NULL,
            `ForecastTypeId`   INT         NOT NULL,
            `SiteCode`         LONGTEXT    NULL,
            `SiteId`           INT         NULL,
            `ValuesJson`       LONGTEXT    NOT NULL,
            `LastModified`     DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                               ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_ForecastRows_Imports`
                FOREIGN KEY (`ForecastImportId`) REFERENCES `ForecastImports`(`Id`) ON DELETE CASCADE,
            CONSTRAINT `FK_ForecastRows_Types`
                FOREIGN KEY (`ForecastTypeId`) REFERENCES `ForecastTypes`(`Id`) ON DELETE RESTRICT,
            CONSTRAINT `FK_ForecastRows_Sites`
                FOREIGN KEY (`SiteId`) REFERENCES `Sites`(`Id`) ON DELETE SET NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Assignment rules ───────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `AssignmentRules` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `Description`  LONGTEXT    NOT NULL,
            `RuleKey`      LONGTEXT    NOT NULL,
            `Weight`       INT         NOT NULL DEFAULT 1,
            `IsActive`     TINYINT(1)  NOT NULL DEFAULT 1,
            `SortOrder`    INT         NOT NULL DEFAULT 0,
            `IsBuiltIn`    TINYINT(1)  NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Orders (imported from VADATA / Tableau / Open Lines) ───────────
        CREATE TABLE IF NOT EXISTS `Orders` (
            `Id`                            INT          NOT NULL AUTO_INCREMENT,
            `CustomerPO`                    VARCHAR(255) NULL,
            `CustPOLine`                    VARCHAR(255) NULL,
            `ShipHold`                      VARCHAR(255) NULL,
            `FaceSite`                      VARCHAR(255) NULL,
            `CustomerOrder`                 VARCHAR(255) NULL,
            `OrdLine`                       VARCHAR(255) NULL,
            `AssignSite`                    VARCHAR(255) NULL,
            `AssignWhse`                    VARCHAR(255) NULL,
            `JobName`                       VARCHAR(500) NULL,
            `IPN`                           VARCHAR(255) NULL,
            `Item`                          VARCHAR(255) NULL,
            `ItemDescription`               TEXT         NULL,
            `ItemDesignation`               VARCHAR(255) NULL,
            `QtyOrd`                        DOUBLE       NULL,
            `QtyShip`                       DOUBLE       NULL,
            `ShipDate`                      DATETIME     NULL,
            `DeliveryDate`                  DATETIME     NULL,
            `WarrantyExpiration`            DATETIME     NULL,
            `EstimatedDeliveryDate`         DATETIME     NULL,
            `EstimatedCompletionDate`       DATETIME     NULL,
            `ActualCompletionDate`          DATETIME     NULL,
            `RequestedCompletion`           DATETIME     NULL,
            `ProductionVariance`            VARCHAR(255) NULL,
            `RequestedOSD`                  VARCHAR(255) NULL,
            `DaysLate`                      INT          NULL,
            `PreviousEstCompletionDate`     DATETIME     NULL,
            `EstCompletionDateChange`       DATETIME     NULL,
            `ScheduledStartDate`            DATETIME     NULL,
            `ActualStartDate`               DATETIME     NULL,
            `InvoiceDate`                   DATETIME     NULL,
            `CustNum`                       VARCHAR(255) NULL,
            `OrderShipTo`                   VARCHAR(255) NULL,
            `LineItemShipTo`                VARCHAR(255) NULL,
            `CreditHold`                    VARCHAR(255) NULL,
            `Slsman`                        VARCHAR(255) NULL,
            `OrderStatus`                   VARCHAR(255) NULL,
            `ProductCode`                   VARCHAR(255) NULL,
            `Price`                         DOUBLE       NULL,
            `ExtPrice`                      DOUBLE       NULL,
            `SectionCount`                  INT          NULL,
            `EstCompletionDateChangeReason` TEXT         NULL,
            `OrderDate`                     DATETIME     NULL,
            `OrderLineStatus`               VARCHAR(255) NULL,
            `PromiseDate`                   DATETIME     NULL,
            `LastShipDate`                  DATETIME     NULL,
            `QtyInvoiced`                   DOUBLE       NULL,
            `SalesTax1`                     VARCHAR(255) NULL,
            `SalesTax2`                     VARCHAR(255) NULL,
            `TotalCOPrice`                  DOUBLE       NULL,
            `ProjectManagerDataName`        VARCHAR(255) NULL,
            `CustomerName`                  VARCHAR(255) NULL,
            `UploadDate`                    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
            `LastModified`                  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                            ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Saved Reports (Report Builder presets) ──────────────────────────
        CREATE TABLE IF NOT EXISTS `SavedReports` (
            `Id`                  INT          NOT NULL AUTO_INCREMENT,
            `Name`                LONGTEXT     NOT NULL,
            `Mode`                VARCHAR(50)  NOT NULL,
            `FilterJson`          LONGTEXT     NOT NULL,
            `SelectedColumnsJson` LONGTEXT     NOT NULL,
            `ChartGroupByField`   VARCHAR(255) NULL,
            `ChartAggregateField` VARCHAR(255) NULL,
            `ChartAggregateOp`    VARCHAR(50)  NOT NULL DEFAULT 'Count',
            `LastModified`        DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                  ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Custom Data grid (user-defined dynamic columns) ─────────────────
        CREATE TABLE IF NOT EXISTS `CustomColumns` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `Mode`         VARCHAR(50)  NOT NULL DEFAULT 'Orders',
            `Name`         LONGTEXT     NOT NULL,
            `Key`          VARCHAR(255) NOT NULL,
            `ColumnType`   VARCHAR(50)  NOT NULL DEFAULT 'Text',
            `SortOrder`    INT          NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Custom Data rows attach to a real Order/Romp by (Mode, EntityId) rather than
        -- standing alone — see CustomRow.cs.
        CREATE TABLE IF NOT EXISTS `CustomRows` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `Mode`         VARCHAR(50)  NOT NULL DEFAULT 'Orders',
            `EntityId`     INT          NOT NULL DEFAULT 0,
            `ValuesJson`   LONGTEXT     NOT NULL,
            `SortOrder`    INT          NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Users / AuditEntries — accounts and the append-only interaction log ───
        CREATE TABLE IF NOT EXISTS `Users` (
            `Id`                 INT          NOT NULL AUTO_INCREMENT,
            `Email`              VARCHAR(255) NOT NULL,
            `FirstName`          LONGTEXT     NOT NULL,
            `LastName`           LONGTEXT     NOT NULL,
            `PasswordHash`       LONGTEXT     NOT NULL,
            `Role`               VARCHAR(50)  NOT NULL DEFAULT 'User',
            `IsActive`           TINYINT(1)   NOT NULL DEFAULT 1,
            `MustChangePassword` TINYINT(1)   NOT NULL DEFAULT 0,
            `LastLoginUtc`       DATETIME     NULL,
            `LastModified`       DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                 ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        CREATE TABLE IF NOT EXISTS `AuditEntries` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `TimestampUtc` DATETIME     NOT NULL,
            `UserEmail`    VARCHAR(255) NOT NULL DEFAULT '',
            `UserName`     VARCHAR(255) NOT NULL DEFAULT '',
            `Action`       VARCHAR(100) NOT NULL DEFAULT '',
            `EntityType`   VARCHAR(100) NOT NULL DEFAULT '',
            `EntityId`     INT          NULL,
            `Details`      TEXT         NOT NULL,
            `MachineName`  VARCHAR(255) NOT NULL DEFAULT '',
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- LastModifiedBy — who last wrote the row (Orders/Romps/Sites only)
        ALTER TABLE `Orders` ADD COLUMN IF NOT EXISTS `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '';
        ALTER TABLE `Romps`  ADD COLUMN IF NOT EXISTS `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '';
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '';

        -- IsClosed — explicit close flag for a Site (see Site.cs; needed for zero-ROMP sites)
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `IsClosed` TINYINT(1) NOT NULL DEFAULT 0;

        -- StatusWhenEmpty — stand-in status for a zero-ROMP campus, set via "Set All ROMPs to
        -- Status" when there are no ROMPs to actually carry it (see Site.cs).
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `StatusWhenEmpty` LONGTEXT NULL;

        -- Campuses: Cluster → Campus → Site → ROMP (SAT also creates these itself on startup —
        -- see CloudSyncService.EnsureCloudSchema).
        CREATE TABLE IF NOT EXISTS `Campuses` (
            `Id`             INT          NOT NULL AUTO_INCREMENT,
            `Name`           VARCHAR(255) NOT NULL,
            `LocationId`     INT          NOT NULL,
            `AssignedPmId`   INT          NULL,
            `LastModified`   DATETIME(6)  NOT NULL DEFAULT '2000-01-01 00:00:00',
            `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '',
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `CampusId` INT NULL;
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `CampusManual` TINYINT(1) NOT NULL DEFAULT 0;
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `PmOverride` TINYINT(1) NOT NULL DEFAULT 0;
        ALTER TABLE `ForecastFields` ADD COLUMN IF NOT EXISTS `IsCampusField` TINYINT(1) NOT NULL DEFAULT 0;

        -- Teams: Director › PE (sub-region) › PM (PMs.PeId) › PA; clusters map to a sub-region
        -- (Locations.SubRegionId). SAT also creates these itself on startup.
        CREATE TABLE IF NOT EXISTS `SubRegions` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `Name`         VARCHAR(255) NOT NULL,
            `Region`       VARCHAR(64)  NOT NULL DEFAULT '',
            `SortOrder`    INT          NOT NULL DEFAULT 0,
            `LastModified` DATETIME(6)  NOT NULL DEFAULT '2000-01-01 00:00:00',
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS `TeamMembers` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `Role`         VARCHAR(32)  NOT NULL,
            `FirstName`    VARCHAR(255) NOT NULL DEFAULT '',
            `LastName`     VARCHAR(255) NOT NULL DEFAULT '',
            `Email`        VARCHAR(255) NOT NULL DEFAULT '',
            `Phone`        VARCHAR(64)  NOT NULL DEFAULT '',
            `Region`       VARCHAR(64)  NOT NULL DEFAULT '',
            `SubRegionId`  INT          NULL,
            `PmId`         INT          NULL,
            `IsActive`     TINYINT(1)   NOT NULL DEFAULT 1,
            `LastModified` DATETIME(6)  NOT NULL DEFAULT '2000-01-01 00:00:00',
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        ALTER TABLE `PMs`       ADD COLUMN IF NOT EXISTS `PeId` INT NULL;
        ALTER TABLE `Locations` ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL;
        ALTER TABLE `Campuses`  ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL;
        ALTER TABLE `Sites`     ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL;
        ALTER TABLE `PMs`       ADD COLUMN IF NOT EXISTS `IsPlaceholder` TINYINT(1) NOT NULL DEFAULT 0;
        ALTER TABLE `TeamMembers` ADD COLUMN IF NOT EXISTS `IsPlaceholder` TINYINT(1) NOT NULL DEFAULT 0;
        ALTER TABLE `PMs`       ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL;
        ALTER TABLE `PMs`       ADD COLUMN IF NOT EXISTS `OtherSubRegionIds` VARCHAR(1000) NOT NULL DEFAULT '';

        -- Location.AssignedPmId — direct "owner" PM for a cluster with no campuses to carry a
        -- per-Site assignment instead (see Location.cs).
        ALTER TABLE `Locations` ADD COLUMN IF NOT EXISTS `AssignedPmId` INT NULL;

        -- Romp.AssignedPmId — individual-ROMP PM override (see AssignmentResolver); and
        -- AssignmentHistories.RompId so the same history table records ROMP-level overrides
        -- alongside campus-level ones (see AssignmentHistoryService.RecordRompChange).
        ALTER TABLE `Romps` ADD COLUMN IF NOT EXISTS `AssignedPmId` INT NULL;
        ALTER TABLE `AssignmentHistories` ADD COLUMN IF NOT EXISTS `RompId` INT NULL;

        -- RompStatuses.IsDisabled — hides a status from "assign a status" pickers without
        -- touching ROMPs already on it (StatusConfig.Selectable vs .All).
        ALTER TABLE `RompStatuses` ADD COLUMN IF NOT EXISTS `IsDisabled` TINYINT(1) NOT NULL DEFAULT 0;

        -- RompStatuses.Color — indicator-dot color for Campus/ROMP lists (Configuration page
        -- color picker). See Color's doc on the RompStatus model for the seeding story; this
        -- ALTER just adds the column, the app seeds real values from local SQLite on next sync.
        ALTER TABLE `RompStatuses` ADD COLUMN IF NOT EXISTS `Color` VARCHAR(9) NOT NULL DEFAULT '#94A3B8';

        -- ================================================================
        -- Done. Connect the app using:
        --   Server=YOUR_HOST; Port=3306; Database=pmdashboard;
        --   User=YOUR_USER; Password=YOUR_PASSWORD; AllowPublicKeyRetrieval=true;
        -- The app will seed built-in status / equipment / rule data on first launch.
        -- ================================================================
        """;
}
