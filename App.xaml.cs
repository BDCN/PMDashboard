using System.Windows;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard;

public partial class App : Application
{
    public static AutoImportService AutoImport   { get; } = new AutoImportService();
    public static CloudSyncService  CloudSync    { get; } = new CloudSyncService();
    public static NetworkChangeWatcher NetworkWatcher { get; } = new NetworkChangeWatcher();

    /// <summary>Re-evaluates the header banner (Travel Mode reminder / outdated-version warning).</summary>
    public static void ShowTravelBanner() =>
        (Current?.MainWindow as MainWindow)?.RefreshBanner();

    public App()
    {
        // Registered in the constructor — before OnStartup runs at all — so even an exception
        // during the earliest startup work (DatabaseConfig.Load, EnsureCreated, migrations) is
        // caught instead of producing exactly the kind of orphaned, no-window, unkillable process
        // this was added to stop: previously NOTHING in the app caught an exception that escaped
        // this far, on any thread. async void OnStartup in particular marshals its exceptions
        // back through the Dispatcher, so DispatcherUnhandledException alone covers it without
        // needing to wrap OnStartup's ~600-line body in its own try/catch.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static string CrashLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PMDashboard", "crash.log");

    private static void LogFatalException(string source, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(CrashLogPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(CrashLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{source}]  SAT {AppVersion.Full} on {Environment.MachineName}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging itself must never throw from inside a crash handler */ }
    }

    /// <summary>
    /// Anything thrown on the UI thread that nothing else caught lands here — this is the
    /// handler that actually stops a crash from becoming a silent, orphaned process: it logs the
    /// real exception (previously lost entirely), shows the user something concrete happened
    /// instead of the window just vanishing, disposes the background sync/import services so
    /// their Timers don't keep running past the point the app is trying to close, and only then
    /// shuts down deliberately — rather than leaving the default WPF crash behavior (or lack of
    /// any handler at all) to decide what state the process is left in.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // An assignment blocked by the team rule isn't a crash — explain it and carry on. It's
        // thrown before anything is saved (see TeamRules), so there's nothing to undo.
        for (var ex = e.Exception; ex != null; ex = ex.InnerException)
        {
            if (ex is not TeamRuleException rule) continue;
            e.Handled = true;
            try { MessageBox.Show(rule.Message, "Outside the Team's Area", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
            return;
        }

        LogFatalException("DispatcherUnhandledException", e.Exception);
        e.Handled = true; // we're taking over the shutdown ourselves, not letting WPF's default crash path run

        try
        {
            MessageBox.Show(
                $"An unexpected error occurred and the application needs to close.\n\n{e.Exception.Message}\n\n" +
                $"Details were saved to:\n{CrashLogPath}",
                "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* the error dialog itself must never be what crashes the crash handler */ }

        try { NetworkWatcher.Dispose(); } catch { }
        try { CloudSync.Dispose(); } catch { }
        try { AutoImport.Dispose(); } catch { }
        try { Shutdown(1); } catch { Environment.Exit(1); } // last resort if even a clean Shutdown can't run
    }

    /// <summary>Exceptions on a non-UI thread (e.g. a raw Timer/ThreadPool callback, not a Task)
    /// bypass the Dispatcher entirely and land here instead — IsTerminating is effectively always
    /// true by the time this fires, so there's no "handle and continue" option, only logging
    /// whatever we can before the process goes down for a real post-mortem next time.</summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        LogFatalException($"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})", e.ExceptionObject as Exception);

    /// <summary>A faulted Task whose exception nobody ever awaited/observed (several background
    /// operations in CloudSyncService/AutoImportService are fire-and-forget Task.Run calls).
    /// Doesn't crash the process in modern .NET, but logging it is the only way to ever find out
    /// one of those silently failed instead of just... not happening.</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatalException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// Network mode (not traveling): checks the shared database file can be opened before
    /// anything touches it, offering Retry / switch this computer to Local mode / Exit; then
    /// makes sure it uses the rollback journal, not WAL (see NetworkDb). False → close SAT.
    /// </summary>
    private static bool EnsureNetworkDatabaseReachable()
    {
        if (!NetworkDb.IsShared) return true;
        var path = DatabaseConfig.NetworkDbPath;

        while (NetworkDb.CheckReachable(path) is { } problem)
        {
            var choice = MessageBox.Show(
                problem + "\n\n" +
                "Yes — try again\n" +
                "No — switch this computer to Local mode (its own separate database) so you can open Configuration and fix the path\n" +
                "Cancel — close SAT",
                "SAT — Network Database Unavailable", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes) continue;
            if (choice == MessageBoxResult.No) { DatabaseConfig.ApplyLocal(); return true; }
            return false;
        }

        try
        {
            if (NetworkDb.EnsureRollbackJournal(path) == "wal")
                MessageBox.Show(
                    "The network database is in WAL journal mode, which isn't safe when several computers share it " +
                    "over the network, and it couldn't be switched because another computer has it open.\n\n" +
                    "Close SAT on every other computer, then restart SAT here — it switches over automatically.",
                    "SAT — Network Database", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* checked again next start */ }
        return true;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Must load before the first AppDbContext is constructed so OnConfiguring
        // knows which provider / path to use.
        DatabaseConfig.Load();
        IdAllocator.InitFromConfig(); // per-machine Id ranges in Cloud mode — see IdAllocator

        // Network mode: make sure the shared file is really there BEFORE anything opens it —
        // EnsureCreated below would otherwise quietly create a new, empty database at that path
        // (share not mapped yet, VPN down, typo'd path) and this computer would show no data.
        if (!EnsureNetworkDatabaseReachable()) { Shutdown(); return; }

        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        // ── Schema migrations for existing databases ──────────────────────────

        // RompStatuses table (added with the configurable status feature)
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS RompStatuses (
                Id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name           TEXT    NOT NULL,
                IsComplete     INTEGER NOT NULL,
                CountsAsActive INTEGER NOT NULL,
                ScoreWeight    REAL    NOT NULL DEFAULT 0,
                SortOrder      INTEGER NOT NULL,
                IsBuiltIn      INTEGER NOT NULL
            )
            """);

        // ScoreCapacity column on PMs (added with the score-based utilization feature)
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE PMs ADD COLUMN ScoreCapacity REAL NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }

        // PmMultiplier column on PMs — nullable integer 1–10 (null = treat as 1.0 factor)
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE PMs ADD COLUMN PmMultiplier INTEGER"); }
        catch { /* column already exists */ }

        // ScoreWeight column on RompStatuses — now REAL; SQLite stores the value unchanged
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE RompStatuses ADD COLUMN ScoreWeight REAL NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }

        // IsDisabled column on RompStatuses — hides a status from "assign a status" pickers
        // without touching ROMPs already on it (StatusConfig.Selectable vs .All).
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE RompStatuses ADD COLUMN IsDisabled INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }

        // Color column on RompStatuses — indicator-dot color for Campus/ROMP lists (Configuration
        // page color picker). Seeded, ONLY at the moment the column is first added, to match the
        // colors already hardcoded into SitesPage.xaml before this feature, so existing installs
        // see no visual change until an admin picks something different. Must not re-seed on
        // every later startup — that would stomp an admin's deliberate choice if it ever happened
        // to match the pre-seed default, same trap the ScoreWeight migration above works around.
        bool colorColumnJustAdded = false;
        try
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE RompStatuses ADD COLUMN Color TEXT NOT NULL DEFAULT '#94A3B8'");
            colorColumnJustAdded = true;
        }
        catch { /* column already exists */ }

        // Also seed when every status still carries the column default: the first build of this
        // migration crashed right after the ALTER (Dictionary.ContainsKey isn't SQL-translatable),
        // so those installs have the column but were never seeded — and colorColumnJustAdded is
        // false for them forever after. An admin deliberately setting every status to the same
        // grey is not a realistic choice to protect.
        bool neverSeeded = !colorColumnJustAdded
            && db.RompStatuses.Any()
            && db.RompStatuses.All(r => r.Color == "#94A3B8");

        if (colorColumnJustAdded || neverSeeded)
        {
            var seedColors = new Dictionary<string, string>
            {
                ["Ordered"]      = "#94A3B8",
                ["OFCI"]         = "#2563EB",
                ["Construction"] = "#F59E0B",
                ["CX"]           = "#F97316",
                ["Complete"]     = "#22C55E",
            };
            var seedNames = seedColors.Keys.ToList();   // List.Contains translates to SQL IN; Dictionary.ContainsKey doesn't
            foreach (var rs in db.RompStatuses.Where(r => seedNames.Contains(r.Name)).ToList())
                rs.Color = seedColors[rs.Name];
            // No LastModified stamp: in Cloud mode the cloud may already hold colors an admin
            // picked elsewhere. Stamping would make these local defaults "newer" and push them
            // over those choices; unstamped, the startup pull's equal-timestamp re-apply (see
            // CloudSyncService.BatchPush applyOnEqual) replaces them with the cloud's values.
            db.SkipLastModifiedStamp = true;
            db.SaveChanges();
            db.SkipLastModifiedStamp = false;
        }

        // EquipmentWeights table (legacy — kept for schema compat, scoring now uses EquipmentCategories)
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS EquipmentWeights (
                Id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name       TEXT    NOT NULL,
                Keyword    TEXT    NOT NULL,
                Weight     INTEGER NOT NULL,
                IsExcluded INTEGER NOT NULL,
                IsBuiltIn  INTEGER NOT NULL,
                SortOrder  INTEGER NOT NULL
            )
            """);

        // EquipmentCategories + EquipmentKeywords — the active scoring tables
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS EquipmentCategories (
                Id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name       TEXT    NOT NULL,
                Weight     REAL    NOT NULL DEFAULT 0,
                IsExcluded INTEGER NOT NULL,
                IsBuiltIn  INTEGER NOT NULL,
                SortOrder  INTEGER NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS EquipmentKeywords (
                Id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                CategoryId INTEGER NOT NULL REFERENCES EquipmentCategories(Id) ON DELETE CASCADE,
                Keyword    TEXT    NOT NULL
            )
            """);

        // RompEquipmentTypes — new junction table linking ROMPs to their equipment categories
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS RompEquipmentTypes (
                Id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                RompId              INTEGER NOT NULL REFERENCES Romps(Id) ON DELETE CASCADE,
                EquipmentCategoryId INTEGER NOT NULL REFERENCES EquipmentCategories(Id) ON DELETE CASCADE,
                UNIQUE(RompId, EquipmentCategoryId)
            )
            """);

        // AssignmentHistories — one row per (Site, PM) assignment period; survives reassignment
        // and site closure since Site.AssignedPmId only ever holds the current PM. See
        // AssignmentHistoryService, which is the only code that should write to this table.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS AssignmentHistories (
                Id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                SiteId          INTEGER NOT NULL REFERENCES Sites(Id) ON DELETE CASCADE,
                PmId            INTEGER NOT NULL REFERENCES PMs(Id) ON DELETE RESTRICT,
                AssignedAtUtc   TEXT    NOT NULL,
                UnassignedAtUtc TEXT    NULL,
                AssignedBy      TEXT    NOT NULL DEFAULT '',
                UnassignedBy    TEXT    NULL,
                LastModified    TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00'
            )
            """);

        // ── Forecast Planning ────────────────────────────────────────────────
        // ForecastTypes: admin-managed sheet types (the 5 real-world sheets are seeded below).
        // ForecastFields: column definitions — ForecastTypeId NULL = shared across every type
        // (Site/Region/Edd/etc., the columns that actually recur across all 5 real sheets),
        // non-NULL = specific to one type. Key is stable ("ff{Id}"), same convention as
        // CustomColumn.Key, so renaming Name never orphans values already stored under it.
        // ForecastHeaderMappings: remembers "this raw header text, for this type" -> field, so a
        // second upload of the same sheet shape needs no re-mapping. ColumnIndex disambiguates
        // when the same header text appears twice in one sheet's header row (seen for real in
        // one of these sheets — po_number/OSD/MK2/Titus each appear twice on one type).
        // ForecastImports: one row per upload. ForecastRows: one row per data row, values as
        // JSON keyed by ForecastField.Key (same pattern as CustomRow.ValuesJson).
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ForecastTypes (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Code         TEXT    NOT NULL,
                Name         TEXT    NOT NULL,
                SortOrder    INTEGER NOT NULL DEFAULT 0,
                IsBuiltIn    INTEGER NOT NULL DEFAULT 0,
                LastModified TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00'
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ForecastFields (
                Id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ForecastTypeId INTEGER NULL REFERENCES ForecastTypes(Id) ON DELETE RESTRICT,
                Name           TEXT    NOT NULL,
                [Key]          TEXT    NOT NULL,
                FieldType      TEXT    NOT NULL DEFAULT 'Text',
                IsSiteKey      INTEGER NOT NULL DEFAULT 0,
                IsIgnored      INTEGER NOT NULL DEFAULT 0,
                SortOrder      INTEGER NOT NULL DEFAULT 0,
                LastModified   TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00'
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ForecastHeaderMappings (
                Id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ForecastTypeId  INTEGER NOT NULL REFERENCES ForecastTypes(Id) ON DELETE CASCADE,
                SourceHeader    TEXT    NOT NULL,
                ColumnIndex     INTEGER NOT NULL DEFAULT -1,
                ForecastFieldId INTEGER NOT NULL REFERENCES ForecastFields(Id) ON DELETE CASCADE,
                LastModified    TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00'
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ForecastImports (
                Id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ForecastTypeId INTEGER NOT NULL REFERENCES ForecastTypes(Id) ON DELETE RESTRICT,
                FileName       TEXT    NOT NULL DEFAULT '',
                UploadedBy     TEXT    NOT NULL DEFAULT '',
                UploadedAtUtc  TEXT    NOT NULL,
                RowCount       INTEGER NOT NULL DEFAULT 0,
                LastModified   TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00'
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ForecastRows (
                Id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ForecastImportId INTEGER NOT NULL REFERENCES ForecastImports(Id) ON DELETE CASCADE,
                ForecastTypeId   INTEGER NOT NULL REFERENCES ForecastTypes(Id) ON DELETE RESTRICT,
                SiteCode         TEXT    NULL,
                SiteId           INTEGER NULL REFERENCES Sites(Id) ON DELETE SET NULL,
                ValuesJson       TEXT    NOT NULL DEFAULT '{{}}',
                LastModified     TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00'
            )
            """);

        // Seed the 5 real-world sheet types + the canonical fields that recur across all of
        // them (confirmed from an actual column-header sample) as global (ForecastTypeId NULL)
        // fields. Everything else each sheet carries is type-specific and gets created on the
        // fly the first time that header is seen during an upload — see ForecastImportService.
        if (!db.ForecastTypes.Any())
        {
            var types = new[]
            {
                new ForecastType { Code = "O", Name = "Type O", SortOrder = 1, IsBuiltIn = true },
                new ForecastType { Code = "M", Name = "Type M", SortOrder = 2, IsBuiltIn = true },
                new ForecastType { Code = "L", Name = "Type L", SortOrder = 3, IsBuiltIn = true },
                new ForecastType { Code = "B", Name = "Type B", SortOrder = 4, IsBuiltIn = true },
                new ForecastType { Code = "P", Name = "Type P", SortOrder = 5, IsBuiltIn = true },
            };
            db.ForecastTypes.AddRange(types);
            db.SaveChanges();

            var globalFields = new (string Name, string FieldType, bool IsSiteKey)[]
            {
                ("Site",                  "Text",   true),
                ("Region",                "Text",   false),
                ("Country",               "Text",   false),
                ("Cluster",               "Text",   false),
                ("FBN",                   "Text",   false),
                ("PFHO",                  "Date",   false),
                ("PO Number",             "Text",   false),
                ("EDD",                   "Date",   false),
                ("Room Type",             "Text",   false),
                ("NBD",                   "Date",   false),
                ("Qty",                   "Number", false),
                ("Size",                  "Text",   false),
                ("Build Type ID",         "Text",   false),
                ("Config New",            "Text",   false),
                ("Last PRO Sche Update",  "Date",   false),
                ("NBD Prev Wk",           "Date",   false),
                ("PO Created At",         "Date",   false),
            };
            int i = 0;
            foreach (var (name, fieldType, isSiteKey) in globalFields)
            {
                var field = new ForecastField
                {
                    ForecastTypeId = null,
                    Name           = name,
                    FieldType      = fieldType,
                    IsSiteKey      = isSiteKey,
                    SortOrder      = i++,
                };
                db.ForecastFields.Add(field);
                db.SaveChanges(); // need field.Id before Key can be set
                field.Key = $"ff{field.Id}";
                db.SaveChanges();
            }
        }

        // SavedReports — Report Builder named presets (filters/columns/chart config as JSON)
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS SavedReports (
                Id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name                TEXT    NOT NULL,
                Mode                TEXT    NOT NULL,
                FilterJson          TEXT    NOT NULL DEFAULT '{{}}',
                SelectedColumnsJson TEXT    NOT NULL DEFAULT '[]',
                ChartGroupByField   TEXT,
                ChartAggregateField TEXT,
                ChartAggregateOp    TEXT    NOT NULL DEFAULT 'Count'
            )
            """);

        // CustomColumns / CustomRows — user-defined dynamic columns attached to real Orders/ROMPs
        // rows on the Custom Data grid ("Mode" mirrors Report Builder's Orders/SitesRomps split).
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS CustomColumns (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Mode         TEXT    NOT NULL DEFAULT 'Orders',
                Name         TEXT    NOT NULL,
                [Key]        TEXT    NOT NULL,
                ColumnType   TEXT    NOT NULL DEFAULT 'Text',
                SortOrder    INTEGER NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS CustomRows (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Mode         TEXT    NOT NULL DEFAULT 'Orders',
                EntityId     INTEGER NOT NULL DEFAULT 0,
                ValuesJson   TEXT    NOT NULL DEFAULT '{{}}',
                SortOrder    INTEGER NOT NULL DEFAULT 0
            )
            """);

        // Retrofit Mode/EntityId onto CustomColumns/CustomRows for installs where those tables
        // were already created by an earlier version of the app (before rows were tied to
        // real Orders/ROMPs entities).
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE CustomColumns ADD COLUMN Mode TEXT NOT NULL DEFAULT 'Orders'"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE CustomRows ADD COLUMN Mode TEXT NOT NULL DEFAULT 'Orders'"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE CustomRows ADD COLUMN EntityId INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }

        // Users / AuditEntries — accounts and the append-only interaction log
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS Users (
                Id                 INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Email              TEXT    NOT NULL,
                FirstName          TEXT    NOT NULL DEFAULT '',
                LastName           TEXT    NOT NULL DEFAULT '',
                PasswordHash       TEXT    NOT NULL DEFAULT '',
                Role               TEXT    NOT NULL DEFAULT 'User',
                IsActive           INTEGER NOT NULL DEFAULT 1,
                MustChangePassword INTEGER NOT NULL DEFAULT 0,
                LastLoginUtc       TEXT    NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS AuditEntries (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT    NOT NULL,
                UserEmail    TEXT    NOT NULL DEFAULT '',
                UserName     TEXT    NOT NULL DEFAULT '',
                Action       TEXT    NOT NULL DEFAULT '',
                EntityType   TEXT    NOT NULL DEFAULT '',
                EntityId     INTEGER NULL,
                Details      TEXT    NOT NULL DEFAULT '',
                MachineName  TEXT    NOT NULL DEFAULT ''
            )
            """);

        // LastModifiedBy — who last wrote the row; only on the entities the reports/notifications
        // attribute changes to. EF includes it in every SELECT once the model has it, so it must
        // exist before the first query, same rule as LastModified below.
        foreach (var tbl in new[] { "Orders", "Romps", "Sites" })
        {
            // tbl is a hardcoded literal above, never user input, and a table name can't be
            // parameterized via ExecuteSql anyway (only values can) — EF1002 suppressed.
#pragma warning disable EF1002
            try { db.Database.ExecuteSqlRaw(
                $"ALTER TABLE {tbl} ADD COLUMN LastModifiedBy TEXT NOT NULL DEFAULT ''"); }
            catch { /* column already exists on this table */ }
#pragma warning restore EF1002
        }

        // Site.IsClosed — explicit close flag for the "Close All ROMPs & This Site"/Location
        // cascade, needed because a zero-ROMP site can never reach SiteStatus "Complete" through
        // ROMP data alone (see Site.cs).
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE Sites ADD COLUMN IsClosed INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }

        // Site.StatusWhenEmpty — stand-in status for a zero-ROMP campus, set via the same "Set
        // All ROMPs to Status" picker used when ROMPs exist. Nullable, no seeding needed (null
        // just falls back to the existing IsClosed-based guess — see Site.cs).
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE Sites ADD COLUMN StatusWhenEmpty TEXT NULL"); }
        catch { /* column already exists */ }

        // ── Campuses: Cluster → Campus → Site → ROMP (see Data/Models/Campus.cs) ──
        // New table plus three Site columns. CampusArrangementService places every site under a
        // campus after startup (CloudSyncService runs it after the initial pull in Cloud mode).
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS Campuses (
                Id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name           TEXT    NOT NULL,
                LocationId     INTEGER NOT NULL,
                AssignedPmId   INTEGER NULL,
                LastModified   TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00',
                LastModifiedBy TEXT    NOT NULL DEFAULT '',
                FOREIGN KEY (LocationId)   REFERENCES Locations(Id) ON DELETE CASCADE,
                FOREIGN KEY (AssignedPmId) REFERENCES PMs(Id)       ON DELETE SET NULL
            )
            """);
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Sites ADD COLUMN CampusId INTEGER NULL"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Sites ADD COLUMN CampusManual INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Sites ADD COLUMN PmOverride INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }

        // ── Teams: Director › PE (sub-region) › PM › PA (see Data/Models/TeamMember.cs) ──
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS SubRegions (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name         TEXT    NOT NULL,
                Region       TEXT    NOT NULL DEFAULT '',
                SortOrder    INTEGER NOT NULL DEFAULT 0,
                LastModified TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00'
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS TeamMembers (
                Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Role         TEXT    NOT NULL,
                FirstName    TEXT    NOT NULL DEFAULT '',
                LastName     TEXT    NOT NULL DEFAULT '',
                Email        TEXT    NOT NULL DEFAULT '',
                Phone        TEXT    NOT NULL DEFAULT '',
                Region       TEXT    NOT NULL DEFAULT '',
                SubRegionId  INTEGER NULL,
                PmId         INTEGER NULL,
                IsActive     INTEGER NOT NULL DEFAULT 1,
                LastModified TEXT    NOT NULL DEFAULT '2000-01-01 00:00:00',
                FOREIGN KEY (SubRegionId) REFERENCES SubRegions(Id) ON DELETE SET NULL,
                FOREIGN KEY (PmId)        REFERENCES PMs(Id)        ON DELETE SET NULL
            )
            """);
        try { db.Database.ExecuteSqlRaw("ALTER TABLE PMs ADD COLUMN PeId INTEGER NULL"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Locations ADD COLUMN SubRegionId INTEGER NULL"); }
        catch { /* column already exists */ }
        // A PM (and their PAs) can be on several sub-regions: a default plus others (see PM.cs).
        try { db.Database.ExecuteSqlRaw("ALTER TABLE PMs ADD COLUMN SubRegionId INTEGER NULL"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE PMs ADD COLUMN OtherSubRegionIds TEXT NOT NULL DEFAULT ''"); }
        catch { /* column already exists */ }
        // TBD placeholders (a PM or PA still to be hired).
        try { db.Database.ExecuteSqlRaw("ALTER TABLE PMs ADD COLUMN IsPlaceholder INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE TeamMembers ADD COLUMN IsPlaceholder INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }
        // A site can belong to a different sub-region than its campus (split campuses).
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Sites ADD COLUMN SubRegionId INTEGER NULL"); }
        catch { /* column already exists */ }
        // A campus can belong to a different sub-region than its cluster (split clusters).
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Campuses ADD COLUMN SubRegionId INTEGER NULL"); }
        catch { /* column already exists */ }

        // ForecastField.IsCampusField — which forecast column names the campus ("az"). Seeded
        // only at the moment the column is first added (so an admin un-ticking it later sticks):
        // Type O's "az" column, and Type L's column SAT named "Cluster", which holds az-style
        // campus codes (CMH050, ATL086) rather than 3-letter clusters. Not stamped, so nothing is
        // pushed — every machine seeds the same way on its own first run.
        bool campusFieldJustAdded = false;
        try
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE ForecastFields ADD COLUMN IsCampusField INTEGER NOT NULL DEFAULT 0");
            campusFieldJustAdded = true;
        }
        catch { /* column already exists */ }
        if (campusFieldJustAdded)
        {
            db.Database.ExecuteSqlRaw("UPDATE ForecastFields SET IsCampusField = 1 WHERE lower(Name) = 'az'");
            db.Database.ExecuteSqlRaw(
                "UPDATE ForecastFields SET IsCampusField = 1 WHERE Name = 'Cluster' AND ForecastTypeId IN " +
                "(SELECT Id FROM ForecastTypes WHERE Code = 'L')");
        }

        // Location.AssignedPmId — direct "owner" PM for a cluster with no campuses to carry a
        // per-Site assignment instead (see Location.cs). Nullable, no seeding needed.
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE Locations ADD COLUMN AssignedPmId INTEGER NULL"); }
        catch { /* column already exists */ }

        // Romp.AssignedPmId — individual-ROMP PM override (see AssignmentResolver); and
        // AssignmentHistories.RompId to let the same history table record ROMP-level overrides
        // alongside campus-level ones (see AssignmentHistoryService.RecordRompChange).
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE Romps ADD COLUMN AssignedPmId INTEGER NULL"); }
        catch { /* column already exists */ }
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE AssignmentHistories ADD COLUMN RompId INTEGER NULL"); }
        catch { /* column already exists */ }

        // ── LastModified column on all tables (must run before any EF query) ────
        // EF Core includes LastModified in every SELECT once the entity model has it.
        // Adding the column here — after all CREATE TABLE blocks but before the first
        // db.Xxx.Any() call — guarantees the column exists before EF reads any row.
        // SQLite ADD COLUMN only allows literal constants as DEFAULT (not expressions in
        // parentheses such as datetime('now')).  Use a fixed timestamp string —
        // existing rows get '2000-01-01 00:00:00', which compares as older than any real
        // LastModified written by SaveChanges, so the first sync only transfers genuinely
        // new or locally-changed rows rather than blindly overwriting everything.
        foreach (var tbl in new[]
        {
            "PMs", "Locations", "Sites", "Romps", "RompEquipmentTypes", "Orders",
            "RompStatuses", "EquipmentCategories", "EquipmentKeywords",
            "AssignmentRules", "AppConfigs", "SavedReports",
            "CustomColumns", "CustomRows", "Users", "AuditEntries"
        })
        {
            try
            {
                db.Database.ExecuteSqlRaw(
                    $"ALTER TABLE {tbl} ADD COLUMN LastModified TEXT NOT NULL " +
                    $"DEFAULT '2000-01-01 00:00:00'");
            }
            catch { /* column already exists on this table */ }
        }

        // ── Seed built-in assignment rules ────────────────────────────────────
        if (!db.AssignmentRules.Any())
        {
            db.AssignmentRules.AddRange(
                new AssignmentRule { Name = "Region Match",          RuleKey = "RegionMatch",          Description = "PM's geographic region matches the site's location region.",        Weight = 3, SortOrder = 1, IsActive = true, IsBuiltIn = true },
                new AssignmentRule { Name = "Location Familiarity",  RuleKey = "LocationFamiliarity",  Description = "PM already manages other sites within this same location.",        Weight = 2, SortOrder = 2, IsActive = true, IsBuiltIn = true },
                new AssignmentRule { Name = "Capacity Availability", RuleKey = "CapacityAvailability", Description = "PM has remaining capacity relative to their configured maximum.",   Weight = 1, SortOrder = 3, IsActive = true, IsBuiltIn = true }
            );
            db.SaveChanges();
        }

        // ── Seed / migrate built-in ROMP statuses with new activity multipliers ─
        // New weights: Complete=0.1, Ordered=0.4, OFCI=0.6, Construction=1.0, CX=1.2
        var newActivityWeights = new Dictionary<string, double>
        {
            ["Ordered"]      = 0.4,
            ["OFCI"]         = 0.6,
            ["Construction"] = 1.0,
            ["CX"]           = 1.2,
            ["Complete"]     = 0.1,
        };

        if (!db.RompStatuses.Any())
        {
            db.RompStatuses.AddRange(
                new RompStatus { Name = "Ordered",      IsComplete = false, CountsAsActive = false, ScoreWeight = 0.4, SortOrder = 1, IsBuiltIn = true, Color = "#94A3B8" },
                new RompStatus { Name = "OFCI",         IsComplete = false, CountsAsActive = false, ScoreWeight = 0.6, SortOrder = 2, IsBuiltIn = true, Color = "#2563EB" },
                new RompStatus { Name = "Construction", IsComplete = false, CountsAsActive = true,  ScoreWeight = 1.0, SortOrder = 3, IsBuiltIn = true, Color = "#F59E0B" },
                new RompStatus { Name = "CX",           IsComplete = false, CountsAsActive = true,  ScoreWeight = 1.2, SortOrder = 4, IsBuiltIn = true, Color = "#F97316" },
                new RompStatus { Name = "Complete",     IsComplete = true,  CountsAsActive = false, ScoreWeight = 0.1, SortOrder = 5, IsBuiltIn = true, Color = "#22C55E" }
            );
            db.SaveChanges();
        }
        else
        {
            // Migrate existing built-in rows to new double activity weights.
            // Only update if the current value still looks like the old integer (≥ 1 and a round number),
            // so we don't overwrite values the user has customised.
            bool changed = false;
            foreach (var rs in db.RompStatuses.Where(r => r.IsBuiltIn))
            {
                if (newActivityWeights.TryGetValue(rs.Name, out double target))
                {
                    // Old integer seeds were 0, 10, 15, 40 — all ≥ 1 except Complete=0.
                    // New values are all < 2. Migrate if the stored value doesn't match new scale.
                    bool looksLikeOldSeed = rs.ScoreWeight >= 2.0 || (rs.Name == "Ordered" && rs.ScoreWeight == 0);
                    if (looksLikeOldSeed)
                    {
                        rs.ScoreWeight = target;
                        changed = true;
                    }
                }
            }
            if (changed) db.SaveChanges();
        }

        // ── Seed / migrate built-in equipment score categories ────────────────
        // New weights: FS Exclusion=0 (excl), PAX=1.6, LV=1.3, MV=1.0, PDC=0.6
        if (!db.EquipmentCategories.Any())
        {
            static EquipmentCategory Cat(string name, double weight, bool excl, int order, params string[] kws) =>
                new()
                {
                    Name = name, Weight = weight, IsExcluded = excl,
                    IsBuiltIn = true, SortOrder = order,
                    Keywords = kws.Select(k => new EquipmentKeyword { Keyword = k }).ToList()
                };

            db.EquipmentCategories.AddRange(
                Cat("FS Exclusion", 0.0, true,  1, "FSPS", "FSWTY", "FS Field"),
                Cat("PAX",          1.6, false, 2, "PIU", "PIE", "CDP", "ATS"),
                Cat("LV",           1.3, false, 3, "LVSW", "CEELV", "LV"),
                Cat("MV",           1.0, false, 4, "MV SWGR", "SWGR", "MV"),
                Cat("PDC",          0.6, false, 5, "PDU", "PDC")
            );
            db.SaveChanges();
        }
        else
        {
            // Migrate existing built-in equipment weights from old integer scale to new doubles.
            var newEquipWeights = new Dictionary<string, double>
            {
                ["PAX"] = 1.6, ["LV"] = 1.3, ["MV"] = 1.0, ["PDU"] = 0.6, ["PDC"] = 0.6
            };
            bool changed = false;
            foreach (var cat in db.EquipmentCategories.Where(c => c.IsBuiltIn && !c.IsExcluded))
            {
                // Old integer seeds were ≥ 10; new values are all < 2.
                if (cat.Weight >= 2.0 && newEquipWeights.TryGetValue(cat.Name, out double target))
                {
                    cat.Weight = target;
                    // Rename PDU → PDC when migrating
                    if (cat.Name == "PDU") cat.Name = "PDC";
                    changed = true;
                }
            }
            if (changed) db.SaveChanges();
        }

        // ── Seed workload scoring parameters into AppConfig ───────────────────
        void SeedConfig(string key, string value)
        {
            if (db.AppConfigs.Find(key) == null)
                db.AppConfigs.Add(new AppConfig { Key = key, Value = value });
        }
        SeedConfig(ConfigKeys.SiteOverhead,      "0.05");
        SeedConfig(ConfigKeys.TargetCapacity,    "259.2");
        SeedConfig(ConfigKeys.NearFullThreshold, "0.80");
        SeedConfig(ConfigKeys.OverloadThreshold, "1.00");
        SeedConfig(ConfigKeys.SkillBase,         "0.40");
        SeedConfig(ConfigKeys.SkillPerPoint,     "0.06");
        SeedConfig(ConfigKeys.CxWindowDays,            "14");
        SeedConfig(ConfigKeys.MaxConcurrentCxPerRegion, "1");
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        SeedConfig(ConfigKeys.CoverageBuildWeight,     CoverageDefaults.BuildWeight.ToString(ic));
        SeedConfig(ConfigKeys.CoverageCxWeight,        CoverageDefaults.CxWeight.ToString(ic));
        SeedConfig(ConfigKeys.CoverageCapacityPerPm,   CoverageDefaults.CapacityPerPm.ToString(ic));
        SeedConfig(ConfigKeys.CoverageHireLeadDays,    CoverageDefaults.HireLeadDays.ToString(ic));
        SeedConfig(ConfigKeys.CoverageHorizonWeeks,    CoverageDefaults.HorizonWeeks.ToString(ic));
        db.SaveChanges();

        // ── One-time migration: populate RompEquipmentTypes from existing orders ─
        // Runs only when the junction table is empty (i.e. first launch after schema upgrade).
        // Uses the same keyword-matching logic the old WorkloadViewModel used to score on-the-fly.
        MigrateEquipmentTypesFromOrders(db);

        // ── Load scoring caches ───────────────────────────────────────────────
        StatusConfig.Reload();
        ScoreConfig.Reload();

        // ── Start cloud background sync (Cloud mode only) — BEFORE auth ────────
        // Started here (not fire-and-forget-later) so it's already running while the
        // Create-Admin/Login dialogs are up, but NOT awaited by default — most launches are an
        // already-known user on a machine that already has a local Users table, and blocking
        // every single startup on a cloud round-trip just to show the Sign In screen cost ~10
        // seconds for no benefit in that common case. Login.xaml.cs's TrySignIn awaits the same
        // pull, but only if a login actually fails locally (see WaitForInitialPullAsync's doc).
        if (CloudSyncService.IsAvailable)
        {
            var intervalStr = new ConfigService().Get(ConfigKeys.CloudSyncInterval, "30");
            int.TryParse(intervalStr, out var interval);
            CloudSync.Start(interval > 0 ? interval : 30); // runs CampusArrangementService after its initial pull
        }
        else
        {
            // Local/Network mode: place any site without a campus now (see CampusArrangementService).
            try { CampusArrangementService.Arrange(); } catch { /* never block startup over it */ }
        }

        // ── Authentication ─────────────────────────────────────────────────────
        // No user exists yet (fresh install) → the first real person creates the admin
        // account directly, choosing their own email and password. Unlike a seeded
        // placeholder password, nothing here is ever a literal in source, so there is
        // nothing to leak into git history.
        // This IS one of the two cases that's worth waiting on the cloud pull for: an empty
        // local Users table could mean a genuinely fresh install, or it could mean a fresh
        // install on a machine whose cloud account data just hasn't arrived yet — without
        // waiting here, the latter would incorrectly show "create a new admin account" instead
        // of recognizing the accounts that already exist in the cloud.
        if (!db.Users.Any())
        {
            if (CloudSyncService.IsAvailable)
                await CloudSync.WaitForInitialPullAsync(TimeSpan.FromSeconds(15));

            if (!db.Users.Any())
            {
                var create = new Dialogs.CreateAdminAccountDialog();
                if (create.ShowDialog() != true) { Shutdown(); return; }
            }
        }

        // ── Dev auto-login (this machine only) ─────────────────────────────────
        // Opted into via LoginDialog's "Skip login on this computer" checkbox — see
        // DevAutoLogin's doc for the security tradeoff. Falls through to the normal login screen
        // whenever the saved account can't be auto-signed-in as-is (missing, deactivated, or a
        // pending forced password change), rather than erroring.
        var autoLoginUser = DevAutoLogin.TryGetSavedEmail() is { } savedEmail
            ? new UserService().FindByEmail(savedEmail)
            : null;

        if (autoLoginUser is { IsActive: true, MustChangePassword: false })
        {
            CurrentUser.SignIn(autoLoginUser);
            new UserService().RecordLogin(autoLoginUser.Id);
            AuditService.Log("AutoLogin", "User", autoLoginUser.Id,
                $"Auto-signed in as {autoLoginUser.Email} (dev auto-login on this machine) — SAT {AppVersion.Short}");
        }
        else
        {
            // ShutdownMode is OnExplicitShutdown (App.xaml) for exactly this: the login/create-
            // account dialog is the only window open, and under the default OnLastWindowClose its
            // closing would end the application before the main window was ever created.
            var login = new Dialogs.LoginDialog();
            if (login.ShowDialog() != true) { Shutdown(); return; }
        }

        // ── Resume semi-automatic import ──────────────────────────────────────
        var cs = new ConfigService();
        if (cs.Get(ConfigKeys.UploadMethod) == "SemiAutomatic")
        {
            var folder  = cs.Get(ConfigKeys.ImportFolder);
            var pollStr = cs.Get(ConfigKeys.PollTimeSeconds, "60");
            if (!string.IsNullOrEmpty(folder) && int.TryParse(pollStr, out var poll))
            {
                AutoImport.Configure(folder, poll);
                AutoImport.Start();
            }
        }

        // Network mode: notice other computers' saves to the shared file (toast + page refresh).
        NetworkWatcher.Restart();

        // ── Show the main window ──────────────────────────────────────────────
        // Created here rather than via StartupUri so it appears only after a successful
        // sign-in, and so closing it is what ends the app from this point on.
        var main = new MainWindow();
        MainWindow   = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NetworkWatcher.Dispose();
        CloudSync.Dispose();
        AutoImport.Dispose();
        base.OnExit(e);
    }

    // ── Equipment migration helper ─────────────────────────────────────────────

    /// <summary>
    /// Populates RompEquipmentTypes from existing order keyword-matching.
    /// Runs automatically when the table is empty (first launch after schema upgrade).
    /// Can also be triggered manually via ConfigViewModel.SyncEquipmentFromOrders().
    /// </summary>
    public static void MigrateEquipmentTypesFromOrders(AppDbContext db)
    {
        // Skip only if every ROMP that has any orders already has at least one equipment assignment.
        // The old guard (Any()) was too coarse — it skipped ROMPs whose orders didn't match on
        // the first pass even though the table was non-empty from other ROMPs.
        var assignedRompIds = db.RompEquipmentTypes.Select(rt => rt.RompId).ToHashSet();
        // If nothing is assigned yet we run unconditionally; otherwise skip (caller uses
        // SyncEquipmentFromOrders which clears first to force a full re-run).
        if (assignedRompIds.Count > 0) return;

        var prefix = new ConfigService().Get(ConfigKeys.ItemFilterPrefix, "SOLI");

        // Load category rules (same shape as old ScoreConfig defaults)
        var allCats = db.EquipmentCategories
                        .Include(c => c.Keywords)
                        .OrderBy(c => c.SortOrder)
                        .ToList();
        if (!allCats.Any()) return;

        var exclusions = allCats.Where(c =>  c.IsExcluded).ToList();
        var scoringCats = allCats.Where(c => !c.IsExcluded).ToList();

        // Load all ROMPs → composite key (SiteName + RompName)
        var romps = db.Romps.Include(r => r.Site).ToList();
        var keyToRompIds = romps
            .GroupBy(r => (r.Site.Name + r.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList(),
                          StringComparer.OrdinalIgnoreCase);

        if (keyToRompIds.Count == 0) return;

        // Load SOLI-prefixed orders; group ItemDescriptions by composite key
        var orderQuery = db.Orders.AsNoTracking().Where(o => o.JobName != null);
        if (!string.IsNullOrEmpty(prefix))
            orderQuery = orderQuery.Where(o => o.Item != null
                                            && EF.Functions.Like(o.Item, prefix + "%"));

        var ordersByKey = orderQuery
            .Select(o => new { o.JobName, o.ItemDescription })
            .AsEnumerable()
            .GroupBy(o => StripJobSuffix(o.JobName), StringComparer.OrdinalIgnoreCase)
            .Where(g => keyToRompIds.ContainsKey(g.Key))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ItemDescription)
                       .Where(d => !string.IsNullOrEmpty(d))
                       .Cast<string>()
                       .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var toAdd = new List<RompEquipmentType>();

        foreach (var (key, rompIds) in keyToRompIds)
        {
            if (!ordersByKey.TryGetValue(key, out var descs)) continue;

            // Remove excluded lines
            var scorable = descs.Where(d =>
                !exclusions.Any(c =>
                    c.Keywords.Any(k =>
                        d.Contains(k.Keyword, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (scorable.Count == 0) continue;

            // Determine which scoring categories are present on this ROMP
            foreach (var cat in scoringCats)
            {
                bool matches = scorable.Any(d =>
                    cat.Keywords.Any(k =>
                        d.Contains(k.Keyword, StringComparison.OrdinalIgnoreCase)));
                if (!matches) continue;

                foreach (var rid in rompIds)
                    toAdd.Add(new RompEquipmentType
                    {
                        RompId              = rid,
                        EquipmentCategoryId = cat.Id,
                    });
            }
        }

        if (toAdd.Count > 0)
        {
            db.RompEquipmentTypes.AddRange(toAdd);
            db.SaveChanges();
        }
    }

    private static string StripJobSuffix(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName)) return "";
        var s   = jobName.Trim();
        var idx = s.IndexOf('-');
        return idx >= 0 ? s[..idx] : s;
    }
}
