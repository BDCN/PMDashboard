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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Must load before the first AppDbContext is constructed so OnConfiguring
        // knows which provider / path to use.
        DatabaseConfig.Load();

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
            try { db.Database.ExecuteSqlRaw(
                $"ALTER TABLE {tbl} ADD COLUMN LastModifiedBy TEXT NOT NULL DEFAULT ''"); }
            catch { /* column already exists on this table */ }
        }

        // Site.IsClosed — explicit close flag for the "Close All ROMPs & This Site"/Location
        // cascade, needed because a zero-ROMP site can never reach SiteStatus "Complete" through
        // ROMP data alone (see Site.cs).
        try { db.Database.ExecuteSqlRaw(
            "ALTER TABLE Sites ADD COLUMN IsClosed INTEGER NOT NULL DEFAULT 0"); }
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
                new RompStatus { Name = "Ordered",      IsComplete = false, CountsAsActive = false, ScoreWeight = 0.4, SortOrder = 1, IsBuiltIn = true },
                new RompStatus { Name = "OFCI",         IsComplete = false, CountsAsActive = false, ScoreWeight = 0.6, SortOrder = 2, IsBuiltIn = true },
                new RompStatus { Name = "Construction", IsComplete = false, CountsAsActive = true,  ScoreWeight = 1.0, SortOrder = 3, IsBuiltIn = true },
                new RompStatus { Name = "CX",           IsComplete = false, CountsAsActive = true,  ScoreWeight = 1.2, SortOrder = 4, IsBuiltIn = true },
                new RompStatus { Name = "Complete",     IsComplete = true,  CountsAsActive = false, ScoreWeight = 0.1, SortOrder = 5, IsBuiltIn = true }
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
        db.SaveChanges();

        // ── One-time migration: populate RompEquipmentTypes from existing orders ─
        // Runs only when the junction table is empty (i.e. first launch after schema upgrade).
        // Uses the same keyword-matching logic the old WorkloadViewModel used to score on-the-fly.
        MigrateEquipmentTypesFromOrders(db);

        // ── Load scoring caches ───────────────────────────────────────────────
        StatusConfig.Reload();
        ScoreConfig.Reload();

        // ── Authentication ─────────────────────────────────────────────────────
        // No user exists yet (fresh install) → the first real person creates the admin
        // account directly, choosing their own email and password. Unlike a seeded
        // placeholder password, nothing here is ever a literal in source, so there is
        // nothing to leak into git history.
        if (!db.Users.Any())
        {
            var create = new Dialogs.CreateAdminAccountDialog();
            if (create.ShowDialog() != true) { Shutdown(); return; }
        }

        // ShutdownMode is OnExplicitShutdown (App.xaml) for exactly this: the login/create-
        // account dialog is the only window open, and under the default OnLastWindowClose its
        // closing would end the application before the main window was ever created.
        var login = new Dialogs.LoginDialog();
        if (login.ShowDialog() != true) { Shutdown(); return; }

        // ── Start cloud background sync (Cloud mode only) ─────────────────────
        if (CloudSyncService.IsAvailable)
        {
            var intervalStr = new ConfigService().Get(ConfigKeys.CloudSyncInterval, "30");
            int.TryParse(intervalStr, out var interval);
            CloudSync.Start(interval > 0 ? interval : 30);
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
