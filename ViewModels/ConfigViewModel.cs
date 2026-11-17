using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.ViewModels;

public partial class ConfigViewModel : ObservableObject
{
    // ── Database mode ──────────────────────────────────────────────────────────

    [ObservableProperty] private DatabaseMode _dbMode        = DatabaseMode.Local;
    [ObservableProperty] private string       _networkDbPath = "";
    [ObservableProperty] private string       _cloudHost         = "";
    [ObservableProperty] private string       _cloudPort         = "3306";
    [ObservableProperty] private string       _cloudDatabase     = "pmdashboard";
    [ObservableProperty] private string       _cloudUser         = "";
    [ObservableProperty] private string       _cloudPassword     = "";
    [ObservableProperty] private bool         _isTravelMode;
    [ObservableProperty] private string       _dbStatusMessage   = "";
    [ObservableProperty] private string       _cloudSyncInterval = "30";
    [ObservableProperty] private string       _cloudSyncStatus   = "";

    public void RefreshCloudSyncStatus()
    {
        var svc = App.CloudSync;
        if (!CloudSyncService.IsAvailable) { CloudSyncStatus = ""; return; }
        var lastTime = svc.LastSyncTime.HasValue
            ? $"Last sync: {svc.LastSyncTime:HH:mm:ss}"
            : "Waiting for first sync…";
        CloudSyncStatus = $"{svc.LastStatus}   —   {lastTime}";
    }

    // Keeps the Database card's status text live instead of only refreshing on page load /
    // the manual Refresh button. CloudSyncService.Report() fires StatusChanged from a
    // background thread (Task.Run continuations / Timer callbacks), so the handler must hop
    // back to the UI thread itself before touching an ObservableProperty. Named distinctly
    // from "OnCloudSyncStatusChanged" — that name is reserved by the [ObservableProperty]
    // source generator for CloudSyncStatus's own partial change-callback.
    private void HandleCloudSyncServiceStatus(string _) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(RefreshCloudSyncStatus);

    public bool IsDbLocal   => DbMode == DatabaseMode.Local;
    public bool IsDbNetwork => DbMode == DatabaseMode.Network;
    public bool IsDbCloud   => DbMode == DatabaseMode.Cloud;

    /// <summary>Cloud (MariaDB) is retired — the team uses Network mode. The option is only
    /// shown on a computer that is still in Cloud mode, so it can switch away.</summary>
    [ObservableProperty] private bool _showCloudOption;

    partial void OnDbModeChanged(DatabaseMode value)
    {
        OnPropertyChanged(nameof(IsDbLocal));
        OnPropertyChanged(nameof(IsDbNetwork));
        OnPropertyChanged(nameof(IsDbCloud));
    }

    public string CloudConnectionString =>
        $"Server={CloudHost};Port={CloudPort};Database={CloudDatabase};" +
        $"User={CloudUser};Password={CloudPassword};AllowPublicKeyRetrieval=true;";

    public void LoadDatabaseSettings()
    {
        DbMode        = DatabaseConfig.Mode;
        ShowCloudOption = false; // cloud retired — DatabaseConfig.Load moves any Cloud machine to Local
        NetworkDbPath = DatabaseConfig.NetworkDbPath;
        IsTravelMode  = DatabaseConfig.IsTravelMode;
        DbStatusMessage = "";

        // Parse cloud connection string back into fields
        var cs = DatabaseConfig.CloudConnectionString;
        if (!string.IsNullOrEmpty(cs))
        {
            CloudHost     = GetCsPart(cs, "Server")   ?? CloudHost;
            CloudPort     = GetCsPart(cs, "Port")     ?? CloudPort;
            CloudDatabase = GetCsPart(cs, "Database") ?? CloudDatabase;
            CloudUser     = GetCsPart(cs, "User")     ?? CloudUser;
            CloudPassword = GetCsPart(cs, "Password") ?? CloudPassword;
        }
    }

    public void ApplyLocalMode()
    {
        DatabaseConfig.ApplyLocal();
        DbMode = DatabaseMode.Local;
        DbStatusMessage = "Local mode active. Each machine uses its own database at %LOCALAPPDATA%\\PMDashboard\\data.db";
    }

