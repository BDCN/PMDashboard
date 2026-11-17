using System.IO;
using System.Text.Json;

namespace PMDashboard.Services;

public enum DatabaseMode { Local, Network, Cloud }

/// <summary>
/// Persistent database connection settings stored in %APPDATA%\PMDashboard\dbconfig.json —
/// outside the app database itself so they can be read before the first DbContext is created.
/// </summary>
public static class DatabaseConfig
{
    private static readonly string _configDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PMDashboard");
    private static readonly string _configFile =
        Path.Combine(_configDir, "dbconfig.json");

    // ── Current values (loaded by Load()) ────────────────────────────────────
    public static DatabaseMode Mode                 { get; private set; } = DatabaseMode.Local;
    public static string       NetworkDbPath        { get; private set; } = "";
    public static string       CloudConnectionString { get; private set; } = "";
    public static bool         IsTravelMode         { get; private set; } = false;

    /// <summary>
    /// Network mode only: for each table, the highest Id the network database had when this
    /// computer went into Travel Mode (Sync Before Travel). Rows above it in the local copy were
    /// created while traveling, and Sync After Travel gives them fresh Ids so they can't collide
    /// with rows the office created under the same Ids meanwhile. Null when not traveling, or for
    /// a trip started by a build that didn't record it (merge then falls back to plain Id match).
    /// </summary>
    public static Dictionary<string, long>? TravelBaseline { get; private set; }

    /// <summary>When this trip started (Sync Before Travel), UTC — Sync After Travel only looks at
    /// deletions since then. Null when not traveling.</summary>
    public static DateTime? TravelStartedUtc { get; private set; }

    /// <summary>
    /// True once this machine has ever completed one unconditional "send everything" push to
    /// the cloud. Deliberately stored here (per-machine, never synced) rather than as an
    /// AppConfig row — AppConfigs sync between machines, and a flag meaning "has THIS machine
    /// done its one-time full push" would be meaningless (or actively wrong) if another
    /// machine's value overwrote it. Must persist across app restarts: CloudSyncService used to
    /// track this in memory only, so it reset to false — and re-triggered a full resend of every
    /// row in every table, Orders included — on every single launch, not just the first one ever.
    /// For a 130k+-row Orders table that made every restart pay a multi-minute sync before any
    /// real edit could get pushed, which was long enough for other machines' watermarks to
    /// advance past that edit's timestamp and skip it permanently.
    /// </summary>
    public static bool         CloudFirstPushDone   { get; private set; } = false;

    /// <summary>
    /// Bump <see cref="RequiredFullPushGeneration"/> to make every machine redo one full
    /// "push everything newer than the cloud" pass on its next start — for recovering rows that
    /// a sync bug kept from ever reaching the cloud. Generation 1 = 2026-10-05: every
    /// ForceCloud context was silently writing to local SQLite, so nothing saved during that
    /// window was uploaded, and its LastModified is now older than the incremental cursor.
    /// Safe to re-run: BatchPush only applies rows that are newer than the cloud's copy.
    /// </summary>
    public const int RequiredFullPushGeneration = 1;
    public static int          CloudFullPushGeneration { get; private set; } = 0;
    public static bool         CloudFullPushCurrent => CloudFirstPushDone && CloudFullPushGeneration >= RequiredFullPushGeneration;

    /// <summary>
    /// True once this machine has done one unconditional "send everything since the beginning
    /// of time" sync pass for the 5 Forecast Planning tables specifically. Needed because the
    /// Forecast tables were added to the cloud schema after other machines had already pushed
    /// forecast data — those pushes failed per-table (table didn't exist yet) but the global
    /// sync watermark (<c>_lastSyncUtc</c>) still advanced every cycle regardless, since
    /// per-table failures are caught and swallowed. That left already-uploaded forecast rows
    /// permanently stuck: their LastModified became older than the watermark, so the normal
    /// incremental "only rows newer than since" push/pull would never pick them up again, even
    /// after the missing tables were created. This flag forces one more MinValue (full-history)
    /// push+pull of just those 5 tables to flush the stuck rows, the same way
    /// <see cref="CloudFirstPushDone"/> does for the whole database on a brand new machine.
    /// </summary>
    public static bool         ForecastFirstSyncDone { get; private set; } = false;

