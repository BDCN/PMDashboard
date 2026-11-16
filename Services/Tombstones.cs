using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;

namespace PMDashboard.Services;

/// <summary>
/// Network mode's deletion log, so Travel Sync can carry deletions across (without it, a row
/// deleted on one side while the other was offline came straight back on the next merge).
///
/// Every tracked table gets an AFTER DELETE trigger that writes (TableName, RowKey, DeletedUtc)
/// into <c>DeletedRows</c>. Triggers live inside the database file itself, so they record every
/// delete no matter where it comes from — EF, the ~40 raw-SQL/RemoveRange call sites, SQLite
/// FK cascades, even older SAT builds that know nothing about this table.
///
/// Installed at startup in Network mode (shared file, and the travel copy inherits them) and on
/// both files at the start of every Sync After Travel. Entries older than <see cref="KeepDays"/>
/// are pruned; a trip longer than that is warned about at merge time.
/// </summary>
public static class Tombstones
{
    public const string TableName = "DeletedRows";
    public const int KeepDays = 180;

    /// <summary>SQLite's "now" in the same shape/zone as LastModified (UTC, text, sortable).</summary>
    private const string NowExpr = "strftime('%Y-%m-%d %H:%M:%f', 'now')";

    /// <summary>Creates the log table and the per-table triggers in <paramref name="schema"/>
    /// (idempotent — new tables pick up their trigger on the next run), then prunes old entries.</summary>
    public static void EnsureInstalled(SqliteConnection conn, string schema = "main")
    {
        Exec(conn, $"""
            CREATE TABLE IF NOT EXISTS {schema}.{TableName} (
                TableName  TEXT NOT NULL,
                RowKey     TEXT NOT NULL,
                DeletedUtc TEXT NOT NULL,
                PRIMARY KEY (TableName, RowKey)
            ) WITHOUT ROWID
            """);
        Exec(conn, $"CREATE INDEX IF NOT EXISTS {schema}.IX_{TableName}_DeletedUtc ON {TableName} (DeletedUtc)");

        foreach (var table in SyncService.IntPkTables.Append("AppConfigs"))
        {
            if (!Exists(conn, schema, table)) continue;
            var keyCol = table == "AppConfigs" ? "\"Key\"" : "Id";
            // Unqualified names inside the body resolve to the trigger's own schema.
            Exec(conn, $"""
                CREATE TRIGGER IF NOT EXISTS {schema}."trg_tomb_{table}" AFTER DELETE ON "{table}"
                BEGIN
                    INSERT OR REPLACE INTO {TableName} (TableName, RowKey, DeletedUtc)
                    VALUES ('{table}', CAST(OLD.{keyCol} AS TEXT), {NowExpr});
                END
                """);
        }

        Exec(conn, $"DELETE FROM {schema}.{TableName} WHERE DeletedUtc < strftime('%Y-%m-%d %H:%M:%f', 'now', '-{KeepDays} days')");
    }

    /// <summary>Startup hook: installs into whatever file this EF context points at.</summary>
    public static void EnsureInstalled(AppDbContext db)
    {
        db.Database.OpenConnection();
        try { EnsureInstalled((SqliteConnection)db.Database.GetDbConnection()); }
        finally { db.Database.CloseConnection(); }
    }

