using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Gives every Cloud-mode machine its own block of primary-key Ids, so two machines creating
/// rows in the same table at the same time can't both produce the same Id.
///
/// Why: every machine writes to its own local SQLite first, where autoincrement just takes
/// "highest Id + 1". Two machines that are in sync therefore hand out the SAME next Id, and
/// CloudSyncService matches rows by Id — so one machine's new row silently overwrote or was
/// skipped in favour of the other's (seen 2026-10-05: AuditEntry 1294 was a Login on one PC and
/// an AutoLogin on the other). Audit rows are low-stakes, but the same thing can happen to new
/// clusters, campuses, ROMPs, assignment history, etc.
///
/// How: the first time a machine syncs with a cloud database it claims a slot number N by
/// inserting AppConfig row "IdSlot.N" directly into the cloud — AppConfigs.Key is the primary
/// key, so the insert is atomic and only one machine can ever win a given N. From then on, new
/// rows created on that machine get Ids in [N × RangeSize, (N+1) × RangeSize). Ids below
/// RangeSize (everything created before this existed) are left untouched.
///
/// Assignment happens when EF starts tracking an entity as Added (AppDbContext hooks
/// ChangeTracker.Tracked), before SaveChanges and before any dependent rows pick up its key via
/// navigation fix-up. SQLite's own autoincrement can't be used for this: inserting a pulled row
/// with another machine's (higher) Id moves SQLite's counter into that machine's range.
///
/// Local and Network modes don't sync by Id with other machines, so they keep plain autoincrement.
/// </summary>
public static class IdAllocator
{
    /// <summary>Ids per machine. int.MaxValue / 20M ≈ 107 slots — far more machines than this
    /// app will ever have; 20M per table per machine leaves room for repeated large imports
    /// (Orders is ~134k rows, ForecastRows churns ~18k per import).</summary>
    public const int RangeSize = 20_000_000;
    private const int MaxSlot  = int.MaxValue / RangeSize - 1;
    private const string SlotKeyPrefix = "IdSlot.";

    // RompEquipmentType is matched by its natural key (RompId, EquipmentCategoryId) during sync,
    // never by Id (see CloudSyncService.SafeSyncRompEquipmentTypes), so it doesn't need a range.
    private static readonly HashSet<Type> Excluded = new() { typeof(RompEquipmentType) };

    private static readonly object _gate = new();
    private static readonly Dictionary<(Type, int), int> _next = new(); // (entity, slot) → next Id

    /// <summary>The slot in effect for this run, or null (plain autoincrement).</summary>
    public static int? Slot { get; private set; }

    /// <summary>Called at startup. Deliberately does NOT activate the slot saved in
    /// dbconfig.json yet — until <see cref="EnsureSlotClaimed"/> has confirmed against the cloud
    /// that the slot really is this machine's, new rows use the legacy range (slot 0). A saved
    /// slot is not proof of ownership: on 2026-10-05 a bug sent every "cloud" write to local
    /// SQLite, so two machines each saved a "claim" of slot 1 that never reached the cloud.</summary>
    public static void InitFromConfig()
    {
        Slot = null;
        ResetCounters();
        // Network mode: a slot claimed in this same shared file (EnsureNetworkSlotClaimed, under
        // its write lock) is trusted straight away, so a computer that starts offline still
        // creates rows in its own range. Re-verified on the first sync.
        if (DatabaseConfig.UsesLocalCache && DatabaseConfig.IdSlot is { } s
            && DatabaseConfig.IdSlotCloud == "net:" + DatabaseConfig.NetworkDbPath.Trim().ToLowerInvariant())
            Slot = s;
    }

