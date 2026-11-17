using System.Text.Json;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Best-effort import lock so only one person imports at a time. Applies wherever several people
/// have live access to the same data: Cloud mode (lock row written straight to the cloud via
/// ForceCloud, not the local mirror, which can be stale by up to the sync interval) and Network
/// mode on the shared file (a normal context IS the shared file). Local mode and Network Travel
/// Mode are single-machine — always granted.
/// </summary>
public static class ImportLockService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private static bool IsShared => DatabaseConfig.IsCloud || NetworkDb.IsShared;

    /// <summary>The lock lives in the SHARED copy: the cloud, or in Network mode the network file
    /// itself (normal contexts open this computer's working copy, which other computers never see).</summary>
    private static AppDbContext LockContext() =>
        DatabaseConfig.UsesLocalCache ? new AppDbContext(sqlitePath: DatabaseConfig.NetworkDbPath)
                                      : new AppDbContext(forceCloud: DatabaseConfig.IsCloud);

    private sealed record LockInfo(string HeldBy, string Machine, DateTime AcquiredUtc);

    /// <summary>
    /// Attempts to acquire the lock. Returns (true, null) on success, or (false, a message
    /// describing who currently holds it) if someone else's lock is still live. A lock older
    /// than 30 minutes is treated as abandoned (e.g. a crashed import) and silently reclaimed.
    /// </summary>
    public static (bool Acquired, string? HeldByMessage) TryAcquire(string userEmail)
    {
        if (!IsShared) return (true, null);

        // Network mode: an import needs the shared file — both for this lock and to fetch/send
        // the data around it (NetworkSync). Offline → refuse rather than risk two imports at once.
        if (DatabaseConfig.UsesLocalCache && NetworkDb.CheckReachable(DatabaseConfig.NetworkDbPath) is { } problem)
            return (false, "Imports need the shared network database, and it can't be reached right now.\n\n" + problem);

        try
        {
            using var db = LockContext();
            var row = db.AppConfigs.Find(ConfigKeys.ImportLock);

            if (row != null && !string.IsNullOrWhiteSpace(row.Value))
            {
                var existing = Deserialize(row.Value);
                if (existing != null
                    && !string.Equals(existing.HeldBy, userEmail, StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - existing.AcquiredUtc < StaleAfter)
                {
                    var minutes = Math.Max(1, (int)(DateTime.UtcNow - existing.AcquiredUtc).TotalMinutes);
                    return (false, $"{existing.HeldBy} is currently importing (started {minutes} minute(s) ago on {existing.Machine}). Try again shortly.");
                }
            }

            var json = JsonSerializer.Serialize(new LockInfo(userEmail, Environment.MachineName, DateTime.UtcNow));
            if (row == null)
                db.AppConfigs.Add(new AppConfig { Key = ConfigKeys.ImportLock, Value = json });
            else
                row.Value = json;
            db.SaveChanges();
            return (true, null);
        }
        catch
        {
            // Best-effort: if the lock row can't be read/written right now, don't block the import on it.
            return (true, null);
        }
    }

    /// <summary>Releases the lock, but only if this same user still holds it.</summary>
    public static void Release(string userEmail)
    {
        if (!IsShared) return;

        try
        {
            using var db = LockContext();
            var row = db.AppConfigs.Find(ConfigKeys.ImportLock);
            if (row == null) return;

            var existing = Deserialize(row.Value);
            if (existing != null && !string.Equals(existing.HeldBy, userEmail, StringComparison.OrdinalIgnoreCase))
                return;

            db.AppConfigs.Remove(row);
            db.SaveChanges();
        }
        catch { /* best-effort — a stuck lock still self-clears after StaleAfter */ }
    }

    private static LockInfo? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<LockInfo>(json); }
        catch { return null; }
    }
}