    /// <summary>This machine's primary-key Id slot in the cloud database identified by
    /// <see cref="IdSlotCloud"/> — see <see cref="IdAllocator"/>. Null until claimed.</summary>
    public static int?         IdSlot               { get; private set; }
    public static string       IdSlotCloud          { get; private set; } = "";

    // ── Computed paths ────────────────────────────────────────────────────────
    public static string LocalDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PMDashboard", "data.db");

    /// <summary>Network mode's working copy of the shared database on this computer — every page
    /// reads and writes it; <see cref="NetworkSync"/> keeps it in step with the network file.
    /// Separate from <see cref="LocalDbPath"/> so switching to Network mode never overwrites this
    /// computer's own Local-mode data.</summary>
    public static string NetworkCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PMDashboard", "network-cache.db");

    /// <summary>True only for <c>PMDashboard.exe --new-database</c>: that run opens the given file
    /// directly instead of through the local working copy.</summary>
    public static bool DirectNetworkFile { get; private set; }

    /// <summary>Network mode (not a legacy travel trip): the app works on <see cref="NetworkCachePath"/>
    /// and NetworkSync syncs it with the network file in the background (v1.13.0).</summary>
    public static bool UsesLocalCache =>
        Mode == DatabaseMode.Network && !IsTravelMode && !DirectNetworkFile && !string.IsNullOrWhiteSpace(NetworkDbPath);

    /// <summary>
    /// The SQLite file path the app should use right now.
    /// Network mode → this computer's working copy (synced in the background), or the network file
    /// itself for --new-database. A legacy Travel Mode trip / Local mode → local file.
    /// Cloud mode → unused (MySQL connection string is used instead).
    /// </summary>
    public static string ActiveSqlitePath
    {
        get
        {
            if (Mode == DatabaseMode.Network && !IsTravelMode && !string.IsNullOrWhiteSpace(NetworkDbPath))
                return DirectNetworkFile ? NetworkDbPath : NetworkCachePath;
            return LocalDbPath;
        }
    }

    public static bool IsCloud   => Mode == DatabaseMode.Cloud;
    public static bool IsNetwork => Mode == DatabaseMode.Network;
    public static bool IsLocal   => Mode == DatabaseMode.Local;

    // ── Persistence ───────────────────────────────────────────────────────────

    public static void Load()
    {
        if (!File.Exists(_configFile)) return;
        try
        {
            var json = File.ReadAllText(_configFile);
            var obj  = JsonSerializer.Deserialize<Dto>(json);
            if (obj == null) return;
            Mode                  = obj.Mode;
            NetworkDbPath         = obj.NetworkDbPath        ?? "";
            CloudConnectionString = obj.CloudConnectionString ?? "";
            IsTravelMode          = obj.IsTravelMode;
            TravelBaseline        = obj.TravelBaseline;
            TravelStartedUtc      = obj.TravelStartedUtc;
            CloudFirstPushDone    = obj.CloudFirstPushDone;
            CloudFullPushGeneration = obj.CloudFullPushGeneration;
            ForecastFirstSyncDone = obj.ForecastFirstSyncDone;
            IdSlot                = obj.IdSlot;
            IdSlotCloud           = obj.IdSlotCloud ?? "";

            // The MariaDB cloud database is retired (2026-11-17 — the team uses Network mode).
            // A machine still set to Cloud switches to Local: its local SQLite file already holds
            // everything it synced, so nothing is lost, and CloudSyncService never starts
            // (IsCloud is false from here on). The connection string is left in the file untouched.
            if (Mode == DatabaseMode.Cloud)
            {
                Mode = DatabaseMode.Local;
                RetiredFromCloud = true;
                Save();
            }
        }
        catch { /* corrupt file — fall back to Local */ }
    }

