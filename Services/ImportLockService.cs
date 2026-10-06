using System.Text.Json;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Best-effort import lock so only one person imports at a time. Cloud-mode-only: Local mode is
/// single-machine, and Network mode's manual Sync Before/After Travel already serializes access
/// by nature — locking only matters where multiple people have live concurrent access, which is
/// Cloud mode alone. Checked directly against the cloud (via ForceCloud), not the local mirror,
/// since a lock has to be read from the authoritative source, not something that might be stale
/// by up to the sync interval.
/// </summary>
public static class ImportLockService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private sealed record LockInfo(string HeldBy, string Machine, DateTime AcquiredUtc);

    /// <summary>
    /// Attempts to acquire the lock. Returns (true, null) on success, or (false, a message
    /// describing who currently holds it) if someone else's lock is still live. A lock older
    /// than 30 minutes is treated as abandoned (e.g. a crashed import) and silently reclaimed.
    /// </summary>
    public static (bool Acquired, string? HeldByMessage) TryAcquire(string userEmail)
    {
        if (!DatabaseConfig.IsCloud) return (true, null);

        try
        {
            using var db = new AppDbContext(forceCloud: true);
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
            // Best-effort: if the cloud is unreachable right now, don't block a local import on it.
            return (true, null);
        }
    }

    /// <summary>Releases the lock, but only if this same user still holds it.</summary>
    public static void Release(string userEmail)
    {
        if (!DatabaseConfig.IsCloud) return;

        try
        {
            using var db = new AppDbContext(forceCloud: true);
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
