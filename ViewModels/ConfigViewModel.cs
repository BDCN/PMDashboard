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

    public bool IsDbLocal   => DbMode == DatabaseMode.Local;
    public bool IsDbNetwork => DbMode == DatabaseMode.Network;
    public bool IsDbCloud   => DbMode == DatabaseMode.Cloud;

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
        DatabaseConfig.ApplyNetwork(path);
        DbMode = DatabaseMode.Network;
        NetworkDbPath = path;
        DbStatusMessage = $"Network mode active.\nPath: {path}\nAll reads/writes go to the network database.";
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

    public string SyncBeforeTravel()
    {
        if (string.IsNullOrWhiteSpace(NetworkDbPath))
            return "Network database path is not set.";
        var r = SyncService.SyncBeforeTravel(NetworkDbPath);
        if (r.Success) IsTravelMode = true;
        return r.Message;
    }

    public string SyncAfterTravel()
    {
        if (string.IsNullOrWhiteSpace(NetworkDbPath))
            return "Network database path is not set.";
        var r = SyncService.SyncAfterTravel(NetworkDbPath);
        if (r.Success) IsTravelMode = false;
        return r.Message;
    }

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

    partial void OnPercentageCalculationChanged(string value)
    {
        OnPropertyChanged(nameof(IsSitesMode));
        OnPropertyChanged(nameof(IsScoreMode));
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

    // Single admin unlock — controls both Source Data and Database sections
    [ObservableProperty] private bool _isAdminUnlocked = false;

    public bool IsAdminLocked => !IsAdminUnlocked;

    partial void OnIsAdminUnlockedChanged(bool value) => OnPropertyChanged(nameof(IsAdminLocked));

    public void UnlockAdmin() { IsAdminUnlocked = true; }
    public void LockAdmin()   { IsAdminUnlocked = false; }

    // Aliases kept so XAML bindings that still use the old names compile cleanly
    public bool IsSourceDataUnlocked => IsAdminUnlocked;
    public bool IsDatabaseUnlocked   => IsAdminUnlocked;

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

    public void Load()
    {
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
        CloudSyncInterval       = cs.Get(ConfigKeys.CloudSyncInterval, "30");
        StatusMessage           = "";
        LoadDatabaseSettings();
        RefreshCloudSyncStatus();
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

    public void SaveRompStatus(RompStatus status)
    {
        using var db = new AppDbContext();
        var tracked = db.RompStatuses.Find(status.Id);
        if (tracked == null) return;
        tracked.IsComplete     = status.IsComplete;
        tracked.CountsAsActive = status.CountsAsActive;
        tracked.ScoreWeight    = status.ScoreWeight;
        if (!tracked.IsBuiltIn) tracked.Name = status.Name;
        db.SaveChanges();
        StatusConfig.Reload();
        ScoreConfig.Reload();
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

    public void DeleteRompStatus(RompStatus status)
    {
        if (status.IsBuiltIn) return;
        using var db = new AppDbContext();
        var tracked = db.RompStatuses.Find(status.Id);
        if (tracked != null) { db.RompStatuses.Remove(tracked); db.SaveChanges(); }
        StatusConfig.Reload();
        LoadRompStatuses();
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