    /// <summary>True for the one run in which <see cref="Load"/> switched this machine off the
    /// retired cloud database — App startup tells the user once.</summary>
    public static bool RetiredFromCloud { get; private set; }

    public static void Save()
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configFile, JsonSerializer.Serialize(
            new Dto
            {
                Mode                  = Mode,
                NetworkDbPath         = NetworkDbPath,
                CloudConnectionString = CloudConnectionString,
                IsTravelMode          = IsTravelMode,
                TravelBaseline        = TravelBaseline,
                TravelStartedUtc      = TravelStartedUtc,
                CloudFirstPushDone    = CloudFirstPushDone,
                CloudFullPushGeneration = CloudFullPushGeneration,
                ForecastFirstSyncDone = ForecastFirstSyncDone,
                IdSlot                = IdSlot,
                IdSlotCloud           = IdSlotCloud,
            },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Called by CloudSyncService once its first unconditional full push completes.</summary>
    public static void MarkCloudFirstPushDone()
    {
        if (CloudFullPushCurrent) return;
        CloudFirstPushDone      = true;
        CloudFullPushGeneration = RequiredFullPushGeneration;
        Save();
    }

    /// <summary>Called by CloudSyncService once its one-time full-history Forecast sync completes.</summary>
    public static void MarkForecastFirstSyncDone()
    {
        if (ForecastFirstSyncDone) return;
        ForecastFirstSyncDone = true;
        Save();
    }

    /// <summary>Called by IdAllocator once it has claimed a slot in the cloud.</summary>
    public static void SetIdSlot(int slot, string cloudIdentity)
    {
        IdSlot      = slot;
        IdSlotCloud = cloudIdentity;
        Save();
    }

    // ── Apply helpers (call then restart the EF contexts as needed) ───────────

    public static void ApplyLocal()
    {
        Mode         = DatabaseMode.Local;
        IsTravelMode = false;
        TravelBaseline = null;
        Save();
    }

    /// <summary>
    /// Points this run (only — nothing is saved to dbconfig.json) at <paramref name="path"/> as if
    /// it were the network database. Used by <c>PMDashboard.exe --new-database</c> to build a blank
    /// shared database through the normal startup schema/seed code.
    /// </summary>
    public static void UseForThisRunOnly(string path)
    {
        Mode              = DatabaseMode.Network;
        NetworkDbPath     = path;
        IsTravelMode      = false;
        DirectNetworkFile = true;
    }

    public static void ApplyNetwork(string path)
    {
        Mode          = DatabaseMode.Network;
        NetworkDbPath = path;
        IsTravelMode  = false;
        TravelBaseline = null;
        Save();
    }

    // Unreachable from the UI (Cloud option hidden); even if called, the next Load() reverts to Local.
    public static void ApplyCloud(string connectionString)
    {
        Mode                  = DatabaseMode.Cloud;
        CloudConnectionString = connectionString;
        IsTravelMode          = false;
        Save();
    }

    /// <param name="baseline">Going into Travel Mode: the network database's max Id per table
    /// (see <see cref="TravelBaseline"/>). Ignored — and cleared — when coming back.</param>
    public static void SetTravelMode(bool traveling, Dictionary<string, long>? baseline = null)
    {
        IsTravelMode     = traveling;
        TravelBaseline   = traveling ? baseline : null;
        TravelStartedUtc = traveling ? DateTime.UtcNow : null;
        Save();
    }

    private class Dto
    {
        public DatabaseMode Mode                  { get; set; } = DatabaseMode.Local;
        public string?      NetworkDbPath         { get; set; }
        public string?      CloudConnectionString { get; set; }
        public bool         IsTravelMode          { get; set; }
        public Dictionary<string, long>? TravelBaseline { get; set; }
        public DateTime?    TravelStartedUtc      { get; set; }
        public bool         CloudFirstPushDone    { get; set; }
        public int          CloudFullPushGeneration { get; set; }
        public bool         ForecastFirstSyncDone { get; set; }
        public int?         IdSlot                { get; set; }
        public string?      IdSlotCloud           { get; set; }
    }
}
