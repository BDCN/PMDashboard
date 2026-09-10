using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

public class ConfigService
{
    public string Get(string key, string defaultValue = "")
    {
        using var db = new AppDbContext();
        return db.AppConfigs.Find(key)?.Value ?? defaultValue;
    }

    public void Set(string key, string value)
    {
        using var db = new AppDbContext();
        var existing = db.AppConfigs.Find(key);
        if (existing != null)
            existing.Value = value;
        else
            db.AppConfigs.Add(new AppConfig { Key = key, Value = value });
        db.SaveChanges();
    }
}

// Well-known config keys
public static class ConfigKeys
{
    public const string UploadMethod      = "UploadMethod";        // "Manual" | "SemiAutomatic"
    public const string ImportFolder      = "ImportFolder";
    public const string PollTimeSeconds   = "PollTimeSeconds";
    public const string SourceDataType    = "SourceDataType";      // "VADATA" | "Tableau" | "OpenLines"
    public const string ItemFilterPrefix  = "ItemFilterPrefix";    // Prefix on Item column (default "SOLI") — kept for import display purposes
    public const string PercentageCalculation = "PercentageCalculation"; // "Sites" | "Score"

    // Custom Data grid column order, per mode. Suffixed with the ReportMode name
    // ("CustomDataColumnOrder_Orders"), value is a JSON array of column keys.
    public const string CustomDataColumnOrderPrefix = "CustomDataColumnOrder_";

    // Cloud background sync
    public const string CloudSyncInterval = "CloudSyncInterval";  // seconds between sync cycles (default 30)

    // Cloud-mode-only best-effort import lock — see ImportLockService. JSON: who/machine/timestamp.
    public const string ImportLock = "ImportLock";

    // Workload scoring parameters (Score mode)
    public const string SiteOverhead      = "SiteOverhead";       // site_factor overhead per distinct site (default 0.05)
    public const string TargetCapacity    = "TargetCapacity";     // denominator for utilization (default 259.2)
    public const string NearFullThreshold = "NearFullThreshold";  // utilization ratio at which PM is "Near Full" (default 0.80)
    public const string OverloadThreshold = "OverloadThreshold";  // utilization ratio at which PM is "Overloaded" (default 1.00)

    // PM experience multiplier config (used in eff_capacity calculation)
    public const string SkillBase     = "SkillBase";      // floor factor (default 0.40)
    public const string SkillPerPoint = "SkillPerPoint";  // per-point increment (default 0.06)
}
