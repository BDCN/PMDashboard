using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Every code path that (re)assigns or unassigns a site's PM should go through
/// <see cref="RecordChange"/> instead of setting Site.AssignedPmId directly — that field only
/// ever holds the current PM, so a direct assignment silently loses the previous holder the
/// moment a site is reassigned. This closes out the previous open AssignmentHistory row (if
/// any) and opens a new one for the incoming PM (if any), then sets AssignedPmId itself, so
/// callers just call this in place of the old assignment line.
/// </summary>
public static class AssignmentHistoryService
{
    public static void RecordChange(AppDbContext db, Site site, int? newPmId)
    {
        // Team boundary (hard rule): throws TeamRuleException before anything is changed.
        if (newPmId is { } checkPm && checkPm != site.AssignedPmId)
            TeamRules.For(db).Ensure(checkPm, site.LocationId, site.CampusId);

        // Campus-level PM with site overrides (see Campus.cs): a site whose PM differs from its
        // campus's (assigned) PM is an override, so assigning the campus later leaves it alone.
        // Computed here because every path that changes a site's PM comes through this method.
        // A campus with no PM yet makes nothing an override — assigning the campus then sets all.
        int? campusPmId = site.CampusId is { } cid
            ? (site.Campus?.Id == cid ? site.Campus.AssignedPmId : db.Campuses.Where(c => c.Id == cid).Select(c => c.AssignedPmId).FirstOrDefault())
            : null;
        site.PmOverride = campusPmId != null && newPmId != campusPmId;

        var oldPmId = site.AssignedPmId;
        if (oldPmId == newPmId) return;

        var now = DateTime.UtcNow;
        var who = CurrentUser.Email;

        if (oldPmId is { } prevPmId)
        {
            var open = db.AssignmentHistories
                .Where(h => h.SiteId == site.Id && h.PmId == prevPmId && h.UnassignedAtUtc == null)
                .OrderByDescending(h => h.AssignedAtUtc)
                .FirstOrDefault();
            if (open != null)
            {
                open.UnassignedAtUtc = now;
                open.UnassignedBy    = who;
                open.LastModified    = now;
            }
        }

        if (newPmId is { } newId)
        {
            db.AssignmentHistories.Add(new AssignmentHistory
            {
                SiteId        = site.Id,
                PmId          = newId,
                AssignedAtUtc = now,
                AssignedBy    = who,
                LastModified  = now,
            });
        }

        site.AssignedPmId = newPmId;
    }

    /// <summary>
    /// Same idea as <see cref="RecordChange"/>, for an individual ROMP-level override instead of
    /// a whole campus. RompId is set on the history row (SiteId still populated from the ROMP's
    /// own Site, so "history for this campus" queries pick these up too) so the open/close
    /// lookup is scoped to (RompId, PmId) rather than (SiteId, PmId) — a ROMP override and its
    /// campus's own assignment must never close out each other's history rows.
    /// </summary>
    public static void RecordRompChange(AppDbContext db, Romp romp, int? newPmId)
    {
        var oldPmId = romp.AssignedPmId;
        if (oldPmId == newPmId) return;
        if (newPmId is { } checkPm)
        {
            var at = romp.Site is { } st ? new { st.LocationId, st.CampusId }
                   : db.Sites.Where(s => s.Id == romp.SiteId).Select(s => new { s.LocationId, s.CampusId }).First();
            TeamRules.For(db).Ensure(checkPm, at.LocationId, at.CampusId);
        }

        var now = DateTime.UtcNow;
        var who = CurrentUser.Email;

        if (oldPmId is { } prevPmId)
        {
            var open = db.AssignmentHistories
                .Where(h => h.RompId == romp.Id && h.PmId == prevPmId && h.UnassignedAtUtc == null)
                .OrderByDescending(h => h.AssignedAtUtc)
                .FirstOrDefault();
            if (open != null)
            {
                open.UnassignedAtUtc = now;
                open.UnassignedBy    = who;
                open.LastModified    = now;
            }
        }

        if (newPmId is { } newId)
        {
            db.AssignmentHistories.Add(new AssignmentHistory
            {
                SiteId        = romp.SiteId,
                RompId        = romp.Id,
                PmId          = newId,
                AssignedAtUtc = now,
                AssignedBy    = who,
                LastModified  = now,
            });
        }

        romp.AssignedPmId = newPmId;
    }
}
