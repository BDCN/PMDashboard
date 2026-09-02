using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Append-only record of user interactions.
///
/// Writes are best-effort and never throw: an audit failure must not abort the action being
/// audited, or logging becomes a way to break the app. Failures surface through LastError
/// rather than an exception.
///
/// Entries go to the authoritative database (the cloud in Cloud mode) via a normal context,
/// so the trail is shared across machines rather than being per-machine.
/// </summary>
public static class AuditService
{
    public static string LastError { get; private set; } = "";

    /// <summary>Set false to suppress logging, e.g. during bulk seeding.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Log(string action, string entityType = "", int? entityId = null, string details = "")
    {
        if (!Enabled) return;
        try
        {
            using var db = new AppDbContext();
            db.AuditEntries.Add(new AuditEntry
            {
                TimestampUtc = DateTime.UtcNow,
                UserEmail    = CurrentUser.Email,
                UserName     = CurrentUser.DisplayName,
                Action       = action,
                EntityType   = entityType,
                EntityId     = entityId,
                Details      = Truncate(details, 2000),
                MachineName  = Environment.MachineName,
            });
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;   // deliberately swallowed - see summary above

            // The authoritative database may not have the AuditEntries table yet (a cloud
            // created from an older setup script). Keep the entry locally rather than lose it.
            try
            {
                using var local = new AppDbContext { ForceLocal = true };
                local.AuditEntries.Add(new AuditEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    UserEmail    = CurrentUser.Email,
                    UserName     = CurrentUser.DisplayName,
                    Action       = action,
                    EntityType   = entityType,
                    EntityId     = entityId,
                    Details      = Truncate(details, 2000),
                    MachineName  = Environment.MachineName,
                });
                local.SaveChanges();
            }
            catch { /* nothing more to do - auditing must never break the audited action */ }
        }
    }

    /// <summary>Most recent entries first, for the audit log viewer.</summary>
    public static List<AuditEntry> Recent(int take = 500)
    {
        try
        {
            // Authoritative rather than the mirror, so the trail is complete even on a
            // machine whose mirror has not caught up yet. Bounded by Take().
            using var db = new AppDbContext();
            return db.AuditEntries.AsNoTracking()
                     .OrderByDescending(a => a.TimestampUtc)
                     .Take(take)
                     .ToList();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new List<AuditEntry>();
        }
    }

    /// <summary>Everything recorded against one record, newest first.</summary>
    public static List<AuditEntry> ForEntity(string entityType, int entityId)
    {
        try
        {
            using var db = new AppDbContext();
            return db.AuditEntries.AsNoTracking()
                     .Where(a => a.EntityType == entityType && a.EntityId == entityId)
                     .OrderByDescending(a => a.TimestampUtc)
                     .ToList();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new List<AuditEntry>();
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