    /// <summary>
    /// Ensures this machine holds a slot in the current cloud database: keeps the saved slot if
    /// the cloud's "IdSlot.N" row names this machine, otherwise claims one (preferring the saved
    /// number if it's still free). Runs on the sync thread after the initial pull. Throws on
    /// connection failure — the caller logs it and retries on a later sync cycle.
    /// </summary>
    public static void EnsureSlotClaimed()
    {
        if (!DatabaseConfig.IsCloud) return;
        var identity = CloudIdentity();
        int? saved = DatabaseConfig.IdSlotCloud == identity ? DatabaseConfig.IdSlot : null;

        using var cloud = new AppDbContext(forceCloud: true) { SkipLastModifiedStamp = true };
        var slotRows = cloud.AppConfigs.AsNoTracking()
                            .Where(c => c.Key.StartsWith(SlotKeyPrefix))
                            .ToList();

        RemoveLocalOnlyMachineRows(cloud);

        if (saved is { } s
            && slotRows.FirstOrDefault(r => r.Key == SlotKeyPrefix + s) is { } mine
            && mine.Value.StartsWith(Environment.MachineName + " ", StringComparison.OrdinalIgnoreCase))
        {
            Slot = s;
            ResetCounters();
            return;
        }

        var taken = slotRows
            .Select(r => int.TryParse(r.Key[SlotKeyPrefix.Length..], out var n) ? n : 0)
            .ToHashSet();

        // Try the saved number first (keeps any rows this machine already made in that range
        // valid), then the lowest free one.
        var candidates = (saved is { } sv ? new[] { sv } : Array.Empty<int>())
            .Concat(Enumerable.Range(1, MaxSlot))
            .Distinct();

        foreach (var n in candidates)
        {
            if (taken.Contains(n)) continue;
            var row = new AppConfig
            {
                Key          = SlotKeyPrefix + n,
                Value        = $"{Environment.MachineName} ({CurrentUser.Email}) claimed {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC",
                LastModified = DateTime.UtcNow,
            };
            cloud.AppConfigs.Add(row);
            try
            {
                cloud.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // Another machine claimed this N between our read and our insert — try the next.
                cloud.Entry(row).State = EntityState.Detached;
                continue;
            }

            // Lost the saved slot to another machine → this machine's unsynced rows in that old
            // range would collide with that machine's future rows. Move them into the new range.
            if (saved is { } old && old != n)
                RenumberUnsyncedRows(cloud, old, n);

            DatabaseConfig.SetIdSlot(n, identity);
            Slot = n;
            ResetCounters();
            return;
        }
        throw new InvalidOperationException("No free Id slot left in the cloud database.");
    }

    /// <summary>
    /// Network mode (NetworkSync): the same claim, made in the shared SQLite file attached as
    /// <paramref name="netSchema"/> on <paramref name="conn"/> — called inside NetworkSync's locked
    /// transaction, so the "is N free → insert IdSlot.N" check can't race another computer.
    /// </summary>
    public static void EnsureNetworkSlotClaimed(Microsoft.Data.Sqlite.SqliteConnection conn, string netSchema, string networkPath)
    {
        var identity = "net:" + networkPath.Trim().ToLowerInvariant();
        int? saved = DatabaseConfig.IdSlotCloud == identity ? DatabaseConfig.IdSlot : null;

        var taken = new Dictionary<int, string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT \"Key\", Value FROM {netSchema}.AppConfigs WHERE \"Key\" LIKE '{SlotKeyPrefix}%'";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                if (int.TryParse(rd.GetString(0)[SlotKeyPrefix.Length..], out var n)) taken[n] = rd.IsDBNull(1) ? "" : rd.GetString(1);
        }

        if (saved is { } s && taken.TryGetValue(s, out var owner)
            && owner.StartsWith(Environment.MachineName + " ", StringComparison.OrdinalIgnoreCase))
        {
            Slot = s;
            ResetCounters();
            return;
        }

