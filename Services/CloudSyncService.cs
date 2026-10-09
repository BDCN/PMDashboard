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
    // Two cursors on two different clocks (they used to be one, which is what let a delayed
    // upload slip past the pull — see PullOverlap):
    //  • _lastPushUtc       — THIS machine's clock. Local rows are stamped by this machine,
    //                         so "which local rows changed since" must use the same clock.
    //  • _lastPullServerUtc — the CLOUD SERVER's clock (CloudNowUtc). Every pushed row is
    //                         restamped with the server's time as it lands (see BatchPush's
    //                         stamp), so "which cloud rows arrived since" is one clock for all
    //                         machines: no skew between PCs, and a row edited an hour ago but
    //                         uploaded just now still counts as new.
    private DateTime              _lastPushUtc       = DateTime.MinValue;
    private DateTime              _lastPullServerUtc = DateTime.MinValue;

    // Periodic pulls re-read this much history before _lastPullServerUtc, to cover rows whose
    // upload transaction was still committing when this machine's cycle read the server clock.
    // History: LastModified used to be only the EDIT time on the editing machine's clock, so any
    // upload delay longer than the overlap lost the row for good — 6 cluster assignments (clock
    // race) and then 2 campus unassignments made during a 5-minute full upload (2026-10-05).
    // Pushes now restamp rows with the server's time on arrival, so the overlap only has to
    // cover commit latency. Re-reading is harmless — BatchPush only applies newer rows.
    private static readonly TimeSpan PullOverlap = TimeSpan.FromMinutes(2);
    private bool                  _initialDone  = false;
    // Seeded from DatabaseConfig inside Start() (not here — App.CloudSync is a static field
    // constructed before App.xaml.cs's OnStartup calls DatabaseConfig.Load(), so reading it in
    // a field initializer would always see the not-yet-loaded default). See
    // DatabaseConfig.CloudFirstPushDone for why this must survive app restarts.
    private bool                  _firstPushDone;
    // See DatabaseConfig.ForecastFirstSyncDone for why the Forecast tables need their own
    // one-time "ignore the watermark" flag, separate from _firstPushDone.
    private bool                  _forecastFirstSyncDone;
    private Task?                 _initialPullTask;

    /// <summary>True once the initial cloud→local pull for this app session has finished.</summary>
    public bool InitialPullDone => _initialDone;

    public event Action<string>?  StatusChanged;

    // Fires from a periodic (not the initial startup) pull whenever it brought in ANY rows —
    // a short, human-readable summary for a non-distracting toast, and the signal pages use to
    // reload. Deliberately not raised from the initial pull (that's just this machine catching
    // up, not "news"). Raised even when the changes can't be attributed to another user (same
    // account signed in on two computers, or a table without LastModifiedBy like
    // AssignmentHistories/PMs) — every pulled row came from some other machine by definition,
    // and gating on attribution left pages silently stale in exactly those cases.
    public event Action<string>?  DataChanged;

    public string                 LastStatus   { get; private set; } = "Not started";
    public DateTime?              LastSyncTime { get; private set; }
    public bool                   IsRunning    { get; private set; }

    // ── Progress (for a sidebar "syncing…" indicator) ──────────────────────────
    // Only populated for the two operations big enough to matter: the initial pull every
    // launch does, and the one-time-per-machine full push (see DatabaseConfig.CloudFirstPushDone).
    // Ordinary 30s incremental ticks are near-instant and deliberately don't populate this —
    // a progress bar that flashes for a fraction of a second is just noise.
    public event Action?          ProgressChanged;

    /// <summary>
    /// Set when some other machine has reported a newer app version than this one (see
    /// ReportAndCheckVersions) — e.g. "1.0.3 on CHARLES-BELL-L2". Null when this build is the
    /// newest in use. An outdated build is exactly what silently dropped cluster assignments on
    /// 2026-10-05 (it didn't know the Locations.AssignedPmId column existed), so it's worth
    /// telling the person at that machine to update.
    /// </summary>
    public string? NewerVersionInUse { get; private set; }
    public event Action?          VersionWarningChanged;

    private const string ClientVersionKeyPrefix = "ClientVersion.";
    private int _ticksSinceVersionCheck;
    public bool                   IsSyncing      { get; private set; }
    public int                    SyncUnitsDone  { get; private set; }
    public int                    SyncUnitsTotal { get; private set; }
    public DateTime?              SyncStartedUtc { get; private set; }

    /// <summary>0-100, or null when there's no total to measure against (nothing to show).</summary>
    public double? SyncPercent => SyncUnitsTotal > 0
        ? Math.Min(100.0, 100.0 * SyncUnitsDone / SyncUnitsTotal)
        : null;

    /// <summary>Linear estimate from elapsed time and units done so far — rough, but self-corrects as it runs.</summary>
    public TimeSpan? SyncEta
    {
        get
        {
            if (SyncStartedUtc is not { } started || SyncUnitsDone <= 0 || SyncUnitsTotal <= 0) return null;
            var elapsed = DateTime.UtcNow - started;
            var remaining = SyncUnitsTotal - SyncUnitsDone;
            if (remaining <= 0) return TimeSpan.Zero;
            var perUnit = elapsed.TotalSeconds / SyncUnitsDone;
            return TimeSpan.FromSeconds(perUnit * remaining);
        }
    }

    private void BeginProgress(int totalUnits)
    {
        IsSyncing      = true;
        SyncUnitsDone  = 0;
        SyncUnitsTotal = totalUnits;
        SyncStartedUtc = DateTime.UtcNow;
        ProgressChanged?.Invoke();
    }

    private void AddProgress(int units)
    {
        if (!IsSyncing) return;
        SyncUnitsDone += units;
        ProgressChanged?.Invoke();
    }

    private void EndProgress()
    {
        IsSyncing      = false;
        SyncUnitsDone  = 0;
        SyncUnitsTotal = 0;
        SyncStartedUtc = null;
        ProgressChanged?.Invoke();
    }

    /// <summary>Cheap local COUNT(*) pass ahead of the one-time-per-machine full push, purely for the progress total.</summary>
    private void BeginLocalCountProgress()
    {
        try
        {
            using var local = new AppDbContext();
            int total = local.PMs.Count() + local.Locations.Count() + local.RompStatuses.Count()
                + local.AssignmentRules.Count() + local.EquipmentCategories.Count() + local.EquipmentKeywords.Count()
                + local.Sites.Count() + local.Campuses.Count() + local.SubRegions.Count() + local.TeamMembers.Count() + local.Romps.Count() + local.RompEquipmentTypes.Count()
                + local.Orders.Count() + local.SavedReports.Count() + local.CustomColumns.Count()
                + local.CustomRows.Count() + local.Users.Count() + local.AuditEntries.Count()
                + local.AssignmentHistories.Count()
                + local.ForecastTypes.Count() + local.ForecastFields.Count() + local.ForecastHeaderMappings.Count()
                + local.ForecastImports.Count() + local.ForecastRows.Count()
                + local.AppConfigs.Count();
            BeginProgress(total);
        }
        catch { /* progress is a nice-to-have, never worth failing the push over */ }
    }

    public static bool IsAvailable =>
        DatabaseConfig.IsCloud &&
        !string.IsNullOrWhiteSpace(DatabaseConfig.CloudConnectionString);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start(int intervalSeconds = 30)
    {
        if (!IsAvailable) return;
        IsRunning = true;
        Report($"Connecting to cloud — initial sync…  (SAT {AppVersion.Full}, {Environment.MachineName})");
        _firstPushDone         = DatabaseConfig.CloudFullPushCurrent; // see RequiredFullPushGeneration
        _forecastFirstSyncDone = DatabaseConfig.ForecastFirstSyncDone;

        // Hook immediate push on every local save
        AppDbContext.LocalSaved += OnLocalSaved;

        // Initial full pull on background thread; start timer after it finishes.
        // Re-check IsRunning before creating the timer — Stop()/Dispose() may have run while
        // the pull was still in flight (e.g. app closed mid-sync), and without this guard the
        // continuation would spin up a new Timer after shutdown already believed sync was off.
        // The task is kept (not fire-and-forget) so a caller that genuinely needs fresh data
        // before proceeding — see WaitForInitialPullAsync — can await it.
        _initialPullTask = Task.Run(async () =>
        {
            await DoInitialPullAsync();
            if (!IsRunning) return;
            _timer = new Timer(SyncTick, null,
                TimeSpan.FromSeconds(intervalSeconds),
                TimeSpan.FromSeconds(intervalSeconds));
        });
    }

    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) for the initial cloud→local pull kicked off by
    /// <see cref="Start"/> to finish, without blocking the app's startup path by default — most
    /// launches are an already-known user on a machine that already has a local Users table, and
    /// making every single one of those wait for a cloud round-trip just to show the Sign In
    /// screen added a ~10 second startup delay for no benefit. Callers should reach for this only
    /// in the two cases that actually need fresh cloud data first: a genuinely empty local Users
    /// table (deciding whether to show Create-Admin vs. recognizing an existing cloud account),
    /// and a failed login that might just mean "not synced to this machine yet".
    /// </summary>
    public async Task WaitForInitialPullAsync(TimeSpan timeout)
    {
        var task = _initialPullTask;
        if (task == null) return;
        await Task.WhenAny(task, Task.Delay(timeout));
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
                var forecastSince = _forecastFirstSyncDone ? _lastPushUtc : DateTime.MinValue;
                int pushed = DoPush(_lastPushUtc, forecastSince);
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
        try { RunSyncCycle(); }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Runs one push+pull sync cycle right now, instead of waiting for the next periodic tick —
    /// backs the sidebar's manual "Sync Now" button. Unlike the periodic tick (which just skips
    /// a cycle if one is already running), this waits briefly for an in-progress cycle to finish
    /// rather than silently doing nothing, since it's a deliberate user action. The actual DB
    /// work runs on a background thread so the UI stays responsive.
    /// </summary>
    public async Task SyncNowAsync()
    {
        if (!IsAvailable || !IsRunning || !_initialDone) return;
        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Report("Sync already in progress — try again in a moment.");
            return;
        }
        try { await Task.Run(RunSyncCycle); }
        finally { _lock.Release(); }
    }

    private void RunSyncCycle()
    {
        try
        {
            if (!_initialDone) return;
            var since      = _lastPushUtc;
            var pullCursor = _lastPullServerUtc;
            // The new watermark is stamped from BEFORE this cycle's reads, not after they
            // finish — DoPush/DoPull can take tens of seconds (each table opens its own
            // connection), and a row edited by the user while the cycle is mid-flight would
            // have a LastModified that lands between "old since" and "watermark taken at the
            // end", so it's silently skipped by THIS cycle's query yet permanently excluded
            // from every future one too (since its timestamp would then be older than the new
            // watermark) — never retried again without another manual edit. Stamping from the
            // start means any such edit's LastModified is still >= the new watermark, so the
            // next cycle picks it up instead of losing it. (Reprocessing a row whose edit
            // *was* already captured this cycle is harmless — BatchPush no-ops it.)
            var cycleStartUtc    = DateTime.UtcNow;  // push cursor — this machine's clock
            var cycleStartServer = CloudNowUtc();    // pull cursor — the server's clock
            TryClaimIdSlot(); // no-op once claimed
            // ClientVersion rows arrive with the normal AppConfigs pull, so this only reads
            // local SQLite — re-checked every ~10 minutes (20 ticks at the default 30s).
            if (++_ticksSinceVersionCheck >= 20) { _ticksSinceVersionCheck = 0; CheckForNewerVersion(); }
            // First push EVER for this machine sends ALL local records so that records with the
            // migration-default LastModified ('2000-01-01') are not filtered out. Persisted via
            // DatabaseConfig so this happens once per machine, not once per app launch — see
            // DatabaseConfig.CloudFirstPushDone.
            // MergeTable still checks LastModified before overwriting, so newer cloud
            // records won't be clobbered.
            // Forecast tables get their own one-time MinValue pass — see
            // DatabaseConfig.ForecastFirstSyncDone.
            var forecastSince = _forecastFirstSyncDone ? since : DateTime.MinValue;
            // This is the one periodic-tick case worth showing progress for — it's the only
            // one big enough to take more than an instant (see the class doc on ProgressChanged).
            if (!_firstPushDone) BeginLocalCountProgress();
            int pushed = DoPush(_firstPushDone ? since : DateTime.MinValue, forecastSince);
            EndProgress();
            _firstPushDone = true;
            DatabaseConfig.MarkCloudFirstPushDone();
            var pullSince         = pullCursor    == DateTime.MinValue ? pullCursor        : pullCursor - PullOverlap;
            var forecastPullSince = forecastSince == DateTime.MinValue ? DateTime.MinValue : pullSince;
            int pulled = DoPull(pullSince, forecastPullSince);
            if (pulled > 0) ReportDataChanged(pullSince, pulled);
            _forecastFirstSyncDone = true;
            DatabaseConfig.MarkForecastFirstSyncDone();
            _lastPushUtc       = cycleStartUtc;
            _lastPullServerUtc = cycleStartServer;
            LastSyncTime = DateTime.Now;
            Report($"Synced  {LastSyncTime:HH:mm:ss}  (↑{pushed} ↓{pulled})");
        }
        catch (Exception ex) { Report($"Sync error: {InnerMessage(ex)}"); DebugLog("FULL: " + ex); if (IsSyncing) EndProgress(); }
    }

    /// <summary>
    /// Adds the Cluster → Campus → Site columns/table to the cloud database if they aren't there
    /// yet, so a new build doesn't depend on someone running the phpMyAdmin script first (the
    /// cloud account has CREATE/ALTER rights). Idempotent MariaDB IF NOT EXISTS forms; no foreign
    /// keys on the new columns, so a push order hiccup can never reject a site. Mirrored in
    /// SyncService.MariaDbDdlScript for fresh installs. Best-effort: failures are logged and the
    /// per-table sync errors make any gap obvious.
    /// </summary>
    private static void EnsureCloudSchema(AppDbContext cloud)
    {
        string[] statements =
        {
            """
            CREATE TABLE IF NOT EXISTS `Campuses` (
                `Id`             INT          NOT NULL AUTO_INCREMENT,
                `Name`           VARCHAR(255) NOT NULL,
                `LocationId`     INT          NOT NULL,
                `AssignedPmId`   INT          NULL,
                `LastModified`   DATETIME(6)  NOT NULL DEFAULT '2000-01-01 00:00:00',
                `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '',
                PRIMARY KEY (`Id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            "ALTER TABLE `Sites` ADD COLUMN IF NOT EXISTS `CampusId` INT NULL",
            "ALTER TABLE `Sites` ADD COLUMN IF NOT EXISTS `CampusManual` TINYINT(1) NOT NULL DEFAULT 0",
            "ALTER TABLE `Sites` ADD COLUMN IF NOT EXISTS `PmOverride` TINYINT(1) NOT NULL DEFAULT 0",
            "ALTER TABLE `ForecastFields` ADD COLUMN IF NOT EXISTS `IsCampusField` TINYINT(1) NOT NULL DEFAULT 0",
            """
            CREATE TABLE IF NOT EXISTS `SubRegions` (
                `Id`           INT          NOT NULL AUTO_INCREMENT,
                `Name`         VARCHAR(255) NOT NULL,
                `Region`       VARCHAR(64)  NOT NULL DEFAULT '',
                `SortOrder`    INT          NOT NULL DEFAULT 0,
                `LastModified` DATETIME(6)  NOT NULL DEFAULT '2000-01-01 00:00:00',
                PRIMARY KEY (`Id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
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
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            "ALTER TABLE `PMs` ADD COLUMN IF NOT EXISTS `PeId` INT NULL",
            "ALTER TABLE `Locations` ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL",
            "ALTER TABLE `Campuses` ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL",
            "ALTER TABLE `Sites` ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL",
            "ALTER TABLE `PMs` ADD COLUMN IF NOT EXISTS `IsPlaceholder` TINYINT(1) NOT NULL DEFAULT 0",
            "ALTER TABLE `TeamMembers` ADD COLUMN IF NOT EXISTS `IsPlaceholder` TINYINT(1) NOT NULL DEFAULT 0",
            "ALTER TABLE `PMs` ADD COLUMN IF NOT EXISTS `SubRegionId` INT NULL",
            "ALTER TABLE `PMs` ADD COLUMN IF NOT EXISTS `OtherSubRegionIds` VARCHAR(1000) NOT NULL DEFAULT ''",
        };
        foreach (var sql in statements)
        {
            try
            {
#pragma warning disable EF1002 // constant DDL, no user input
                cloud.Database.ExecuteSqlRaw(sql);
#pragma warning restore EF1002
            }
            catch (Exception ex) { DebugLog("Cloud schema update failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// The cloud server's current UTC time — the single clock every machine's pull cursor and
    /// every pushed row's LastModified are expressed in (see _lastPullServerUtc). Falls back to
    /// this machine's clock if the query fails, which just degrades to the old behaviour.
    /// </summary>
    private static DateTime CloudNowUtc()
    {
        try
        {
            using var cloud = new AppDbContext(forceCloud: true);
            return cloud.Database.SqlQueryRaw<DateTime>("SELECT UTC_TIMESTAMP(6) AS `Value`").AsEnumerable().First();
        }
        catch (Exception ex)
        {
            DebugLog("Cloud clock read failed, using local clock: " + ex.Message);
            return DateTime.UtcNow;
        }
    }

    /// <summary>Claims this machine's Id slot if it doesn't hold one yet (see IdAllocator).
    /// Best-effort: on failure it's logged and retried at the next sync cycle; until then new
    /// rows fall back to plain autoincrement, i.e. the old behaviour.</summary>
    private void TryClaimIdSlot()
    {
        if (IdAllocator.Slot != null) return;
        try
        {
            IdAllocator.EnsureSlotClaimed();
            if (IdAllocator.Slot is { } s) DebugLog($"Id slot {s} in use (new rows get Ids from {(long)s * IdAllocator.RangeSize})");
        }
        catch (Exception ex) { DebugLog("Id slot claim failed (will retry): " + ex); }
    }

    /// <summary>
    /// Records this machine's running version in the cloud as AppConfig "ClientVersion.{machine}"
    /// — written straight to the cloud (ForceCloud, like ImportLockService) so it's there even
    /// before this machine's first push. Every other machine's normal AppConfigs pull then
    /// brings it down. Best-effort: never worth failing sync over.
    /// </summary>
    private void ReportVersionToCloud()
    {
        try
        {
            var key   = ClientVersionKeyPrefix + Environment.MachineName;
            var value = System.Text.Json.JsonSerializer.Serialize(new ClientVersionInfo
            {
                Version      = AppVersion.Number,
                BuildDateUtc = AppVersion.BuildDateUtc,
                User         = CurrentUser.Email,
                LastSeenUtc  = DateTime.UtcNow,
            });
            using var cloud = new AppDbContext(forceCloud: true) { SkipLastModifiedStamp = true };
            var row = cloud.AppConfigs.Find(key);
            if (row == null)
                cloud.AppConfigs.Add(new AppConfig { Key = key, Value = value, LastModified = CloudNowUtc() });
            else
            {
                row.Value        = value;
                row.LastModified = CloudNowUtc(); // server clock, so other machines' pull cursors see it
            }
            cloud.SaveChanges();
        }
        catch (Exception ex) { DebugLog("Version report failed: " + ex.Message); }
    }

    /// <summary>Compares this build against every machine's reported version (local copies of
    /// the ClientVersion.* rows) and raises VersionWarningChanged when the answer changes.</summary>
    private void CheckForNewerVersion()
    {
        try
        {
            var newest = ClientVersions()
                .Where(c => c.Machine != Environment.MachineName && c.ParsedVersion != null)
                .OrderByDescending(c => c.ParsedVersion)
                .FirstOrDefault();

            string? warning = newest != null && newest.ParsedVersion > AppVersion.Version
                ? $"{newest.Info.Version} on {newest.Machine}"
                : null;

            if (warning != NewerVersionInUse)
            {
                NewerVersionInUse = warning;
                if (warning != null) DebugLog($"Newer version in use: {warning} (this machine runs {AppVersion.Number})");
                VersionWarningChanged?.Invoke();
            }
        }
        catch (Exception ex) { DebugLog("Version check failed: " + ex.Message); }
    }

    /// <summary>Every machine's last-reported version, from the local AppConfigs mirror —
    /// also used by the Configuration page's "Versions in use" list.</summary>
    public static List<ClientVersionEntry> ClientVersions()
    {
        using var db = new AppDbContext();
        return db.AppConfigs.AsNoTracking()
            .Where(c => c.Key.StartsWith(ClientVersionKeyPrefix))
            .ToList()
            .Select(c =>
            {
                ClientVersionInfo? info = null;
                try { info = System.Text.Json.JsonSerializer.Deserialize<ClientVersionInfo>(c.Value); } catch { }
                return info == null ? null : new ClientVersionEntry(c.Key[ClientVersionKeyPrefix.Length..], info);
            })
            .Where(e => e != null)
            .Select(e => e!)
            .OrderBy(e => e.Machine)
            .ToList();
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
            var since = _lastPushUtc;
            var forecastSince = _forecastFirstSyncDone ? since : DateTime.MinValue;
            int pushed = await Task.Run(() => DoPush(_firstPushDone ? since : DateTime.MinValue, forecastSince));
            _firstPushDone = true;
            DatabaseConfig.MarkCloudFirstPushDone();
            _forecastFirstSyncDone = true;
            DatabaseConfig.MarkForecastFirstSyncDone();
            // Deliberately advances neither cursor: this path never pulls (advancing the pull
            // cursor once silently skipped other machines' rows), and leaving the push cursor
            // just means the next tick re-checks these rows — a no-op, BatchPush only applies
            // newer rows.
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
            using var cloud = new AppDbContext(forceCloud: true);
            EnsureCloudSchema(cloud);
            // Cursor = when the pull STARTED, not finished — it can take 30s+, and anything
            // another machine pushes meanwhile must still be picked up by the first tick.
            var pullStartUtc  = CloudNowUtc();   // pull cursor — server clock
            var pushStartUtc  = DateTime.UtcNow; // push cursor — this machine's clock

            // Cheap COUNT(*) pass so the sidebar indicator has a real total to show a
            // percentage/ETA against — a row fetch is the expensive part, a count isn't.
            // Best-effort: if this fails for some reason, progress just won't show a total
            // (SyncPercent/SyncEta both go null), the actual pull below is unaffected either way.
            try
            {
                int total = cloud.PMs.Count() + cloud.Locations.Count() + cloud.RompStatuses.Count()
                    + cloud.AssignmentRules.Count() + cloud.EquipmentCategories.Count() + cloud.EquipmentKeywords.Count()
                    + cloud.Sites.Count() + cloud.Campuses.Count() + cloud.SubRegions.Count() + cloud.TeamMembers.Count() + cloud.Romps.Count() + cloud.RompEquipmentTypes.Count()
                    + cloud.Orders.Count() + cloud.SavedReports.Count() + cloud.CustomColumns.Count()
                    + cloud.CustomRows.Count() + cloud.Users.Count() + cloud.AuditEntries.Count()
                    + cloud.AssignmentHistories.Count()
                    + cloud.ForecastTypes.Count() + cloud.ForecastFields.Count() + cloud.ForecastHeaderMappings.Count()
                    + cloud.ForecastImports.Count() + cloud.ForecastRows.Count()
                    + cloud.AppConfigs.Count();
                BeginProgress(total);
            }
            catch { /* progress is a nice-to-have, never worth failing startup over */ }

            // Pull each table independently so a failure in one table doesn't block others.
            // FK-safe order: parents before children.
            // IMPORTANT: the cloud read (cloud.Xxx.AsNoTracking().ToList()) must happen INSIDE
            // InitialPullTable's own try/catch, not be materialized here as a method argument —
            // a schema mismatch (e.g. a column added locally but never migrated on the cloud
            // side) throws during that read, and if it happens as an argument to this call it
            // escapes to the outer try/catch and aborts every table listed after it, silently.
            // (This is exactly how a missing Sites.IsClosed column on the cloud once caused
            // Users — listed after Sites — to never get pulled at all.)
            InitialPullTable("PMs",                 () => cloud.PMs.AsNoTracking().ToList(),                db => db.PMs,                e => e.Id, e => e.LastModified);
            InitialPullTable("Locations",            () => cloud.Locations.AsNoTracking().ToList(),           db => db.Locations,          e => e.Id, e => e.LastModified);
            InitialPullTable("RompStatuses",         () => cloud.RompStatuses.AsNoTracking().ToList(),        db => db.RompStatuses,       e => e.Id, e => e.LastModified);
            InitialPullTable("AssignmentRules",      () => cloud.AssignmentRules.AsNoTracking().ToList(),     db => db.AssignmentRules,    e => e.Id, e => e.LastModified);
            InitialPullTable("EquipmentCategories",  () => cloud.EquipmentCategories.AsNoTracking().ToList(), db => db.EquipmentCategories,e => e.Id, e => e.LastModified);
            InitialPullTable("EquipmentKeywords",    () => cloud.EquipmentKeywords.AsNoTracking().ToList(),   db => db.EquipmentKeywords,  e => e.Id, e => e.LastModified);
            InitialPullTable("SubRegions",             () => cloud.SubRegions.AsNoTracking().ToList(),            db => db.SubRegions,           e => e.Id, e => e.LastModified);
            InitialPullTable("TeamMembers",             () => cloud.TeamMembers.AsNoTracking().ToList(),            db => db.TeamMembers,           e => e.Id, e => e.LastModified);
            InitialPullTable("Campuses",             () => cloud.Campuses.AsNoTracking().ToList(),            db => db.Campuses,           e => e.Id, e => e.LastModified);
            InitialPullTable("Sites",                () => cloud.Sites.AsNoTracking().ToList(),               db => db.Sites,              e => e.Id, e => e.LastModified);
            InitialPullTable("Romps",                () => cloud.Romps.AsNoTracking().ToList(),               db => db.Romps,              e => e.Id, e => e.LastModified);
            // RompEquipmentTypes uses natural key (RompId, EquipmentCategoryId) — not Id — to avoid
            // unique constraint violations when both sides have the same pair under different auto-increment Ids.
            try
            {
                var rompEquip = cloud.RompEquipmentTypes.AsNoTracking().ToList();
                AddProgress(rompEquip.Count);
                InitialPullRompEquipmentTypes(rompEquip);
            }
            catch (Exception ex) { Report($"Initial pull warning (RompEquipmentTypes): {InnerMessage(ex)}"); DebugLog("FULL initial pull error (RompEquipmentTypes): " + ex); }
            InitialPullTable("Orders",               () => cloud.Orders.AsNoTracking().ToList(),              db => db.Orders,             e => e.Id, e => e.LastModified);
            InitialPullTable("SavedReports",         () => cloud.SavedReports.AsNoTracking().ToList(),        db => db.SavedReports,       e => e.Id, e => e.LastModified);
            InitialPullTable("CustomColumns",        () => cloud.CustomColumns.AsNoTracking().ToList(),       db => db.CustomColumns,      e => e.Id, e => e.LastModified);
            InitialPullTable("CustomRows",           () => cloud.CustomRows.AsNoTracking().ToList(),          db => db.CustomRows,         e => e.Id, e => e.LastModified);
            InitialPullTable("Users",                () => cloud.Users.AsNoTracking().ToList(),               db => db.Users,              e => e.Id, e => e.LastModified);
            InitialPullTable("AuditEntries",         () => cloud.AuditEntries.AsNoTracking().ToList(),        db => db.AuditEntries,       e => e.Id, e => e.LastModified);
            InitialPullTable("AssignmentHistories",  () => cloud.AssignmentHistories.AsNoTracking().ToList(), db => db.AssignmentHistories,e => e.Id, e => e.LastModified);

            // Forecast Planning — FK-safe order: Types, then Fields/HeaderMappings (both
            // reference Types; HeaderMappings also references Fields), then Imports (references
            // Types), then Rows (references Imports, Types, and Sites — Sites already pulled above).
            InitialPullTable("ForecastTypes",          () => cloud.ForecastTypes.AsNoTracking().ToList(),          db => db.ForecastTypes,          e => e.Id, e => e.LastModified);
            InitialPullTable("ForecastFields",         () => cloud.ForecastFields.AsNoTracking().ToList(),         db => db.ForecastFields,         e => e.Id, e => e.LastModified);
            InitialPullTable("ForecastHeaderMappings", () => cloud.ForecastHeaderMappings.AsNoTracking().ToList(), db => db.ForecastHeaderMappings, e => e.Id, e => e.LastModified);
            InitialPullTable("ForecastImports",        () => cloud.ForecastImports.AsNoTracking().ToList(),        db => db.ForecastImports,        e => e.Id, e => e.LastModified);
            InitialPullTable("ForecastRows",           () => cloud.ForecastRows.AsNoTracking().ToList(),           db => db.ForecastRows,           e => e.Id, e => e.LastModified);

            // AppConfigs (string PK)
            try
            {
                var configs = cloud.AppConfigs.AsNoTracking().ToList();
                AddProgress(configs.Count);
                using var local = new AppDbContext { SkipLastModifiedStamp = true };
                BatchPushConfigs(configs, local);
                local.SaveChanges();
            }
            catch (Exception ex)
            {
                Report($"Initial pull warning (AppConfigs): {InnerMessage(ex)}");
                DebugLog("FULL initial pull error (AppConfigs): " + ex);
            }

            _lastPullServerUtc = pullStartUtc;
            _lastPushUtc       = pushStartUtc;
            // The pull may have brought down rows this machine created in an earlier install;
            // make IdAllocator re-read its range maxima from the now-complete local DB.
            IdAllocator.ResetCounters();
            TryClaimIdSlot();
            ReportVersionToCloud();
            // Place any site that has no campus yet (first run of this version, or new sites
            // pulled from a machine on an older build). Its writes push on the next tick.
            try
            {
                var arranged = CampusArrangementService.Arrange();
                if (arranged.SitesPlaced + arranged.SitesMoved + arranged.CampusesCreated + arranged.DuplicatesMerged > 0)
                    DebugLog($"Campus arrangement: {arranged}");
            }
            catch (Exception ex) { DebugLog("Campus arrangement failed: " + ex); }
            CheckForNewerVersion();
            _initialDone = true;
            LastSyncTime = DateTime.Now;
            Report($"Cloud sync ready  {LastSyncTime:HH:mm:ss}");
            EndProgress();
        }
        catch (Exception ex)
        {
            Report($"Initial pull failed: {InnerMessage(ex)}");
            _initialDone = true;
            EndProgress();
        }
        finally { _lock.Release(); }
    }

    private void InitialPullTable<T>(
        string tableName,
        Func<IReadOnlyList<T>> loadCloudRows,
        Func<AppDbContext, DbSet<T>> getSet,
        Func<T, int> getId,
        Func<T, DateTime> getLastMod) where T : class
    {
        try
        {
            var cloudRows = loadCloudRows();
            AddProgress(cloudRows.Count);
            using var local = new AppDbContext { SkipLastModifiedStamp = true };
            // applyOnEqual: re-apply cloud rows even when timestamps match — see BatchPush.
            BatchPush(cloudRows, getSet(local), local, getId, getLastMod, applyOnEqual: true);
            local.SaveChanges();
        }
        catch (Exception ex)
        {
            Report($"Initial pull warning ({tableName}): {InnerMessage(ex)}");
            DebugLog($"FULL initial pull error ({tableName}): " + ex);
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
    //
    // Each table gets its own destination context and its own SaveChanges() — a failure in
    // one table (a schema mismatch, a constraint violation, etc.) is reported and skipped
    // rather than aborting every table queued after it. This used to be one shared context
    // with a single SaveChanges() at the end: a single bad row anywhere (a missing column on
    // Sites; a duplicate key on RompEquipmentTypes) silently blocked Users and everything
    // else behind it, for as long as the bad row existed — which in practice was over a day.

    // forecastSince overrides since just for the 5 Forecast tables — see
    // DatabaseConfig.ForecastFirstSyncDone for why they need a separate watermark.
    private int DoPush(DateTime since, DateTime forecastSince)
    {
        using var local = new AppDbContext();
        int total = 0;
        // Arrival stamp for everything this push writes - the server's clock (see _lastPullServerUtc).
        var stamp = CloudNowUtc();

        total += SafeSyncTable("PMs",                () => Since(local.PMs, since, e => e.LastModified),                db => db.PMs,                e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("Locations",           () => Since(local.Locations, since, e => e.LastModified),           db => db.Locations,          e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("RompStatuses",        () => Since(local.RompStatuses, since, e => e.LastModified),        db => db.RompStatuses,       e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("AssignmentRules",     () => Since(local.AssignmentRules, since, e => e.LastModified),     db => db.AssignmentRules,    e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("EquipmentCategories", () => Since(local.EquipmentCategories, since, e => e.LastModified), db => db.EquipmentCategories,e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("EquipmentKeywords",   () => Since(local.EquipmentKeywords, since, e => e.LastModified),   db => db.EquipmentKeywords,  e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("SubRegions",            () => Since(local.SubRegions, since, e => e.LastModified),            db => db.SubRegions,           e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("TeamMembers",            () => Since(local.TeamMembers, since, e => e.LastModified),            db => db.TeamMembers,           e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("Campuses",            () => Since(local.Campuses, since, e => e.LastModified),            db => db.Campuses,           e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("Sites",               () => Since(local.Sites, since, e => e.LastModified),               db => db.Sites,              e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("Romps",               () => Since(local.Romps, since, e => e.LastModified),               db => db.Romps,              e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncRompEquipmentTypes("RompEquipmentTypes", () => Since(local.RompEquipmentTypes, since, e => e.LastModified), destIsCloud: true);
        total += SafeSyncTable("Orders",              () => Since(local.Orders, since, e => e.LastModified),              db => db.Orders,             e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("SavedReports",        () => Since(local.SavedReports, since, e => e.LastModified),        db => db.SavedReports,       e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("CustomColumns",       () => Since(local.CustomColumns, since, e => e.LastModified),       db => db.CustomColumns,      e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("CustomRows",          () => Since(local.CustomRows, since, e => e.LastModified),          db => db.CustomRows,         e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("Users",               () => Since(local.Users, since, e => e.LastModified),               db => db.Users,              e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("AuditEntries",        () => Since(local.AuditEntries, since, e => e.LastModified),        db => db.AuditEntries,       e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("AssignmentHistories",  () => Since(local.AssignmentHistories, since, e => e.LastModified), db => db.AssignmentHistories,e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("ForecastTypes",          () => Since(local.ForecastTypes, forecastSince, e => e.LastModified),          db => db.ForecastTypes,          e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("ForecastFields",         () => Since(local.ForecastFields, forecastSince, e => e.LastModified),         db => db.ForecastFields,         e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("ForecastHeaderMappings", () => Since(local.ForecastHeaderMappings, forecastSince, e => e.LastModified), db => db.ForecastHeaderMappings, e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("ForecastImports",        () => Since(local.ForecastImports, forecastSince, e => e.LastModified),        db => db.ForecastImports,        e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncTable("ForecastRows",           () => Since(local.ForecastRows, forecastSince, e => e.LastModified),           db => db.ForecastRows,           e => e.Id, e => e.LastModified, destIsCloud: true, pushStamp: stamp);
        total += SafeSyncConfigs("AppConfigs",        () => Since(local.AppConfigs, since, c => c.LastModified), destIsCloud: true, pushStamp: stamp);

        return total;
    }

    // ── Incremental pull (cloud → local) ──────────────────────────────────────

    private int DoPull(DateTime since, DateTime forecastSince)
    {
        using var cloud = new AppDbContext(forceCloud: true);
        int total = 0;

        total += SafeSyncTable("PMs",                () => Since(cloud.PMs, since, e => e.LastModified),                db => db.PMs,                e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("Locations",           () => Since(cloud.Locations, since, e => e.LastModified),           db => db.Locations,          e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("RompStatuses",        () => Since(cloud.RompStatuses, since, e => e.LastModified),        db => db.RompStatuses,       e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("AssignmentRules",     () => Since(cloud.AssignmentRules, since, e => e.LastModified),     db => db.AssignmentRules,    e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("EquipmentCategories", () => Since(cloud.EquipmentCategories, since, e => e.LastModified), db => db.EquipmentCategories,e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("EquipmentKeywords",   () => Since(cloud.EquipmentKeywords, since, e => e.LastModified),   db => db.EquipmentKeywords,  e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("SubRegions",            () => Since(cloud.SubRegions, since, e => e.LastModified),            db => db.SubRegions,           e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("TeamMembers",            () => Since(cloud.TeamMembers, since, e => e.LastModified),            db => db.TeamMembers,           e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("Campuses",            () => Since(cloud.Campuses, since, e => e.LastModified),            db => db.Campuses,           e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("Sites",               () => Since(cloud.Sites, since, e => e.LastModified),               db => db.Sites,              e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("Romps",               () => Since(cloud.Romps, since, e => e.LastModified),               db => db.Romps,              e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncRompEquipmentTypes("RompEquipmentTypes", () => Since(cloud.RompEquipmentTypes, since, e => e.LastModified), destIsCloud: false);
        total += SafeSyncTable("Orders",              () => Since(cloud.Orders, since, e => e.LastModified),              db => db.Orders,             e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("SavedReports",        () => Since(cloud.SavedReports, since, e => e.LastModified),        db => db.SavedReports,       e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("CustomColumns",       () => Since(cloud.CustomColumns, since, e => e.LastModified),       db => db.CustomColumns,      e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("CustomRows",          () => Since(cloud.CustomRows, since, e => e.LastModified),          db => db.CustomRows,         e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("Users",               () => Since(cloud.Users, since, e => e.LastModified),               db => db.Users,              e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("AuditEntries",        () => Since(cloud.AuditEntries, since, e => e.LastModified),        db => db.AuditEntries,       e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("AssignmentHistories",  () => Since(cloud.AssignmentHistories, since, e => e.LastModified), db => db.AssignmentHistories,e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("ForecastTypes",          () => Since(cloud.ForecastTypes, forecastSince, e => e.LastModified),          db => db.ForecastTypes,          e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("ForecastFields",         () => Since(cloud.ForecastFields, forecastSince, e => e.LastModified),         db => db.ForecastFields,         e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("ForecastHeaderMappings", () => Since(cloud.ForecastHeaderMappings, forecastSince, e => e.LastModified), db => db.ForecastHeaderMappings, e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("ForecastImports",        () => Since(cloud.ForecastImports, forecastSince, e => e.LastModified),        db => db.ForecastImports,        e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncTable("ForecastRows",           () => Since(cloud.ForecastRows, forecastSince, e => e.LastModified),           db => db.ForecastRows,           e => e.Id, e => e.LastModified, destIsCloud: false);
        total += SafeSyncConfigs("AppConfigs",        () => Since(cloud.AppConfigs, since, c => c.LastModified), destIsCloud: false);

        return total;
    }

    /// <summary>
    /// Pushes one table's changed records into a dedicated, table-scoped context/SaveChanges —
    /// isolating this table's failure from every other table in the same sync cycle. Returns 0
    /// (and reports/logs) on failure instead of throwing, so the caller's loop keeps going.
    /// </summary>
    private int SafeSyncTable<T>(
        string tableName,
        Func<IReadOnlyList<T>> loadSource,
        Func<AppDbContext, DbSet<T>> getDestSet,
        Func<T, int> getId,
        Func<T, DateTime> getLastMod,
        bool destIsCloud,
        DateTime? pushStamp = null) where T : class
    {
        try
        {
            var source = loadSource();
            AddProgress(source.Count);
            if (source.Count == 0) return 0;
            using var dest = destIsCloud
                ? new AppDbContext(forceCloud: true) { SkipLastModifiedStamp = true }
                : new AppDbContext { SkipLastModifiedStamp = true };
            var applied = pushStamp.HasValue ? new List<(int Id, DateTime Orig)>() : null;
            int count = BatchPush(source, getDestSet(dest), dest, getId, getLastMod,
                                  stamp: pushStamp, applied: applied);
            if (count > 0) dest.SaveChanges();
            if (applied is { Count: > 0 } && pushStamp is { } st)
                RestampLocal(getDestSet, getId, getLastMod, applied, st);
            return count;
        }
        catch (Exception ex)
        {
            var dir = destIsCloud ? "Push" : "Pull";
            Report($"{dir} error ({tableName}): {InnerMessage(ex)}");
            DebugLog($"FULL {dir} error ({tableName}): " + ex);
            return 0;
        }
    }

    /// <summary>
    /// RompEquipmentTypes has no meaningful Id-stability across machines — see
    /// InitialPullRompEquipmentTypes — so, like that method, this matches on the natural key
    /// (RompId, EquipmentCategoryId) instead of Id. The old code path used the generic
    /// Id-based BatchPush here too, which is what let two machines' independently-created rows
    /// for the same (RompId, EquipmentCategoryId) pair collide on the table's unique
    /// constraint and (before this table got its own isolated SaveChanges above) take the rest
    /// of that sync cycle down with it.
    /// </summary>
    private int SafeSyncRompEquipmentTypes(
        string tableName, Func<IReadOnlyList<RompEquipmentType>> loadSource, bool destIsCloud)
    {
        try
        {
            var source = loadSource();
            AddProgress(source.Count);
            if (source.Count == 0) return 0;
            using var dest = destIsCloud
                ? new AppDbContext(forceCloud: true) { SkipLastModifiedStamp = true }
                : new AppDbContext { SkipLastModifiedStamp = true };

            // Batch-load every existing (RompId, EquipmentCategoryId) pair in ONE query instead
            // of one round-trip per source record — the original per-record FirstOrDefault
            // lookup was fine for the small handful of rows a normal 30s cycle sees, but on the
            // first cycle after a long sync outage (or any time "since" resets to MinValue) this
            // table can have thousands of pending rows, and a few thousand sequential round
            // trips to the cloud is what actually caused a multi-minute stall here.
            var existing = dest.RompEquipmentTypes.ToDictionary(x => (x.RompId, x.EquipmentCategoryId));

            int count = 0;
            foreach (var record in source)
            {
                var key = (record.RompId, record.EquipmentCategoryId);
                if (existing.TryGetValue(key, out var match))
                {
                    if (record.LastModified > match.LastModified)
                    {
                        match.LastModified = record.LastModified;
                        count++;
                    }
                }
                else
                {
                    var added = new RompEquipmentType
                    {
                        RompId              = record.RompId,
                        EquipmentCategoryId = record.EquipmentCategoryId,
                        LastModified        = record.LastModified,
                    };
                    dest.RompEquipmentTypes.Add(added);
                    existing[key] = added; // guards against duplicate keys within source itself
                    count++;
                }
            }
            if (count > 0) dest.SaveChanges();
            return count;
        }
        catch (Exception ex)
        {
            var dir = destIsCloud ? "Push" : "Pull";
            Report($"{dir} error ({tableName}): {InnerMessage(ex)}");
            DebugLog($"FULL {dir} error ({tableName}): " + ex);
            return 0;
        }
    }

    private int SafeSyncConfigs(string tableName, Func<IReadOnlyList<AppConfig>> loadSource, bool destIsCloud,
                                DateTime? pushStamp = null)
    {
        try
        {
            var source = loadSource();
            AddProgress(source.Count);
            if (source.Count == 0) return 0;
            using var dest = destIsCloud
                ? new AppDbContext(forceCloud: true) { SkipLastModifiedStamp = true }
                : new AppDbContext { SkipLastModifiedStamp = true };
            var applied = pushStamp.HasValue ? new List<(string Key, DateTime Orig)>() : null;
            int count = BatchPushConfigs(source, dest, pushStamp, applied);
            if (count > 0) dest.SaveChanges();
            if (applied is { Count: > 0 } && pushStamp is { } st)
            {
                try
                {
                    var orig = applied.ToDictionary(a => a.Key, a => a.Orig);
                    using var local = new AppDbContext { SkipLastModifiedStamp = true };
                    var keys = orig.Keys.ToList();
                    foreach (var c in local.AppConfigs.Where(c => keys.Contains(c.Key)))
                        if (c.LastModified == orig[c.Key]) c.LastModified = st;
                    local.SaveChanges();
                }
                catch (Exception ex) { DebugLog("Local restamp failed (AppConfigs): " + ex.Message); }
            }
            return count;
        }
        catch (Exception ex)
        {
            var dir = destIsCloud ? "Push" : "Pull";
            Report($"{dir} error ({tableName}): {InnerMessage(ex)}");
            DebugLog($"FULL {dir} error ({tableName}): " + ex);
            return 0;
        }
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
    ///   (equal is applied too when <paramref name="applyOnEqual"/> — startup full pull only)
    ///
    /// Why applyOnEqual exists: a build that predates a column (e.g. Locations.AssignedPmId)
    /// pulls a row, copies its LastModified, and silently drops the column it doesn't know
    /// about. From then on the timestamps match, so every later "newer only" sync — including
    /// the full startup pull — skips it forever. This happened to 3 cluster assignments on
    /// 2026-10-05. Re-applying equal-timestamp rows at startup heals that; EF's change tracker
    /// only issues an UPDATE for rows whose values actually differ, so for normal rows it's free.
    /// </summary>
    private static int BatchPush<T>(
        IReadOnlyList<T>  source,
        DbSet<T>          destSet,
        DbContext         destCtx,
        Func<T, int>      getId,
        Func<T, DateTime> getLastMod,
        bool              applyOnEqual = false,
        DateTime?         stamp        = null,
        List<(int Id, DateTime Orig)>? applied = null) where T : class
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
                var orig = getLastMod(record);
                destSet.Add(record);
                if (stamp is { } st) { destCtx.Entry(record).Property("LastModified").CurrentValue = st; applied?.Add((id, orig)); }
                count++;
            }
            else if (getLastMod(record) > getLastMod(existing))
            {
                // The conflict check above uses the EDIT time (last writer wins); only the stored
                // value is then replaced by the arrival stamp - see _lastPullServerUtc.
                var orig = getLastMod(record);
                destCtx.Entry(existing).CurrentValues.SetValues(record);
                if (stamp is { } st) { destCtx.Entry(existing).Property("LastModified").CurrentValue = st; applied?.Add((id, orig)); }
                count++;
            }
            else if (applyOnEqual && getLastMod(record) == getLastMod(existing))
            {
                var entry = destCtx.Entry(existing);
                entry.CurrentValues.SetValues(record);
                if (entry.State == EntityState.Modified) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// After a push, copies the arrival stamp onto this machine's own copies of the pushed rows,
    /// so local and cloud agree (otherwise the next pull would see the cloud copy as "newer" and
    /// re-download this machine's own change, firing a bogus "changes from another computer"
    /// refresh). Skips any row edited again since it was read for the push — that newer edit
    /// must keep its own timestamp so it gets pushed too.
    /// </summary>
    private static void RestampLocal<T>(
        Func<AppDbContext, DbSet<T>> getSet, Func<T, int> getId, Func<T, DateTime> getLastMod,
        List<(int Id, DateTime Orig)> applied, DateTime stamp) where T : class
    {
        try
        {
            var orig = applied.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Orig);
            var ids  = orig.Keys.ToList();
            using var local = new AppDbContext { SkipLastModifiedStamp = true };
            var set = getSet(local);
            for (int i = 0; i < ids.Count; i += 500)
            {
                var chunk = ids.Skip(i).Take(500).ToList();
                foreach (var e in set.Where(e => chunk.Contains(EF.Property<int>(e, "Id"))))
                    if (getLastMod(e) == orig[getId(e)])
                        local.Entry(e).Property("LastModified").CurrentValue = stamp;
            }
            local.SaveChanges();
        }
        catch (Exception ex) { DebugLog("Local restamp failed (harmless — next pull re-syncs it): " + ex.Message); }
    }

    /// <summary>AppConfig has a string PK (Key) — batch version.</summary>
    private static int BatchPushConfigs(IReadOnlyList<AppConfig> source, DbContext destCtx,
                                        DateTime? stamp = null, List<(string Key, DateTime Orig)>? applied = null)
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
                var orig = record.LastModified;
                destSet.Add(record);
                if (stamp is { } st) { record.LastModified = st; applied?.Add((record.Key, orig)); }
                count++;
            }
            else if (record.LastModified > existing.LastModified)
            {
                var orig = record.LastModified;
                destCtx.Entry(existing).CurrentValues.SetValues(record);
                if (stamp is { } st) { existing.LastModified = st; applied?.Add((record.Key, orig)); }
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
        using var cloud = new AppDbContext(forceCloud: true) { SkipLastModifiedStamp = true };

        ForceMergeTable(local.PMs.AsNoTracking().ToList(),                cloud.PMs,                cloud, e => e.Id);
        ForceMergeTable(local.Locations.AsNoTracking().ToList(),           cloud.Locations,          cloud, e => e.Id);
        ForceMergeTable(local.RompStatuses.AsNoTracking().ToList(),        cloud.RompStatuses,       cloud, e => e.Id);
        ForceMergeTable(local.AssignmentRules.AsNoTracking().ToList(),     cloud.AssignmentRules,    cloud, e => e.Id);
        ForceMergeTable(local.EquipmentCategories.AsNoTracking().ToList(), cloud.EquipmentCategories,cloud, e => e.Id);
        ForceMergeTable(local.EquipmentKeywords.AsNoTracking().ToList(),   cloud.EquipmentKeywords,  cloud, e => e.Id);
        ForceMergeTable(local.SubRegions.AsNoTracking().ToList(),            cloud.SubRegions,           cloud, e => e.Id);
        ForceMergeTable(local.TeamMembers.AsNoTracking().ToList(),            cloud.TeamMembers,           cloud, e => e.Id);
        ForceMergeTable(local.Campuses.AsNoTracking().ToList(),            cloud.Campuses,           cloud, e => e.Id);
        ForceMergeTable(local.Sites.AsNoTracking().ToList(),               cloud.Sites,              cloud, e => e.Id);
        ForceMergeTable(local.Romps.AsNoTracking().ToList(),               cloud.Romps,              cloud, e => e.Id);
        ForceMergeTable(local.RompEquipmentTypes.AsNoTracking().ToList(),  cloud.RompEquipmentTypes, cloud, e => e.Id);
        ForceMergeTable(local.SavedReports.AsNoTracking().ToList(),        cloud.SavedReports,       cloud, e => e.Id);
        ForceMergeTable(local.CustomColumns.AsNoTracking().ToList(),       cloud.CustomColumns,      cloud, e => e.Id);
        ForceMergeTable(local.CustomRows.AsNoTracking().ToList(),          cloud.CustomRows,         cloud, e => e.Id);
        ForceMergeTable(local.Users.AsNoTracking().ToList(),               cloud.Users,              cloud, e => e.Id);
        ForceMergeTable(local.AuditEntries.AsNoTracking().ToList(),        cloud.AuditEntries,       cloud, e => e.Id);
        ForceMergeTable(local.AssignmentHistories.AsNoTracking().ToList(), cloud.AssignmentHistories,cloud, e => e.Id);
        ForceMergeTable(local.ForecastTypes.AsNoTracking().ToList(),          cloud.ForecastTypes,          cloud, e => e.Id);
        ForceMergeTable(local.ForecastFields.AsNoTracking().ToList(),         cloud.ForecastFields,         cloud, e => e.Id);
        ForceMergeTable(local.ForecastHeaderMappings.AsNoTracking().ToList(), cloud.ForecastHeaderMappings, cloud, e => e.Id);
        ForceMergeTable(local.ForecastImports.AsNoTracking().ToList(),        cloud.ForecastImports,        cloud, e => e.Id);
        ForceMergeTable(local.ForecastRows.AsNoTracking().ToList(),           cloud.ForecastRows,           cloud, e => e.Id);
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
        DebugLog(msg);
    }

    // TEMPORARY diagnostic logging — appends every sync status line (including full exception
    // detail for errors) to a local file so a failure can be inspected without needing to attach
    // a debugger. Safe to remove once the current sync issue is root-caused.
    private static void DebugLog(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PMDashboard");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "sync-debug.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { /* diagnostic only */ }
    }

    /// <summary>
    /// Builds and raises a short "who changed what" summary for the rows this pull just
    /// brought in, using the LastModifiedBy column (Orders/Sites/Romps only — the entities that
    /// column exists on). This machine's own changes are excluded, since they aren't news to
    /// the person sitting at it. If nothing is attributable to someone else, falls back to a
    /// generic "synced from another computer" message rather than staying silent — see the
    /// DataChanged doc. Best-effort: a failure here must never break the sync cycle itself.
    /// </summary>
    private void ReportDataChanged(DateTime since, int pulled)
    {
        if (DataChanged == null) return;

        try
        {
            using var cloud = new AppDbContext(forceCloud: true);
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

            DataChanged?.Invoke(parts.Count > 0
                ? string.Join("  •  ", parts)
                : FallbackSummary(pulled));
        }
        catch
        {
            // The summary query is best-effort, but the refresh signal is not — pages still
            // need to reload even if the cloud lookup for "who changed it" failed.
            try { DataChanged?.Invoke(FallbackSummary(pulled)); } catch { }
        }
    }

    private static string FallbackSummary(int pulled) =>
        $"{pulled} change{(pulled == 1 ? "" : "s")} synced from another computer";
}

public class ClientVersionInfo
{
    public string    Version      { get; set; } = "";
    public DateTime? BuildDateUtc { get; set; }
    public string    User         { get; set; } = "";
    public DateTime  LastSeenUtc  { get; set; }
}

public record ClientVersionEntry(string Machine, ClientVersionInfo Info)
{
    public Version? ParsedVersion => AppVersion.Parse(Info.Version);
}
