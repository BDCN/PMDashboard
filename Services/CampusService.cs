using Microsoft.EntityFrameworkCore;
using PMDashboard.Data;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

/// <summary>
/// Campus-level operations: assigning a PM to a campus (cascading to its sites), site overrides,
/// and moving sites (with their ROMPs) between campuses. See Campus.cs for the model.
/// Each method saves its own changes and writes one audit entry.
/// </summary>
public static class CampusService
{
    /// <summary>Find a campus in a cluster by exact name, or create it.</summary>
    public static Campus GetOrCreate(AppDbContext db, int locationId, string name)
    {
        name = name.Trim();
        var existing = db.Campuses.Local.FirstOrDefault(c => c.LocationId == locationId && c.Name == name)
                    ?? db.Campuses.Where(c => c.LocationId == locationId && c.Name == name).OrderBy(c => c.Id).FirstOrDefault();
        if (existing != null) return existing;
        var created = new Campus { LocationId = locationId, Name = name };
        db.Campuses.Add(created);
        return created;
    }

    /// <summary>
    /// Assigns (or, with null, unassigns) a campus's PM and writes it onto every site in the campus
    /// that isn't a site-level override. Returns how many sites changed PM.
    /// </summary>
    public static int AssignCampus(int campusId, int? pmId)
    {
        using var db = new AppDbContext();
        var campus = db.Campuses.Include(c => c.Sites).FirstOrDefault(c => c.Id == campusId);
        if (campus == null) return 0;
        if (pmId is { } checkPm) TeamRules.For(db).Ensure(checkPm, campus.LocationId, campus.Id);

        campus.AssignedPmId = pmId;
        int changed = 0;
        foreach (var site in campus.Sites.Where(s => !s.PmOverride))
        {
            if (site.AssignedPmId != pmId) changed++;
            site.Campus = campus;
            AssignmentHistoryService.RecordChange(db, site, pmId);
        }
        // A site whose own PM is now the campus's PM is no longer an override.
        foreach (var site in campus.Sites.Where(s => s.PmOverride && pmId != null && s.AssignedPmId == pmId))
            site.PmOverride = false;
        db.SaveChanges();

        var pmName = pmId is { } id ? db.PMs.Find(id)?.FullName ?? "PM" : null;
        AuditService.Log(pmId == null ? "Unassign" : "Assign", "Campus", campus.Id,
            pmId == null ? $"Unassigned campus \"{campus.Name}\" ({changed} site(s))"
                         : $"Assigned {pmName} to campus \"{campus.Name}\" ({changed} site(s) changed)");
        return changed;
    }

    /// <summary>Number of sites in the campus that keep their own PM (overrides).</summary>
    public static int OverrideCount(int campusId)
    {
        using var db = new AppDbContext();
        return db.Sites.Count(s => s.CampusId == campusId && s.PmOverride);
    }

    /// <summary>Puts a site back on its campus's PM (clears a site-level override).</summary>
    public static void ClearOverride(int siteId)
    {
        using var db = new AppDbContext();
        var site = db.Sites.Include(s => s.Campus).FirstOrDefault(s => s.Id == siteId);
        if (site == null) return;
        AssignmentHistoryService.RecordChange(db, site, site.Campus?.AssignedPmId);
        site.PmOverride = false;
        db.SaveChanges();
        AuditService.Log("Assign", "Site", site.Id, $"Site \"{site.Name}\" now follows its campus's PM");
    }

    /// <summary>
    /// Moves a site — and with it all its ROMPs, which hang off the site — to another campus.
    /// Marks it CampusManual so forecast uploads never move it back. A site without its own
    /// override takes on the new campus's PM.
    /// </summary>
    public static void MoveSite(int siteId, int newCampusId)
    {
        using var db = new AppDbContext();
        var site   = db.Sites.FirstOrDefault(s => s.Id == siteId);
        var campus = db.Campuses.FirstOrDefault(c => c.Id == newCampusId);
        if (site == null || campus == null) return;

        var from = site.CampusId is { } fid ? db.Campuses.Where(c => c.Id == fid).Select(c => c.Name).FirstOrDefault() : null;
        site.CampusId     = campus.Id;
        site.Campus       = campus;
        site.LocationId   = campus.LocationId; // a campus belongs to one cluster; the site follows it
        site.CampusManual = true;
        if (!site.PmOverride)
            AssignmentHistoryService.RecordChange(db, site, campus.AssignedPmId);
        else
            AssignmentHistoryService.RecordChange(db, site, site.AssignedPmId); // re-evaluates the override flag
        db.SaveChanges();
        AuditService.Log("Move", "Site", site.Id, $"Moved site \"{site.Name}\" from campus \"{from ?? "(none)"}\" to \"{campus.Name}\"");
    }
}