    public void ApplyNetworkMode(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            DbStatusMessage = "Please enter or browse to the network database path.";
            return;
        }
        // Refuse a path that doesn't hold a database yet — otherwise the next start would
        // quietly create a brand-new EMPTY database there and everyone would see no data.
        if (NetworkDb.CheckReachable(path) is { } problem)
        {
            DbStatusMessage = problem + "\n\nUse \"Create Network Database\" first if this is a new location.";
            return;
        }
        try { NetworkDb.EnsureRollbackJournal(path); } catch { /* re-checked at every startup */ }
        DatabaseConfig.ApplyNetwork(path);
        DbMode = DatabaseMode.Network;
        NetworkDbPath = path;
        DbStatusMessage = $"Network mode saved.\nPath: {path}\n" +
                          "Restart SAT to finish switching — after that all reads/writes go to the network database.";
    }

    public void ApplyCloudMode()
    {
        if (string.IsNullOrWhiteSpace(CloudHost) || string.IsNullOrWhiteSpace(CloudUser))
        {
            DbStatusMessage = "Please fill in at least Host and Username.";
            return;
        }
        DatabaseConfig.ApplyCloud(CloudConnectionString);
        DbMode = DatabaseMode.Cloud;
        new ConfigService().Set(ConfigKeys.CloudSyncInterval, CloudSyncInterval);
        DbStatusMessage = "Cloud mode saved. Restart the app to activate local-first sync.";
    }

    // Travel Sync itself lives in Dialogs/TravelSyncFlow (shared with the sidebar control).

    public string CreateNetworkDatabase(string path)
    {
        var r = SyncService.CreateNetworkDatabase(path);
        return r.Message;
    }

    public async Task<string> TestCloudConnectionAsync()
    {
        var r = await SyncService.TestCloudConnectionAsync(CloudConnectionString);
        return r.Message;
    }

    public string GetDdlScript() => SyncService.MariaDbDdlScript;

    private static string? GetCsPart(string cs, string key)
    {
        var prefix = key + "=";
        foreach (var part in cs.Split(';'))
        {
            var t = part.Trim();
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return t[prefix.Length..].Trim();
        }
        return null;
    }

    // ── Existing fields ────────────────────────────────────────────────────────

    [ObservableProperty] private string _sourceDataType  = "VADATA";
    [ObservableProperty] private bool   _isSemiAutomatic;
    [ObservableProperty] private string _importFolder    = "";
    [ObservableProperty] private string _pollTimeSeconds = "60";
    [ObservableProperty] private string _statusMessage   = "";
    [ObservableProperty] private bool   _showNewFolderInput;

    // Source data type helpers for radio button OneWay bindings
    public bool IsVadata    => SourceDataType == "VADATA";
    public bool IsTableau   => SourceDataType == "Tableau";
    public bool IsOpenLines => SourceDataType == "OpenLines";

    partial void OnSourceDataTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsVadata));
        OnPropertyChanged(nameof(IsTableau));
        OnPropertyChanged(nameof(IsOpenLines));
    }

    public void SaveSourceDataType(string type)
    {
        new ConfigService().Set(ConfigKeys.SourceDataType, type);
        SourceDataType = type;
    }

    public bool IsManual => !IsSemiAutomatic;

    partial void OnIsSemiAutomaticChanged(bool value)
    {
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(CanSaveImportSettings));
    }
    partial void OnImportFolderChanged(string value)    => OnPropertyChanged(nameof(CanSaveImportSettings));
    partial void OnPollTimeSecondsChanged(string value) => OnPropertyChanged(nameof(CanSaveImportSettings));

    public bool CanSaveImportSettings =>
        !string.IsNullOrWhiteSpace(ImportFolder)
        && Directory.Exists(ImportFolder)
        && int.TryParse(PollTimeSeconds, out var t) && t >= 60;

    // ── Percentage Calculation mode ──

    [ObservableProperty] private string _percentageCalculation = "Sites";

    public bool IsSitesMode => PercentageCalculation == "Sites";
    public bool IsScoreMode  => PercentageCalculation == "Score";
    public bool IsCountMode  => PercentageCalculation == "Count";

    partial void OnPercentageCalculationChanged(string value)
    {
        OnPropertyChanged(nameof(IsSitesMode));
        OnPropertyChanged(nameof(IsScoreMode));
        OnPropertyChanged(nameof(IsCountMode));
    }

    public void SavePercentageCalculation(string mode)
    {
        new ConfigService().Set(ConfigKeys.PercentageCalculation, mode);
        PercentageCalculation = mode;
    }

    // ── Workload scoring parameters ──

    // Stored as doubles so sliders can bind directly
    [ObservableProperty] private double _siteOverhead      = 0.05;
    [ObservableProperty] private double _targetCapacity    = 259.2;
    [ObservableProperty] private double _nearFullThreshold = 0.80;
    [ObservableProperty] private double _overloadThreshold = 1.00;
    [ObservableProperty] private double _skillBase         = 0.40;  // floor factor for eff_capacity
    [ObservableProperty] private double _skillPerPoint     = 0.06;  // increment per multiplier point

    // ── Forecast Timeline schedule-conflict settings — see ScheduleConflict doc on
    // ForecastTimelineViewModel ──
    [ObservableProperty] private int _cxWindowDays             = 14; // days before PFHO treated as the CX/cutover window
    [ObservableProperty] private int _maxConcurrentCxPerRegion = 1;  // sites one PM may have in a region's CX window at once before flagging

    // Coverage Planner (see Services/CoveragePlanner.cs)
    [ObservableProperty] private double _coverageCapacityPerPm = CoverageDefaults.CapacityPerPm;
    [ObservableProperty] private double _coverageBuildWeight   = CoverageDefaults.BuildWeight;
    [ObservableProperty] private double _coverageCxWeight      = CoverageDefaults.CxWeight;
    [ObservableProperty] private int    _coverageHireLeadDays  = CoverageDefaults.HireLeadDays;
    [ObservableProperty] private int    _coverageHorizonWeeks  = CoverageDefaults.HorizonWeeks;

    /// <summary>The defaults "Reset to defaults" restores, spelled out for the card's hint line.</summary>
    public string CoverageDefaultsText =>
        $"Defaults: capacity {CoverageDefaults.CapacityPerPm:0.##} · build {CoverageDefaults.BuildWeight:0.##}/day · " +
        $"CX {CoverageDefaults.CxWeight:0.##}/day · CX window {CoverageDefaults.CxWindowDays} days · " +
        $"hire lead {CoverageDefaults.HireLeadDays} days · horizon {CoverageDefaults.HorizonWeeks} weeks";

    /// <summary>Set by MainViewModel when another page asks to open Configuration at a specific
    /// card ("Coverage"); ConfigPage scrolls there on load and clears it.</summary>
    public string? PendingScrollTarget { get; set; }

    // ── Delete local data ────────────────────────────────────────────────────

    /// <summary>
    /// Tables cleared by <see cref="DeleteLocalData"/>, in FK-safe order (children first).
    /// Configuration tables are deliberately NOT in this list — see the remarks there.
    /// </summary>
    private static readonly string[] LocalDataTables =
    {
        "RompEquipmentTypes", "AssignmentHistories", "Romps", "Sites", "Campuses", "Locations",
        "Orders", "CustomRows", "CustomColumns", "SavedReports", "EquipmentWeights",
    };

    /// <summary>Row counts for the tables the delete would clear, for the confirmation prompt.</summary>
    public Dictionary<string, int> GetLocalDataCounts()
    {
        using var db = new AppDbContext();
        var counts = new Dictionary<string, int>();
        foreach (var table in LocalDataTables)
        {
            // table comes from the hardcoded LocalDataTables array, never user input, and a
            // table name can't be parameterized via SqlQuery anyway — EF1002 suppressed.
#pragma warning disable EF1002
            try { counts[table] = db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}").AsEnumerable().First(); }
            catch { counts[table] = 0; }   // table missing on an older schema
#pragma warning restore EF1002
        }
        return counts;
    }

    /// <summary>
    /// Empties the local SQLite database of records, leaving the schema in place.
    ///
    /// PMs are preserved — the roster is maintained by hand rather than imported, so it is not
    /// something a re-import or a cloud pull would bring back. Deleting Sites while keeping PMs
    /// leaves no dangling FK, since Site.AssignedPmId points at PM and not the other way round;
    /// the surviving PMs simply end up with no sites assigned.
    ///
    /// Configuration tables (AppConfigs, RompStatuses, EquipmentCategories, EquipmentKeywords,
    /// AssignmentRules) are preserved for the same reason: they hold the admin password hash, the
    /// workload scoring parameters and the status/equipment setup, none of which is "data" in the
    /// sense meant here, and wiping AppConfigs would drop the admin password and lock this card.
    ///
    /// Raw DELETE is used rather than EF RemoveRange so 8k+ Orders do not have to be
    /// materialised and tracked. That also means SaveChanges never runs, so no cloud push is
    /// triggered — and since sync never deletes remotely, nothing is removed from the cloud.
    /// In Cloud mode the next sync pulls the cloud's copy back down, which is the point: it
    /// makes the cloud authoritative for this machine.
    /// </summary>
    /// <summary>How many PM assignments exist, by kind — for the Clear All Assignments confirmation.</summary>
    public (int Sites, int Campuses, int Romps, int Clusters) GetAssignmentCounts()
    {
        using var db = new AppDbContext();
        return (db.Sites.Count(s => s.AssignedPmId != null), db.Campuses.Count(c => c.AssignedPmId != null),
                db.Romps.Count(r => r.AssignedPmId != null), db.Locations.Count(l => l.AssignedPmId != null));
    }

    /// <summary>
    /// Unassigns every PM from every site, campus, ROMP override and cluster, and closes their open
    /// assignment-history rows (the history itself is kept). Goes through SaveChanges, so the rows
    /// are stamped and — in Cloud mode — pushed to every machine like any other edit. PMs, teams,
    /// sites and everything else are untouched.
    /// </summary>
    public string ClearAllAssignments()
    {
        using var db = new AppDbContext();
        var now = DateTime.UtcNow;
        var who = CurrentUser.Email;

        var sites = db.Sites.Where(s => s.AssignedPmId != null || s.PmOverride).ToList();
        int siteCount = sites.Count(s => s.AssignedPmId != null);
        foreach (var s in sites) { s.AssignedPmId = null; s.PmOverride = false; }
        var campuses = db.Campuses.Where(c => c.AssignedPmId != null).ToList();
        foreach (var c in campuses) c.AssignedPmId = null;
        var romps = db.Romps.Where(r => r.AssignedPmId != null).ToList();
        foreach (var r in romps) r.AssignedPmId = null;
        var clusters = db.Locations.Where(l => l.AssignedPmId != null).ToList();
        foreach (var l in clusters) l.AssignedPmId = null;
        var open = db.AssignmentHistories.Where(h => h.UnassignedAtUtc == null).ToList();
        foreach (var h in open) { h.UnassignedAtUtc = now; h.UnassignedBy = who; h.LastModified = now; }

        db.SaveChanges();
        var summary = $"Cleared {siteCount:N0} site, {campuses.Count:N0} campus, {romps.Count:N0} ROMP and {clusters.Count:N0} cluster assignment(s).";
        AuditService.Log("Unassign", "AllAssignments", null, summary + $" Closed {open.Count:N0} assignment-history row(s).");
        return summary;
    }

    public string DeleteLocalData()
    {
        using var db = new AppDbContext();
        int total = 0;
        var cleared = new List<string>();

        foreach (var table in LocalDataTables)
        {
            try
            {
                // table comes from the hardcoded LocalDataTables array, never user input, and a
                // table name can't be parameterized via ExecuteSql/SqlQuery anyway — EF1002 suppressed.
#pragma warning disable EF1002
                var before = db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}").AsEnumerable().First();
                if (before == 0) continue;
                db.Database.ExecuteSqlRaw($"DELETE FROM {table}");
#pragma warning restore EF1002
                total += before;
                cleared.Add($"{table} ({before:N0})");
            }
            catch { /* table absent on this schema — nothing to clear */ }
        }

        // Reset AUTOINCREMENT counters so a subsequent cloud pull can insert the cloud's own
        // Ids directly instead of colliding with locally-consumed ones.
        try { db.Database.ExecuteSqlRaw("DELETE FROM sqlite_sequence"); } catch { /* no AUTOINCREMENT tables */ }

        StatusConfig.Reload();
        ScoreConfig.Reload();

        AuditService.Log("Delete", "LocalDatabase", details:
            total == 0 ? "Delete local data: nothing to delete" : $"Deleted {total:N0} local record(s)");

        return total == 0
            ? "Local database was already empty — nothing deleted."
            : $"Deleted {total:N0} record(s): {string.Join(", ", cleared)}.";
    }

    /// <summary>
    /// Every table in the schema, in FK-safe order (children first) — unlike
    /// <see cref="LocalDataTables"/>, nothing is held back. Used by ClearAllData, which (unlike
    /// DeleteLocalData) also wipes the cloud copy in Cloud mode, so this is a genuine full
    /// factory reset: every machine, every user account, every setting, gone for good.
    /// </summary>
    private static readonly string[] AllDataTables =
    {
        "RompEquipmentTypes", "AssignmentHistories", "EquipmentKeywords", "Romps", "Sites", "Campuses", "Locations", "TeamMembers", "SubRegions", "PMs",
        "Orders", "CustomRows", "CustomColumns", "SavedReports", "EquipmentWeights",
        "EquipmentCategories", "RompStatuses", "AssignmentRules", "AuditEntries", "Users", "AppConfigs",
    };

    /// <summary>Row counts (local) for the confirmation prompt — used as a representative total; in a
    /// working Cloud-mode system local and cloud counts should already match.</summary>
    public Dictionary<string, int> GetAllDataCounts()
    {
        using var db = new AppDbContext();
        var counts = new Dictionary<string, int>();
        foreach (var table in AllDataTables)
        {
#pragma warning disable EF1002
            try { counts[table] = db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}").AsEnumerable().First(); }
            catch { counts[table] = 0; }
#pragma warning restore EF1002
        }
        return counts;
    }

    /// <summary>
    /// Full factory reset: every table, this machine AND (in Cloud mode) the shared cloud
    /// database — including PMs, Users, and every setting, none of which DeleteLocalData
    /// touches. There is no self-heal from a subsequent sync here, because there is nothing
    /// left anywhere to sync back from. The caller (ConfigPage.xaml.cs) is expected to shut the
    /// app down immediately after this succeeds — the signed-in Super Admin's own account is
    /// gone too, and the app has no "you were just signed out, please re-authenticate" flow
    /// mid-session, only the first-run Create-Admin-Account path a full restart lands on.
    /// </summary>
    public string ClearAllData()
    {
        int totalLocal = ClearAllTables(forceCloud: false);

        if (!DatabaseConfig.IsCloud) return $"Cleared {totalLocal:N0} local record(s). Everything, including accounts, has been wiped.";

        try
        {
            int totalCloud = ClearAllTables(forceCloud: true);
            return $"Cleared {totalLocal:N0} local record(s) and {totalCloud:N0} cloud record(s). Everything, including accounts, has been wiped everywhere.";
        }
        catch (Exception ex)
        {
            return $"Cleared {totalLocal:N0} local record(s), but clearing the cloud database failed: {ex.Message}\n\n" +
                   "Local and cloud are now out of sync. Fix the connection and run this again, or the next sync may pull the old cloud data back down.";
        }
    }

    private static int ClearAllTables(bool forceCloud)
    {
        using var db = forceCloud ? new AppDbContext(forceCloud: true) : new AppDbContext();

        // Disable FK enforcement for the duration rather than relying solely on AllDataTables'
        // ordering — cheap insurance against a future schema change adding a constraint this
        // list wasn't updated for.
        try { db.Database.ExecuteSqlRaw(forceCloud ? "SET FOREIGN_KEY_CHECKS=0" : "PRAGMA foreign_keys=OFF"); }
        catch { /* best-effort */ }

        int total = 0;
        foreach (var table in AllDataTables)
        {
#pragma warning disable EF1002
            try
            {
                var before = db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}").AsEnumerable().First();
                if (before == 0) continue;
                db.Database.ExecuteSqlRaw($"DELETE FROM {table}");
                total += before;
            }
            catch { /* table absent on this schema — nothing to clear */ }
#pragma warning restore EF1002
        }

        if (forceCloud)
        {
            try { db.Database.ExecuteSqlRaw("SET FOREIGN_KEY_CHECKS=1"); } catch { /* best-effort */ }
        }
        else
        {
            try { db.Database.ExecuteSqlRaw("DELETE FROM sqlite_sequence"); } catch { /* no AUTOINCREMENT tables */ }
            try { db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON"); } catch { /* best-effort */ }
        }

        return total;
    }

    /// <summary>Gates the whole Configuration page and every card on it — Manage Users has moved to its own page (see UserManagementViewModel), so this is Super Admin only again.</summary>
    public bool IsSuperAdmin => CurrentUser.IsSuperAdmin;

    public void SaveWorkloadParams()
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var cs = new ConfigService();
        cs.Set(ConfigKeys.SiteOverhead,      SiteOverhead.ToString("F4", ic));
        cs.Set(ConfigKeys.TargetCapacity,    TargetCapacity.ToString("F1", ic));
        cs.Set(ConfigKeys.NearFullThreshold, NearFullThreshold.ToString("F3", ic));
        cs.Set(ConfigKeys.OverloadThreshold, OverloadThreshold.ToString("F3", ic));
        cs.Set(ConfigKeys.SkillBase,         SkillBase.ToString("F3", ic));
        cs.Set(ConfigKeys.SkillPerPoint,     SkillPerPoint.ToString("F4", ic));
        ScoreConfig.Reload();
    }

    public void SaveCoverageSettings()
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var cs = new ConfigService();
        cs.Set(ConfigKeys.CoverageCapacityPerPm, Math.Max(0.01, CoverageCapacityPerPm).ToString("0.###", ic));
        cs.Set(ConfigKeys.CoverageBuildWeight,   Math.Max(0, CoverageBuildWeight).ToString("0.###", ic));
        cs.Set(ConfigKeys.CoverageCxWeight,      Math.Max(0, CoverageCxWeight).ToString("0.###", ic));
        cs.Set(ConfigKeys.CoverageHireLeadDays,  Math.Max(0, CoverageHireLeadDays).ToString());
        cs.Set(ConfigKeys.CoverageHorizonWeeks,  Math.Clamp(CoverageHorizonWeeks, 4, 520).ToString());
        // CX window is shared with the Forecast Timeline's conflict rule — this card edits the same setting.
        cs.Set(ConfigKeys.CxWindowDays,          Math.Max(0, CxWindowDays).ToString());
        AuditService.Log("Update", "Config", null,
            $"Coverage planning: capacity {CoverageCapacityPerPm}, build {CoverageBuildWeight}/day, CX {CoverageCxWeight}/day, " +
            $"CX window {CxWindowDays} days, hire lead {CoverageHireLeadDays} days, horizon {CoverageHorizonWeeks} weeks");
    }

    /// <summary>Puts every Coverage Planner variable back to <see cref="CoverageDefaults"/> and saves.</summary>
    public void ResetCoverageSettings()
    {
        CoverageCapacityPerPm = CoverageDefaults.CapacityPerPm;
        CoverageBuildWeight   = CoverageDefaults.BuildWeight;
        CoverageCxWeight      = CoverageDefaults.CxWeight;
        CxWindowDays          = CoverageDefaults.CxWindowDays;
        CoverageHireLeadDays  = CoverageDefaults.HireLeadDays;
        CoverageHorizonWeeks  = CoverageDefaults.HorizonWeeks;
        SaveCoverageSettings();
        AuditService.Log("Reset", "Config", null, "Coverage planning settings reset to defaults");
    }

    public void SaveScheduleConflictSettings()
    {
        var cs = new ConfigService();
        cs.Set(ConfigKeys.CxWindowDays,            Math.Max(0, CxWindowDays).ToString());
        cs.Set(ConfigKeys.MaxConcurrentCxPerRegion, Math.Max(1, MaxConcurrentCxPerRegion).ToString());
    }

    /// <summary>
    /// Clears all RompEquipmentType rows and re-derives them from existing orders
    /// using the same keyword-matching logic as the initial migration.
    /// Useful after importing new orders or changing equipment category keywords.
    /// </summary>
    public string SyncEquipmentFromOrders()
    {
        using var db = new AppDbContext();
        // Clear first so MigrateEquipmentTypesFromOrders will run
        var existing = db.RompEquipmentTypes.ToList();
        db.RompEquipmentTypes.RemoveRange(existing);
        db.SaveChanges();

        App.MigrateEquipmentTypesFromOrders(db);

        int newCount = db.RompEquipmentTypes.Count();
        return $"Sync complete — {newCount} equipment type assignments created from orders.";
    }

    // ── Versions in use (Database card) ──
    public string ThisVersion => AppVersion.Full;
    [ObservableProperty] private List<ClientVersionRow> _clientVersions = new();

    private void LoadClientVersions()
    {
        try
        {
            var newest = CloudSyncService.ClientVersions()
                .Select(c => c.ParsedVersion).Where(v => v != null).DefaultIfEmpty(AppVersion.Version).Max()!;
            if (AppVersion.Version > newest) newest = AppVersion.Version;
            ClientVersions = CloudSyncService.ClientVersions()
                .Select(c => new ClientVersionRow(
                    c.Machine,
                    "v" + c.Info.Version,
                    c.ParsedVersion is { } v && v < newest,
                    $"{c.Info.User}  ·  last started {c.Info.LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
                        + (c.Machine == Environment.MachineName ? "  (this computer)" : "")))
                .ToList();
        }
        catch { ClientVersions = new(); }
    }

    public void Load()
    {
        LoadClientVersions();
        var cs = new ConfigService();
        SourceDataType          = cs.Get(ConfigKeys.SourceDataType, "VADATA");
        IsSemiAutomatic         = cs.Get(ConfigKeys.UploadMethod) == "SemiAutomatic";
        ImportFolder            = cs.Get(ConfigKeys.ImportFolder, "");
        PollTimeSeconds         = cs.Get(ConfigKeys.PollTimeSeconds, "60");
        PercentageCalculation   = cs.Get(ConfigKeys.PercentageCalculation, "Sites");
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        SiteOverhead      = double.TryParse(cs.Get(ConfigKeys.SiteOverhead,      "0.05"),  System.Globalization.NumberStyles.Any, ic, out var so) ? so : 0.05;
        TargetCapacity    = double.TryParse(cs.Get(ConfigKeys.TargetCapacity,    "259.2"), System.Globalization.NumberStyles.Any, ic, out var tc) ? tc : 259.2;
        NearFullThreshold = double.TryParse(cs.Get(ConfigKeys.NearFullThreshold, "0.80"),  System.Globalization.NumberStyles.Any, ic, out var nf) ? nf : 0.80;
        OverloadThreshold = double.TryParse(cs.Get(ConfigKeys.OverloadThreshold, "1.00"),  System.Globalization.NumberStyles.Any, ic, out var ol) ? ol : 1.00;
        SkillBase         = double.TryParse(cs.Get(ConfigKeys.SkillBase,         "0.40"),  System.Globalization.NumberStyles.Any, ic, out var sb) ? sb : 0.40;
        SkillPerPoint     = double.TryParse(cs.Get(ConfigKeys.SkillPerPoint,     "0.06"),  System.Globalization.NumberStyles.Any, ic, out var sp) ? sp : 0.06;
        CxWindowDays             = int.TryParse(cs.Get(ConfigKeys.CxWindowDays,             "14"), out var cx) ? cx : 14;
        MaxConcurrentCxPerRegion = int.TryParse(cs.Get(ConfigKeys.MaxConcurrentCxPerRegion, "1"),  out var mc) ? mc : 1;
        var cov = CoverageDataService.LoadSettings();
        CoverageCapacityPerPm = cov.CapacityPerPm;
        CoverageBuildWeight   = cov.BuildWeight;
        CoverageCxWeight      = cov.CxWeight;
        CoverageHireLeadDays  = cov.HireLeadDays;
        CoverageHorizonWeeks  = cov.HorizonWeeks;
        CloudSyncInterval       = cs.Get(ConfigKeys.CloudSyncInterval, "30");
        StatusMessage           = "";
        LoadDatabaseSettings();
        RefreshCloudSyncStatus();
        App.CloudSync.StatusChanged -= HandleCloudSyncServiceStatus;
        App.CloudSync.StatusChanged += HandleCloudSyncServiceStatus;
        LoadRules();
        LoadRompStatuses();
        LoadEquipmentWeights();
    }

    public void SaveUploadMethod()
    {
        var cs = new ConfigService();
        cs.Set(ConfigKeys.UploadMethod, IsSemiAutomatic ? "SemiAutomatic" : "Manual");

        if (!IsSemiAutomatic)
        {
            App.AutoImport.Stop();
            StatusMessage = "Manual upload mode saved.";
        }
        else
        {
            StatusMessage = "Semi-automatic mode selected — configure import settings below.";
        }
    }

    public void SaveImportSettings()
    {
        if (!CanSaveImportSettings) return;

        var cs = new ConfigService();
        // Save all three keys together so the method is never left as Manual when settings are saved
        cs.Set(ConfigKeys.UploadMethod,    "SemiAutomatic");
        cs.Set(ConfigKeys.ImportFolder,    ImportFolder);
        cs.Set(ConfigKeys.PollTimeSeconds, PollTimeSeconds);

        App.AutoImport.Configure(ImportFolder, int.Parse(PollTimeSeconds));
        App.AutoImport.Start();

        // Subscribe to status updates if not already done
        App.AutoImport.StatusChanged -= OnAutoImportStatus;
        App.AutoImport.StatusChanged += OnAutoImportStatus;

        StatusMessage = "Import settings saved. Auto-import is running.";
    }

    private void OnAutoImportStatus(string msg) => StatusMessage = msg;

    // ── Assignment Rules management ──

    [ObservableProperty] private ObservableCollection<AssignmentRule> _assignmentRules = new();

    public void LoadRules()
    {
        using var db = new AppDbContext();
        var rules = db.AssignmentRules.AsNoTracking()
                                      .OrderBy(r => r.SortOrder)
                                      .ToList();
        AssignmentRules = new ObservableCollection<AssignmentRule>(rules);
    }

    public void SaveRule(AssignmentRule rule)
    {
        using var db = new AppDbContext();
        var tracked = db.AssignmentRules.Find(rule.Id);
        if (tracked == null) return;
        tracked.IsActive = rule.IsActive;
        tracked.Weight   = rule.Weight;
        if (!tracked.IsBuiltIn) { tracked.Name = rule.Name; tracked.Description = rule.Description; }
        db.SaveChanges();
    }

    public void AddCustomRule(string name, string description, int weight)
    {
        using var db = new AppDbContext();
        var maxOrder = db.AssignmentRules.Any() ? db.AssignmentRules.Max(r => r.SortOrder) : 0;
        db.AssignmentRules.Add(new AssignmentRule
        {
            Name = name, Description = description, RuleKey = "Custom",
            Weight = weight, IsActive = true, SortOrder = maxOrder + 1, IsBuiltIn = false
        });
        db.SaveChanges();
        LoadRules();
    }

    public void DeleteRule(AssignmentRule rule)
    {
        if (rule.IsBuiltIn) return;
        using var db = new AppDbContext();
        var tracked = db.AssignmentRules.Find(rule.Id);
        if (tracked != null) { db.AssignmentRules.Remove(tracked); db.SaveChanges(); }
        LoadRules();
    }

    public void MoveRuleUp(AssignmentRule rule)   => SwapSortOrder(rule, -1);
    public void MoveRuleDown(AssignmentRule rule) => SwapSortOrder(rule, +1);

    private void SwapSortOrder(AssignmentRule rule, int direction)
    {
        using var db = new AppDbContext();
        var all    = db.AssignmentRules.OrderBy(r => r.SortOrder).ToList();
        var idx    = all.FindIndex(r => r.Id == rule.Id);
        var target = idx + direction;
        if (target < 0 || target >= all.Count) return;
        (all[idx].SortOrder, all[target].SortOrder) = (all[target].SortOrder, all[idx].SortOrder);
        db.SaveChanges();
        LoadRules();
    }

    // ── ROMP Status management ──

    [ObservableProperty] private ObservableCollection<RompStatus> _rompStatuses = new();

    public void LoadRompStatuses()
    {
        using var db = new AppDbContext();
        var statuses = db.RompStatuses.AsNoTracking()
                                      .OrderBy(r => r.SortOrder)
                                      .ToList();
        RompStatuses = new ObservableCollection<RompStatus>(statuses);
    }

    /// <summary>
    /// Super Admin can now rename/retune any status, built-in included. Returns an error message
    /// (and leaves the name unchanged) instead of applying a rename that would corrupt data: an
    /// empty or duplicate name, or a change that doesn't propagate to ROMPs already on this
    /// status, since Romp.Status stores the status NAME directly (no FK) — any ROMP left on the
    /// old name would become invisible to StatusConfig/scoring (silently scores as 0) instead of
    /// following the rename.
    /// </summary>
    public string? SaveRompStatus(RompStatus status)
    {
        using var db = new AppDbContext();
        var tracked = db.RompStatuses.Find(status.Id);
        if (tracked == null) return "Status no longer exists.";

        var newName = status.Name.Trim();
        string? error = null;

        if (newName != tracked.Name)
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                error = "Status name can't be blank.";
            }
            else if (db.RompStatuses.Any(r => r.Id != status.Id && r.Name.ToLower() == newName.ToLower()))
            {
                error = $"A status named \"{newName}\" already exists.";
            }
            else
            {
                // Carry every existing ROMP on the old name forward to the new one so none of
                // them fall out of StatusConfig's lists (and silently drop to a 0 score weight).
                foreach (var romp in db.Romps.Where(r => r.Status == tracked.Name))
                    romp.Status = newName;
                tracked.Name = newName;
            }
        }

        tracked.IsComplete     = status.IsComplete;
        tracked.CountsAsActive = status.CountsAsActive;
        tracked.ScoreWeight    = status.ScoreWeight;
        tracked.IsDisabled     = status.IsDisabled;
        tracked.Color          = string.IsNullOrWhiteSpace(status.Color) ? tracked.Color : status.Color;
        db.SaveChanges();
        StatusConfig.Reload();
        ScoreConfig.Reload();
        LoadRompStatuses(); // re-pull from DB so a rejected rename reverts in the UI too

        return error;
    }

    public void AddCustomStatus(string name, bool isComplete, bool countsAsActive)
    {
        using var db = new AppDbContext();
        var maxOrder = db.RompStatuses.Any() ? db.RompStatuses.Max(r => r.SortOrder) : 0;
        db.RompStatuses.Add(new RompStatus
        {
            Name = name, IsComplete = isComplete, CountsAsActive = countsAsActive,
            SortOrder = maxOrder + 1, IsBuiltIn = false
        });
        db.SaveChanges();
        StatusConfig.Reload();
        LoadRompStatuses();
    }

    /// <summary>
    /// Super Admin can now delete any status, built-in included — but not out from under data
    /// still using it: a ROMP's Status is a plain string, not an FK, so deleting a status that
    /// ROMPs still reference would leave them pointing at a name StatusConfig no longer knows
    /// about (same silent-0-score-weight problem as an uncascaded rename). Returns an error
    /// message instead, naming how many ROMPs are blocking it; null on success.
    /// </summary>
    public string? DeleteRompStatus(RompStatus status)
    {
        using var db = new AppDbContext();

        if (db.RompStatuses.Count() <= 1)
            return "At least one status must remain.";

        var inUseCount = db.Romps.Count(r => r.Status == status.Name);
        if (inUseCount > 0)
            return $"\"{status.Name}\" is still used by {inUseCount} ROMP(s) — change their status before deleting it.";

        var tracked = db.RompStatuses.Find(status.Id);
        if (tracked != null) { db.RompStatuses.Remove(tracked); db.SaveChanges(); }
        StatusConfig.Reload();
        LoadRompStatuses();
        return null;
    }

    public void MoveRompStatusUp(RompStatus status)   => SwapRompStatusOrder(status, -1);
    public void MoveRompStatusDown(RompStatus status) => SwapRompStatusOrder(status, +1);

    private void SwapRompStatusOrder(RompStatus status, int direction)
    {
        using var db = new AppDbContext();
        var all    = db.RompStatuses.OrderBy(r => r.SortOrder).ToList();
        var idx    = all.FindIndex(r => r.Id == status.Id);
        var target = idx + direction;
        if (target < 0 || target >= all.Count) return;
        (all[idx].SortOrder, all[target].SortOrder) = (all[target].SortOrder, all[idx].SortOrder);
        db.SaveChanges();
        StatusConfig.Reload();
        LoadRompStatuses();
    }

    // ── Equipment Score Categories management ──

    /// <summary>Flat VM row for the Config DataGrid — Keywords stored comma-separated for inline editing.</summary>
    public class EquipmentCategoryVm
    {
        public int    Id           { get; set; }
        public string Name         { get; set; } = "";
        public string KeywordsText { get; set; } = "";   // comma-separated, e.g. "PIU, PIE, CDP, ATS"
        public double Weight       { get; set; }          // activity multiplier (e.g. 1.6 for PAX)
        public bool   IsExcluded   { get; set; }
        public bool   IsBuiltIn    { get; set; }
        public int    SortOrder    { get; set; }

        // Helper used by ConfigPage handlers
        public IReadOnlyList<string> ParsedKeywords =>
            KeywordsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(k => k.Trim().ToUpperInvariant())
                        .Where(k => k.Length > 0)
                        .Distinct()
                        .ToList();
    }

    [ObservableProperty] private ObservableCollection<EquipmentCategoryVm> _equipmentCategories = new();
    [ObservableProperty] private string _itemFilterPrefix = "SOLI";

    public void LoadEquipmentWeights()   // name kept so Load() call compiles unchanged
    {
        using var db = new AppDbContext();
        var cats = db.EquipmentCategories.AsNoTracking()
                                          .Include(c => c.Keywords)
                                          .OrderBy(c => c.SortOrder)
                                          .ToList();
        EquipmentCategories = new ObservableCollection<EquipmentCategoryVm>(
            cats.Select(c => new EquipmentCategoryVm
            {
                Id           = c.Id,
                Name         = c.Name,
                KeywordsText = string.Join(", ", c.Keywords.Select(k => k.Keyword)),
                Weight       = c.Weight,
                IsExcluded   = c.IsExcluded,
                IsBuiltIn    = c.IsBuiltIn,
                SortOrder    = c.SortOrder,
            }));
        ItemFilterPrefix = new ConfigService().Get(ConfigKeys.ItemFilterPrefix, "SOLI");
    }

    public void SaveEquipmentCategory(EquipmentCategoryVm vm)
    {
        using var db = new AppDbContext();
        var tracked = db.EquipmentCategories.Include(c => c.Keywords).FirstOrDefault(c => c.Id == vm.Id);
        if (tracked == null) return;

        tracked.Weight     = vm.Weight;
        tracked.IsExcluded = vm.IsExcluded;
        if (!tracked.IsBuiltIn) tracked.Name = vm.Name;

        // Replace keywords
        tracked.Keywords.Clear();
        foreach (var kw in vm.ParsedKeywords)
            tracked.Keywords.Add(new Data.Models.EquipmentKeyword { Keyword = kw });

        db.SaveChanges();
        ScoreConfig.Reload();
    }

    public void SaveItemFilterPrefix()
    {
        new ConfigService().Set(ConfigKeys.ItemFilterPrefix, ItemFilterPrefix);
        ScoreConfig.Reload();
    }

    public void AddEquipmentCategory(string name, string keywordsText, int weight, bool isExcluded)
    {
        var keywords = keywordsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                   .Select(k => k.Trim())
                                   .Where(k => k.Length > 0)
                                   .Distinct()
                                   .ToList();
        using var db = new AppDbContext();
        int maxOrder = db.EquipmentCategories.Any() ? db.EquipmentCategories.Max(c => c.SortOrder) : 0;
        db.EquipmentCategories.Add(new Data.Models.EquipmentCategory
        {
            Name = name, Weight = weight, IsExcluded = isExcluded,
            IsBuiltIn = false, SortOrder = maxOrder + 1,
            Keywords = keywords.Select(k => new Data.Models.EquipmentKeyword { Keyword = k }).ToList()
        });
        db.SaveChanges();
        ScoreConfig.Reload();
        LoadEquipmentWeights();
    }

    public void DeleteEquipmentCategory(EquipmentCategoryVm vm)
    {
        if (vm.IsBuiltIn) return;
        using var db = new AppDbContext();
        var tracked = db.EquipmentCategories.Find(vm.Id);
        if (tracked != null) { db.EquipmentCategories.Remove(tracked); db.SaveChanges(); }
        ScoreConfig.Reload();
        LoadEquipmentWeights();
    }

    public void MoveEquipmentCategoryUp(EquipmentCategoryVm vm)   => SwapEquipmentOrder(vm, -1);
    public void MoveEquipmentCategoryDown(EquipmentCategoryVm vm) => SwapEquipmentOrder(vm, +1);

    private void SwapEquipmentOrder(EquipmentCategoryVm vm, int direction)
    {
        using var db = new AppDbContext();
        var all    = db.EquipmentCategories.OrderBy(c => c.SortOrder).ToList();
        var idx    = all.FindIndex(c => c.Id == vm.Id);
        var target = idx + direction;
        if (target < 0 || target >= all.Count) return;
        (all[idx].SortOrder, all[target].SortOrder) = (all[target].SortOrder, all[idx].SortOrder);
        db.SaveChanges();
        ScoreConfig.Reload();
        LoadEquipmentWeights();
    }
}

public record ClientVersionRow(string Machine, string Version, bool IsOutdated, string Details);