    /// <summary>
    /// Applies <paramref name="from"/>'s deletions to <paramref name="to"/> (schemas on one
    /// connection, inside the caller's transaction, foreign_keys OFF). A deletion only wins over
    /// a row last changed BEFORE it — a row edited on the other side after the delete is kept
    /// (and the merge then copies it back): last writer wins, deletes included.
    /// Children follow their deleted parents the way the database itself would have done it
    /// (cascade → deleted, otherwise the FK is set to NULL), using the EF model's delete rules.
    /// </summary>
    /// <param name="sinceUtc">Only deletions at/after this time (the trip start) — older ones
    /// predate the travel copy and are already reflected on both sides.</param>
    /// <param name="maxIdPerTable">If given, only deletions of rows with Id ≤ the value per table
    /// (the travel baseline): a local row created and deleted on the trip had an Id that may
    /// belong to a different office row.</param>
    /// <returns>Rows deleted in <paramref name="to"/> (including cascaded children).</returns>
    public static int Apply(SqliteConnection conn, string from, string to,
                            string? sinceUtc, IReadOnlyDictionary<string, long>? maxIdPerTable)
    {
        if (!Exists(conn, from, TableName)) return 0;

        Exec(conn, "CREATE TEMP TABLE IF NOT EXISTS del (TableName TEXT NOT NULL, Id INTEGER NOT NULL, PRIMARY KEY (TableName, Id))");
        Exec(conn, "DELETE FROM temp.del");

        var since = sinceUtc == null ? "" : $" AND t.DeletedUtc >= '{sinceUtc}'";
        var tables = SyncService.IntPkTables.Where(t => Exists(conn, to, t)).ToList();

        // 1. The rows those deletions name, where the deletion is newer than the row.
        foreach (var table in tables)
        {
            string cap = "";
            if (maxIdPerTable != null)
            {
                if (!maxIdPerTable.TryGetValue(table, out var max)) continue;
                cap = $" AND r.Id <= {max}";
            }
            Exec(conn, $"""
                INSERT OR IGNORE INTO temp.del (TableName, Id)
                SELECT '{table}', r.Id
                FROM {from}.{TableName} t
                JOIN {to}."{table}" r ON r.Id = CAST(t.RowKey AS INTEGER)
                WHERE t.TableName = '{table}' AND t.DeletedUtc > r.LastModified{since}{cap}
                """);
        }

        // 2. Children, per the EF model's delete behaviour.
        using var ctx = new AppDbContext();
        var fks = ctx.Model.GetEntityTypes()
            .SelectMany(et => et.GetForeignKeys())
            .Where(fk => fk.Properties.Count == 1)
            .Select(fk => (
                Child:     fk.DeclaringEntityType.GetTableName(),
                Column:    fk.Properties[0].GetColumnName(),
                Parent:    fk.PrincipalEntityType.GetTableName(),
                Cascade:   fk.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.ClientCascade
                           || !fk.Properties[0].IsNullable))
            .Where(f => f.Child != null && f.Parent != null && tables.Contains(f.Child!) && tables.Contains(f.Parent!))
            .ToList();

        for (int round = 0; round < 10; round++)   // cascades are a few levels deep at most
        {
            int added = 0;
            foreach (var f in fks.Where(f => f.Cascade))
                added += Exec(conn, $"""
                    INSERT OR IGNORE INTO temp.del (TableName, Id)
                    SELECT '{f.Child}', c.Id FROM {to}."{f.Child}" c
                    WHERE c."{f.Column}" IN (SELECT Id FROM temp.del WHERE TableName = '{f.Parent}')
                    """);
            if (added == 0) break;
        }
        foreach (var f in fks.Where(f => !f.Cascade))
            Exec(conn, $"""
                UPDATE {to}."{f.Child}" SET "{f.Column}" = NULL
                WHERE "{f.Column}" IN (SELECT Id FROM temp.del WHERE TableName = '{f.Parent}')
                  AND Id NOT IN (SELECT Id FROM temp.del WHERE TableName = '{f.Child}')
                """);

        // 3. Delete. (The target's own triggers log these too, so they travel on further.)
        int deleted = 0;
        foreach (var table in tables)
            deleted += Exec(conn, $"""
                DELETE FROM {to}."{table}" WHERE Id IN (SELECT Id FROM temp.del WHERE TableName = '{table}')
                """);

        if (Exists(conn, to, "AppConfigs"))
            deleted += Exec(conn, $"""
                DELETE FROM {to}.AppConfigs
                WHERE "Key" IN (
                    SELECT r."Key" FROM {from}.{TableName} t
                    JOIN {to}.AppConfigs r ON r."Key" = t.RowKey
                    WHERE t.TableName = 'AppConfigs' AND t.DeletedUtc > r.LastModified{since})
                """);

        Exec(conn, "DROP TABLE IF EXISTS temp.del");
        return deleted;
    }

    /// <summary>Highest numeric RowKey logged for a table (so fresh Ids never reuse a deleted one).</summary>
    public static long MaxDeletedId(SqliteConnection conn, string schema, string table)
    {
        if (!Exists(conn, schema, TableName)) return 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(CAST(RowKey AS INTEGER)), 0) FROM {schema}.{TableName} WHERE TableName = '{table}'";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool Exists(SqliteConnection conn, string schema, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type = 'table' AND name = '{table}'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static int Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
}
