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

    // Forecast Timeline schedule-conflict detection — see ScheduleConflictService
    public const string CxWindowDays           = "CxWindowDays";           // days before PFHO treated as the CX/cutover window (default 14)
    public const string MaxConcurrentCxPerRegion = "MaxConcurrentCxPerRegion"; // max sites one PM can have in the same region's CX window at once before flagging (default 1)

    // Coverage Planner — see Services/CoveragePlanner.cs for the model
    public const string CoverageBuildWeight   = "CoverageBuildWeight";   // load per day during a campus's build phase, NBD → CX (default 0.25)
    public const string CoverageCxWeight      = "CoverageCxWeight";      // load per day during the CX window (default 1.0)
    public const string CoverageCapacityPerPm = "CoverageCapacityPerPm"; // load one PM can carry at once (default 3.0 — e.g. 12 campuses in build, or 3 in CX)
    public const string CoverageHireLeadDays  = "CoverageHireLeadDays";  // hire this many days before a shortfall starts (default 90)
    public const string CoverageHorizonWeeks  = "CoverageHorizonWeeks";  // how far ahead to plan (default 104 = 2 years)
}