        var n2 = (saved is { } sv && !taken.ContainsKey(sv) ? new[] { sv } : Array.Empty<int>())
            .Concat(Enumerable.Range(1, MaxSlot)).First(x => !taken.ContainsKey(x));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO {netSchema}.AppConfigs (\"Key\", Value, LastModified) VALUES (@k, @v, @t)";
            cmd.Parameters.AddWithValue("@k", SlotKeyPrefix + n2);
            cmd.Parameters.AddWithValue("@v", $"{Environment.MachineName} ({CurrentUser.Email}) claimed {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF"));
            cmd.ExecuteNonQuery();
        }
        DatabaseConfig.SetIdSlot(n2, identity);
        Slot = n2;
        ResetCounters();
    }

    /// <summary>
    /// Deletes local AppConfig "IdSlot.*"/"ClientVersion.*" rows the cloud doesn't have — left
    /// behind by the 2026-10-05 bug where those cloud writes landed in local SQLite. Harmless
    /// once real ones exist, but they'd mislead the Versions-in-use list and a Force Push would
    /// upload them as bogus claims.
    /// </summary>
    private static void RemoveLocalOnlyMachineRows(AppDbContext cloud)
    {
        string[] prefixes = { SlotKeyPrefix, "ClientVersion." };
        var cloudKeys = cloud.AppConfigs.AsNoTracking()
                             .Where(c => c.Key.StartsWith(SlotKeyPrefix) || c.Key.StartsWith("ClientVersion."))
                             .Select(c => c.Key)
                             .ToHashSet();
        using var local = new AppDbContext { SkipLastModifiedStamp = true };
        var strays = local.AppConfigs
                          .Where(c => c.Key.StartsWith(SlotKeyPrefix) || c.Key.StartsWith("ClientVersion."))
                          .ToList()
                          .Where(c => !cloudKeys.Contains(c.Key))
                          .ToList();
        if (strays.Count == 0) return;
        local.AppConfigs.RemoveRange(strays);
        local.SaveChanges();
    }

    /// <summary>
    /// Moves this machine's local-only rows (not present in the cloud) with Ids in
    /// <paramref name="oldSlot"/>'s range into <paramref name="newSlot"/>'s range, rewriting
    /// every foreign key that points at them. Rows that exist in the cloud are left alone — they
    /// belong to whichever machine really owns that range. Local SQLite only, one transaction.
    /// </summary>
    private static void RenumberUnsyncedRows(AppDbContext cloud, int oldSlot, int newSlot)
    {
        long oldLo = (long)oldSlot * RangeSize, oldHi = oldLo + RangeSize;
        using var local = new AppDbContext { SkipLastModifiedStamp = true };
        var model = local.Model;
        var conn  = local.Database.GetDbConnection();
        local.Database.OpenConnection();
        try
        {
            using (var off = conn.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys = OFF"; off.ExecuteNonQuery(); }
            using var tx = conn.BeginTransaction();

            foreach (var et in model.GetEntityTypes())
            {
                var key = et.FindPrimaryKey();
                if (key is not { Properties.Count: 1 } || key.Properties[0].ClrType != typeof(int)) continue;
                var table = et.GetTableName();
                if (table == null) continue;

                var localIds = new List<int>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\" >= {oldLo} AND \"Id\" < {oldHi} ORDER BY \"Id\"";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) localIds.Add(rd.GetInt32(0));
                }
                if (localIds.Count == 0) continue;

                // Which of those the cloud already has (another machine's, legitimately).
                var inCloud = new HashSet<int>();
                try
                {
                    var cc = cloud.Database.GetDbConnection();
                    if (cc.State != System.Data.ConnectionState.Open) cloud.Database.OpenConnection();
                    using var cmd = cc.CreateCommand();
                    cmd.CommandText = $"SELECT `Id` FROM `{table}` WHERE `Id` >= {oldLo} AND `Id` < {oldHi}";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) inCloud.Add(rd.GetInt32(0));
                }
                catch { continue; } // table missing in cloud etc. — leave it alone, don't guess

                var toMove = localIds.Where(id => !inCloud.Contains(id)).ToList();
                if (toMove.Count == 0) continue;

                // Next free Id in the new range (local max, then count up).
                int next;
                using (var cmd = conn.CreateCommand())
                {
                    long lo = (long)newSlot * RangeSize, hi = lo + RangeSize;
                    cmd.Transaction = tx;
                    cmd.CommandText = $"SELECT MAX(\"Id\") FROM \"{table}\" WHERE \"Id\" >= {lo} AND \"Id\" < {hi}";
                    var r = cmd.ExecuteScalar();
                    next = r is null or DBNull ? (int)lo + 1 : Convert.ToInt32(r) + 1;
                }

                // Every FK column (in any table) that references this table's Id.
                var refs = model.GetEntityTypes()
                    .SelectMany(d => d.GetForeignKeys())
                    .Where(fk => fk.PrincipalEntityType == et && fk.Properties.Count == 1)
                    .Select(fk => (Table: fk.DeclaringEntityType.GetTableName(), Column: fk.Properties[0].GetColumnName()))
                    .Where(x => x.Table != null)
                    .ToList();

                foreach (var oldId in toMove)
                {
                    var newId = next++;
                    Exec(conn, tx, $"UPDATE \"{table}\" SET \"Id\" = {newId} WHERE \"Id\" = {oldId}");
                    foreach (var (refTable, col) in refs)
                        Exec(conn, tx, $"UPDATE \"{refTable}\" SET \"{col}\" = {newId} WHERE \"{col}\" = {oldId}");
                }
            }
            tx.Commit();
        }
        finally
        {
            using (var on = conn.CreateCommand()) { on.CommandText = "PRAGMA foreign_keys = ON"; on.ExecuteNonQuery(); }
            local.Database.CloseConnection();
        }

        static void Exec(System.Data.Common.DbConnection c, System.Data.Common.DbTransaction t, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Forgets cached "next Id" values so they're re-read from the local DB. Called
    /// after the initial pull, which may bring down rows this machine created in an earlier
    /// install (e.g. after data.db was deleted) that the cached counters didn't know about.</summary>
    public static void ResetCounters()
    {
        lock (_gate) _next.Clear();
    }

    /// <summary>ChangeTracker.Tracked handler — see the class doc.</summary>
    internal static void OnTracked(AppDbContext ctx, EntityTrackedEventArgs e)
    {
        if (e.FromQuery || e.Entry.State != EntityState.Added) return;
        // No slot yet (first launch, before the claim completes) → the legacy range below
        // RangeSize, in Cloud mode only. Leaving it to SQLite autoincrement instead would hand
        // out "highest Id + 1", which lands inside another machine's range once any of its rows
        // have been pulled. Local/Network modes keep plain autoincrement — Network mode shares
        // one SQLite file between machines, where an in-memory counter would be the bug.
        // Network mode works on a local copy synced by Id too (NetworkSync), so it needs ranges
        // exactly like Cloud mode did.
        int slot;
        if (Slot is { } claimed) slot = claimed;
        else if (DatabaseConfig.IsCloud || DatabaseConfig.UsesLocalCache) slot = 0;
        else return;

        var type = e.Entry.Metadata.ClrType;
        // NetworkSync matches every table by Id (no natural-key path), so nothing is excluded there.
        if (DatabaseConfig.IsCloud && Excluded.Contains(type)) return;

        var key = e.Entry.Metadata.FindPrimaryKey();
        if (key is not { Properties.Count: 1 } || key.Properties[0].ClrType != typeof(int)) return;

        var idProp = e.Entry.Property(key.Properties[0].Name);
        // Only rows that are being created here. A row carrying an explicit Id (a pulled/merged
        // copy of another machine's row) keeps its Id.
        if (!idProp.IsTemporary && idProp.CurrentValue is int v && v > 0) return;

        idProp.CurrentValue = NextId(ctx, e.Entry.Metadata, type, slot);
        idProp.IsTemporary  = false;
    }

    private static int NextId(AppDbContext ctx, Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
                              Type clrType, int slot)
    {
        lock (_gate)
        {
            // A claimed slot's range is only ever written by this machine, so its max is read
            // once and then counted in memory. The legacy range (slot 0) is still written by
            // other machines running older builds, whose rows get pulled in at any time — so
            // it's re-read on every allocation (one indexed MAX lookup) and never trusted stale.
            bool cached = _next.TryGetValue((clrType, slot), out var next);
            if (!cached || slot == 0)
            {
                long lo = (long)slot * RangeSize, hi = lo + RangeSize;
                var table = entityType.GetTableName();
                // Interpolation is safe here: the table name comes from the EF model and lo/hi
                // are computed numbers — no user input. (A table name can't be a SQL parameter.)
#pragma warning disable EF1002
                var maxInRange = ctx.Database
                    .SqlQueryRaw<int?>($"SELECT MAX(\"Id\") AS \"Value\" FROM \"{table}\" WHERE \"Id\" >= {lo} AND \"Id\" < {hi}")
                    .AsEnumerable()
                    .FirstOrDefault();
#pragma warning restore EF1002
                var fromDb = (int)(maxInRange.HasValue ? maxInRange.Value + 1 : lo + 1);
                next = cached ? Math.Max(next, fromDb) : fromDb;
            }
            if (next >= (long)(slot + 1) * RangeSize)
                throw new InvalidOperationException($"Id range for slot {slot} exhausted in {clrType.Name}.");
            _next[(clrType, slot)] = next + 1;
            return next;
        }
    }

    /// <summary>Which cloud database a saved slot belongs to — a slot is only unique within the
    /// database it was claimed in, so pointing at a different one means claiming again.</summary>
    private static string CloudIdentity()
    {
        var parts = DatabaseConfig.CloudConnectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim().ToLowerInvariant(), kv => kv[1].Trim().ToLowerInvariant());
        string Get(params string[] names) =>
            names.Select(n => parts.GetValueOrDefault(n)).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
        return $"{Get("server", "host", "data source")}/{Get("database", "initial catalog")}";
    }
}
