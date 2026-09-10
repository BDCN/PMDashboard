using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Append-only record of user interactions.
///
/// Writes are best-effort and never throw: an audit failure must not abort the action being
/// audited, or logging becomes a way to break the app. Failures surface through LastError
/// rather than an exception. Like every other table, entries are written to local SQLite and
/// carried to the cloud in the background by CloudSyncService — there is only one database
/// context concept in this app, so there is no separate "authoritative vs. mirror" fallback.
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
            LastError = ex.Message;   // deliberately swallowed — see summary above
        }
    }

    /// <summary>Most recent entries first, for the audit log viewer.</summary>
    public static List<AuditEntry> Recent(int take = 500)
    {
        try
        {
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
