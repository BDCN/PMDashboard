using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Local-first cloud sync service.
///
/// The app always reads and writes local SQLite for speed.  This service keeps the
/// MariaDB cloud database in sync in the background so every user sees current data.
///
/// Sync cycle (runs every SyncIntervalSeconds):
///   1. PUSH — write local records that are newer than the cloud version to MariaDB.
///   2. PULL — write cloud records that are newer than the local version to SQLite.
///
/// Startup: a full pull from cloud → local is performed so the user's local cache
/// contains all data before they interact with the app.
///
/// After every local SaveChanges (AppDbContext.LocalSaved event), an immediate
/// lightweight push is triggered so other users see changes within seconds.
///
/// ID conflict handling: auto-increment IDs may collide when two users create records
/// offline before syncing.  This is handled by LastModified — the newer record wins.
/// The initial startup pull reduces collision probability by seeding local IDs from cloud.
/// </summary>
public sealed class CloudSyncService : IDisposable
{
    private Timer?                _timer;
    private readonly SemaphoreSlim _lock        = new(1, 1);
    private DateTime              _lastSyncUtc  = DateTime.MinValue;
    private bool                  _initialDone  = false;
    private bool                  _firstPushDone = false;

    public event Action<string>?  StatusChanged;

    // Fires from a periodic (not the initial startup) pull when it brought in rows someone
    // else changed — a short, human-readable summary for a non-distracting toast. Deliberately
    // not raised from the initial pull (that's just this machine catching up, not "news").
    public event Action<string>?  DataChanged;

    public string                 LastStatus   { get; private set; } = "Not started";
    public DateTime?              LastSyncTime { get; private set; }
    public bool                   IsRunning    { get; private set; }

    public static bool IsAvailable =>
        DatabaseConfig.IsCloud &&
        !string.IsNullOrWhiteSpace(DatabaseConfig.CloudConnectionString);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start(int intervalSeconds = 30)
    {
        if (!IsAvailable) return;
        IsRunning = true;
        Report("Connecting to cloud — initial sync…");

        // Hook immediate push on every local save
        AppDbContext.LocalSaved += OnLocalSaved;

        // Initial full pull on background thread; start timer after it finishes.
        // Re-check IsRunning before creating the timer — Stop()/Dispose() may have run while
        // the pull was still in flight (e.g. app closed mid-sync), and without this guard the
        // continuation would spin up a new Timer after shutdown already believed sync was off.
        Task.Run(async () =>
        {
            await DoInitialPullAsync();
            if (!IsRunning) return;
            _timer = new Timer(SyncTick, null,
                TimeSpan.FromSeconds(intervalSeconds),
                TimeSpan.FromSeconds(intervalSeconds));
        });
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer    = null;
        IsRunning = false;
        AppDbContext.LocalSaved -= OnLocalSaved;
        Report("Cloud sync stopped");
    }

    public void Dispose() { Stop(); _lock.Dispose(); }

    // ── Event-driven push after local save ────────────────────────────────────

    private void OnLocalSaved()
    {
        if (!_initialDone) return;
        Task.Run(async () =>
        {
            if (!await _lock.WaitAsync(0)) return;   // already syncing — skip
            try
            {
                int pushed = DoPush(_lastSyncUtc);
                if (pushed > 0) Report($"Pushed {pushed} record(s)  {DateTime.Now:HH:mm:ss}");
            }
            catch (Exception ex) { Report($"Push error: {InnerMessage(ex)}"); }
            finally { _lock.Release(); }
        });
    }

    // ── Timer tick ────────────────────────────────────────────────────────────

    private async void SyncTick(object? _)
    {
        if (!await _lock.WaitAsync(0)) return;
        try
        {
            if (!_initialDone) return;
            var since = _lastSyncUtc;
            // First push after startup sends ALL local records so that records with the
            // migration-default LastModified ('2000-01-01') are not filtered out.
            // MergeTable still checks LastModified before overwriting, so newer cloud
            // records won't be clobbered.
            int pushed = DoPush(_firstPushDone ? since : DateTime.MinValue);
            _firstPushDone = true;
            int pulled = DoPull(since);
            if (pulled > 0) ReportDataChanged(since);
            _lastSyncUtc = DateTime.UtcNow;
            LastSyncTime = DateTime.Now;
            Report($"Synced  {LastSyncTime:HH:mm:ss}  (↑{pushed} ↓{pulled})");
        }
        catch (Exception ex) { Report($"Sync error: {InnerMessage(ex)}"); }
        finally { _lock.Release(); }
    }

    // ── On-demand push (import lock flow) ───────────────────────────────────────

    /// <summary>
    /// Forces an immediate push and waits for it, so a caller (the import-lock flow) can be
    /// sure local changes reached the cloud before releasing the lock. A no-op when cloud sync
    /// isn't running (Local/Network mode, or before the initial pull has completed) — locking
    /// only exists in Cloud mode anyway. If the wait times out, this simply returns rather than
    /// wedging the caller forever; the next regular sync tick will push anything still pending.
    /// </summary>
    public async Task PushNowAndWaitAsync(TimeSpan? timeout = null)
    {
        if (!IsAvailable || !IsRunning || !_initialDone) return;

        var wait = timeout ?? TimeSpan.FromSeconds(60);
        if (!await _lock.WaitAsync(wait))
        {
            Report("Push timed out waiting for an in-progress sync — will retry on the next cycle.");
            return;
        }

        try
        {
            var since = _lastSyncUtc;
            int pushed = await Task.Run(() => DoPush(_firstPushDone ? since : DateTime.MinValue));
            _firstPushDone = true;
            _lastSyncUtc = DateTime.UtcNow;
            LastSyncTime = DateTime.Now;
            if (pushed > 0) Report($"Pushed {pushed} record(s)  {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex) { Report($"Push error: {InnerMessage(ex)}"); }
        finally { _lock.Release(); }
    }

    // ── Initial full pull ─────────────────────────────────────────────────────

    private async Task DoInitialPullAsync()
    {
        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(60))) return;
        try
        {
            using var cloud = new AppDbContext { ForceCloud = true };

            // Pull each table independently so a failure in one table doesn't block others.
            // FK-safe order: parents before children.
            InitialPullTable("PMs",                 cloud.PMs.AsNoTracking().ToList(),                db => db.PMs,                e => e.Id, e => e.LastModified);
            InitialPullTable("Locations",            cloud.Locations.AsNoTracking().ToList(),           db => db.Locations,          e => e.Id, e => e.LastModified);
            InitialPullTable("RompStatuses",         cloud.RompStatuses.AsNoTracking().ToList(),        db => db.RompStatuses,       e => e.Id, e => e.LastModified);
            InitialPullTable("AssignmentRules",      cloud.AssignmentRules.AsNoTracking().ToList(),     db => db.AssignmentRules,    e => e.Id, e => e.LastModified);
            InitialPullTable("EquipmentCategories",  cloud.EquipmentCategories.AsNoTracking().ToList(), db => db.EquipmentCategories,e => e.Id, e => e.LastModified);
            InitialPullTable("EquipmentKeywords",    cloud.EquipmentKeywords.AsNoTracking().ToList(),   db => db.EquipmentKeywords,  e => e.Id, e => e.LastModified);
            InitialPullTable("Sites",                cloud.Sites.AsNoTracking().ToList(),               db => db.Sites,              e => e.Id, e => e.LastModified);
            InitialPullTable("Romps",                cloud.Romps.AsNoTracking().ToList(),               db => db.Romps,              e => e.Id, e => e.LastModified);
            // RompEquipmentTypes uses natural key (RompId, EquipmentCategoryId) — not Id — to avoid
            // unique constraint violations when both sides have the same pair under different auto-increment Ids.
            InitialPullRompEquipmentTypes(cloud.RompEquipmentTypes.AsNoTracking().ToList());
            InitialPullTable("Orders",               cloud.Orders.AsNoTracking().ToList(),              db => db.Orders,             e => e.Id, e => e.LastModified);
            InitialPullTable("SavedReports",         cloud.SavedReports.AsNoTracking().ToList(),        db => db.SavedReports,       e => e.Id, e => e.LastModified);
            InitialPullTable("CustomColumns",        cloud.CustomColumns.AsNoTracking().ToList(),       db => db.CustomColumns,      e => e.Id, e => e.LastModified);
            InitialPullTable("CustomRows",           cloud.CustomRows.AsNoTracking().ToList(),          db => db.CustomRows,         e => e.Id, e => e.LastModified);
            InitialPullTable("Users",                cloud.Users.AsNoTracking().ToList(),               db => db.Users,              e => e.Id, e => e.LastModified);
            InitialPullTable("AuditEntries",         cloud.AuditEntries.AsNoTracking().ToList(),        db => db.AuditEntries,       e => e.Id, e => e.LastModified);

            // AppConfigs (string PK)
            try
            {
                using var local = new AppDbContext { SkipLastModifiedStamp = true };
                BatchPushConfigs(cloud.AppConfigs.AsNoTracking().ToList(), local);
                local.SaveChanges();
            }
            catch (Exception ex)
            {
                Report($"Initial pull warning (AppConfigs): {InnerMessage(ex)}");
            }

            _lastSyncUtc = DateTime.UtcNow;
            _initialDone = true;
            LastSyncTime = DateTime.Now;
            Report($"Cloud sync ready  {LastSyncTime:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Report($"Initial pull failed: {InnerMessage(ex)}");
            _initialDone = true;
        }
        finally { _lock.Release(); }
    }

    private void InitialPullTable<T>(
        string tableName,
        IReadOnlyList<T> cloudRows,
        Func<AppDbContext, DbSet<T>> getSet,
        Func<T, int> getId,
        Func<T, DateTime> getLastMod) where T : class
    {
        try
        {
            using var local = new AppDbContext { SkipLastModifiedStamp = true };
            BatchPush(cloudRows, getSet(local), local, getId, getLastMod);
            local.SaveChanges();
        }
        catch (Exception ex)
        {
            Report($"Initial pull warning ({tableName}): {InnerMessage(ex)}");
        }
    }

    private void InitialPullRompEquipmentTypes(IReadOnlyList<RompEquipmentType> cloudRows)
    {
        try
        {
            using var local = new AppDbContext { SkipLastModifiedStamp = true };
            foreach (var record in cloudRows)
            {
                bool exists = local.RompEquipmentTypes.Any(
                    x => x.RompId == record.RompId && x.EquipmentCategoryId == record.EquipmentCategoryId);
                if (!exists)
                    // A fresh entity, not the cloud row itself — this table's Id values aren't
                    // expected to align across machines (that's why natural key drives the exists
                    // check above), so reusing record.Id risks colliding with an unrelated local
                    // row that already occupies that Id, which throws a UNIQUE constraint error.
                    local.RompEquipmentTypes.Add(new RompEquipmentType
                    {
                        RompId              = record.RompId,
                        EquipmentCategoryId = record.EquipmentCategoryId,
                        LastModified        = record.LastModified,
                    });
            }
            local.SaveChanges();
        }
        catch (Exception ex)
        {
            Report($"Initial pull warning (RompEquipmentTypes): {InnerMessage(ex)}");
        }
    }

    private static string InnerMessage(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return inner == ex ? ex.Message : $"{ex.Message} → {inner.Message}";
    }

    // ── Incremental push (local → cloud) ──────────────────────────────────────

    private int DoPush(DateTime since)
    {
        using var local = new AppDbContext();
        using var cloud = new AppDbContext { ForceCloud = true, SkipLastModifiedStamp = true };
        int total = 0;

        total += BatchPush(Since(local.PMs,                since, e => e.LastModified), cloud.PMs,                cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.Locations,           since, e => e.LastModified), cloud.Locations,          cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.RompStatuses,        since, e => e.LastModified), cloud.RompStatuses,       cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.AssignmentRules,     since, e => e.LastModified), cloud.AssignmentRules,    cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.EquipmentCategories, since, e => e.LastModified), cloud.EquipmentCategories,cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.EquipmentKeywords,   since, e => e.LastModified), cloud.EquipmentKeywords,  cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.Sites,               since, e => e.LastModified), cloud.Sites,              cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.Romps,               since, e => e.LastModified), cloud.Romps,              cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.RompEquipmentTypes,  since, e => e.LastModified), cloud.RompEquipmentTypes, cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.Orders,              since, e => e.LastModified), cloud.Orders,             cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.SavedReports,        since, e => e.LastModified), cloud.SavedReports,       cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.CustomColumns,       since, e => e.LastModified), cloud.CustomColumns,      cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.CustomRows,          since, e => e.LastModified), cloud.CustomRows,         cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.Users,               since, e => e.LastModified), cloud.Users,              cloud, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(local.AuditEntries,        since, e => e.LastModified), cloud.AuditEntries,       cloud, e => e.Id, e => e.LastModified);
        total += BatchPushConfigs(Since(local.AppConfigs, since, c => c.LastModified), cloud);

        if (total > 0) cloud.SaveChanges();
        return total;
    }

    // ── Incremental pull (cloud → local) ──────────────────────────────────────

    private int DoPull(DateTime since)
    {
        using var cloud = new AppDbContext { ForceCloud = true };
        using var local = new AppDbContext { SkipLastModifiedStamp = true };
        int total = 0;

        total += BatchPush(Since(cloud.PMs,                since, e => e.LastModified), local.PMs,                local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.Locations,           since, e => e.LastModified), local.Locations,          local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.RompStatuses,        since, e => e.LastModified), local.RompStatuses,       local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.AssignmentRules,     since, e => e.LastModified), local.AssignmentRules,    local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.EquipmentCategories, since, e => e.LastModified), local.EquipmentCategories,local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.EquipmentKeywords,   since, e => e.LastModified), local.EquipmentKeywords,  local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.Sites,               since, e => e.LastModified), local.Sites,              local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.Romps,               since, e => e.LastModified), local.Romps,              local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.RompEquipmentTypes,  since, e => e.LastModified), local.RompEquipmentTypes, local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.Orders,              since, e => e.LastModified), local.Orders,             local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.SavedReports,        since, e => e.LastModified), local.SavedReports,       local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.CustomColumns,       since, e => e.LastModified), local.CustomColumns,      local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.CustomRows,          since, e => e.LastModified), local.CustomRows,         local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.Users,               since, e => e.LastModified), local.Users,              local, e => e.Id, e => e.LastModified);
        total += BatchPush(Since(cloud.AuditEntries,        since, e => e.LastModified), local.AuditEntries,       local, e => e.Id, e => e.LastModified);
        total += BatchPushConfigs(Since(cloud.AppConfigs, since, c => c.LastModified), local);

        if (total > 0) local.SaveChanges();
        return total;
    }

    /// <summary>
    /// Load all records newer than <paramref name="since"/>.
    /// When since == MinValue (first run), loads everything.
    /// The predicate is inlined so EF Core can push it to SQL rather than loading all rows.
    /// </summary>
    private static List<T> Since<T>(
        IQueryable<T>       set,
        DateTime            since,
        System.Linq.Expressions.Expression<Func<T, DateTime>> getMod) where T : class
        => since == DateTime.MinValue
            ? set.AsNoTracking().ToList()
            : set.AsNoTracking().Where(
                System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
                    System.Linq.Expressions.Expression.GreaterThan(getMod.Body,
                        System.Linq.Expressions.Expression.Constant(since)),
                    getMod.Parameters))
              .ToList();

    // ── Batch merge helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Merge source records into dest using a single IN-query batch load instead of
    /// one Find() per record.  Returns count of records inserted or updated.
    /// • Id not in dest → INSERT
    /// • Id in dest and source is newer → UPDATE
    /// • Id in dest and dest is newer or equal → skip
    /// </summary>
    private static int BatchPush<T>(
        IReadOnlyList<T>  source,
        DbSet<T>          destSet,
        DbContext         destCtx,
        Func<T, int>      getId,
        Func<T, DateTime> getLastMod) where T : class
    {
        if (source.Count == 0) return 0;

        // A single `ids.Contains(...)` query with tens of thousands of parameters (e.g. a full
        // Orders table pull) is pathologically slow to plan/execute — chunk the destination
        // lookup instead of ever building one query with the whole id list.
        var ids     = source.Select(getId).ToList();
        var destMap = new Dictionary<int, T>();
        const int chunkSize = 500;
        for (int i = 0; i < ids.Count; i += chunkSize)
        {
            var chunk = ids.Skip(i).Take(chunkSize).ToList();
            foreach (var e in destSet.Where(e => chunk.Contains(EF.Property<int>(e, "Id"))))
                destMap[getId(e)] = e;
        }

        int count = 0;
        foreach (var record in source)
        {
            int id = getId(record);
            if (!destMap.TryGetValue(id, out var existing))
            {
                destSet.Add(record);
                count++;
            }
            else if (getLastMod(record) > getLastMod(existing))
            {
                destCtx.Entry(existing).CurrentValues.SetValues(record);
                count++;
            }
        }
        return count;
    }

    /// <summary>AppConfig has a string PK (Key) — batch version.</summary>
    private static int BatchPushConfigs(IReadOnlyList<AppConfig> source, DbContext destCtx)
    {
        if (source.Count == 0) return 0;

        var keys    = source.Select(c => c.Key).ToList();
        var destSet = destCtx.Set<AppConfig>();
        var destMap = destSet
            .Where(c => keys.Contains(c.Key))
            .ToDictionary(c => c.Key);

        int count = 0;
        foreach (var record in source)
        {
            if (!destMap.TryGetValue(record.Key, out var existing))
            {
                destSet.Add(record);
                count++;
            }
            else if (record.LastModified > existing.LastModified)
            {
                destCtx.Entry(existing).CurrentValues.SetValues(record);
                count++;
            }
        }
        return count;
    }

    // ── Force push (local → cloud, ignores LastModified) ─────────────────────
    //
    // Use when the cloud schema changed (new column added) or records are stuck
    // because both sides share the same LastModified timestamp.  Overwrites every
    // cloud record with the local version unconditionally.

    public void TriggerForcePush()
    {
        Task.Run(async () =>
        {
            if (!await _lock.WaitAsync(TimeSpan.FromSeconds(10))) return;
            try
            {
                Report("Force push in progress…");
                DoForcePush();
                LastSyncTime = DateTime.Now;
                Report($"Force push complete  {LastSyncTime:HH:mm:ss}");
            }
            catch (Exception ex) { Report($"Force push error: {ex.Message}"); }
            finally { _lock.Release(); }
        });
    }

    private void DoForcePush()
    {
        using var local = new AppDbContext();
        using var cloud = new AppDbContext { ForceCloud = true, SkipLastModifiedStamp = true };

        ForceMergeTable(local.PMs.AsNoTracking().ToList(),                cloud.PMs,                cloud, e => e.Id);
        ForceMergeTable(local.Locations.AsNoTracking().ToList(),           cloud.Locations,          cloud, e => e.Id);
        ForceMergeTable(local.RompStatuses.AsNoTracking().ToList(),        cloud.RompStatuses,       cloud, e => e.Id);
        ForceMergeTable(local.AssignmentRules.AsNoTracking().ToList(),     cloud.AssignmentRules,    cloud, e => e.Id);
        ForceMergeTable(local.EquipmentCategories.AsNoTracking().ToList(), cloud.EquipmentCategories,cloud, e => e.Id);
        ForceMergeTable(local.EquipmentKeywords.AsNoTracking().ToList(),   cloud.EquipmentKeywords,  cloud, e => e.Id);
        ForceMergeTable(local.Sites.AsNoTracking().ToList(),               cloud.Sites,              cloud, e => e.Id);
        ForceMergeTable(local.Romps.AsNoTracking().ToList(),               cloud.Romps,              cloud, e => e.Id);
        ForceMergeTable(local.RompEquipmentTypes.AsNoTracking().ToList(),  cloud.RompEquipmentTypes, cloud, e => e.Id);
        ForceMergeTable(local.SavedReports.AsNoTracking().ToList(),        cloud.SavedReports,       cloud, e => e.Id);
        ForceMergeTable(local.CustomColumns.AsNoTracking().ToList(),       cloud.CustomColumns,      cloud, e => e.Id);
        ForceMergeTable(local.CustomRows.AsNoTracking().ToList(),          cloud.CustomRows,         cloud, e => e.Id);
        ForceMergeTable(local.Users.AsNoTracking().ToList(),               cloud.Users,              cloud, e => e.Id);
        ForceMergeTable(local.AuditEntries.AsNoTracking().ToList(),        cloud.AuditEntries,       cloud, e => e.Id);
        ForceMergeAppConfigs(local.AppConfigs.AsNoTracking().ToList(),     cloud);
        cloud.SaveChanges();
    }

    private static void ForceMergeTable<T>(
        IReadOnlyList<T> source,
        DbSet<T>         destSet,
        DbContext        destCtx,
        Func<T, int>     getId) where T : class
    {
        foreach (var record in source)
        {
            var existing = destSet.Find(getId(record));
            if (existing is null)
                destSet.Add(record);
            else
                destCtx.Entry(existing).CurrentValues.SetValues(record); // always overwrite
        }
    }

    private static void ForceMergeAppConfigs(IEnumerable<AppConfig> source, DbContext destCtx)
    {
        var destSet = destCtx.Set<AppConfig>();
        foreach (var record in source)
        {
            var existing = destSet.Find(record.Key);
            if (existing is null)
                destSet.Add(record);
            else
                destCtx.Entry(existing).CurrentValues.SetValues(record);
        }
    }

    private void Report(string msg)
    {
        LastStatus = msg;
        StatusChanged?.Invoke(msg);
    }

    /// <summary>
    /// Builds and raises a short "who changed what" summary for the rows this pull just
    /// brought in, using the LastModifiedBy column (Orders/Sites/Romps only — the entities that
    /// column exists on). This machine's own changes are excluded, since they aren't news to
    /// the person sitting at it. Best-effort: a failure here must never break the sync cycle
    /// itself, so it's caught and silently skipped.
    /// </summary>
    private void ReportDataChanged(DateTime since)
    {
        if (DataChanged == null) return;

        try
        {
            using var cloud = new AppDbContext { ForceCloud = true };
            var parts = new List<string>();

            void Summarize(string label, IEnumerable<string> byWhom)
            {
                foreach (var group in byWhom
                    .Where(who => !string.IsNullOrWhiteSpace(who) &&
                                  !string.Equals(who, CurrentUser.Email, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(who => who))
                {
                    var count = group.Count();
                    parts.Add($"{count} {label}{(count == 1 ? "" : "s")} updated by {group.Key}");
                }
            }

            Summarize("order", cloud.Orders.AsNoTracking().Where(o => o.LastModified > since).Select(o => o.LastModifiedBy));
            Summarize("site",  cloud.Sites.AsNoTracking().Where(s => s.LastModified > since).Select(s => s.LastModifiedBy));
            Summarize("ROMP",  cloud.Romps.AsNoTracking().Where(r => r.LastModified > since).Select(r => r.LastModifiedBy));

            if (parts.Count > 0) DataChanged?.Invoke(string.Join("  •  ", parts));
        }
        catch { /* notifications are best-effort — never break the sync cycle over one */ }
    }
}
