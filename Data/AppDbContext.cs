using System.IO;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Data;

public class AppDbContext : DbContext
{
    /// <summary>
    /// Forces the MariaDB cloud database regardless of the reachability probe.
    /// Used by CloudSyncService for the mirror's cloud-side reads.
    /// </summary>
    public bool ForceCloud { get; init; }

    /// <summary>
    /// Forces the local SQLite file even in Cloud mode. Used for the startup schema work
    /// (whose raw SQL is SQLite dialect) and by CloudSyncService when writing the mirror.
    /// </summary>
    public bool ForceLocal { get; init; }

    /// <summary>Set on read contexts; SaveChanges throws rather than writing to the mirror.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// A context for bulk reads: grids, reports and any query that materialises many rows.
    ///
    /// In Cloud mode these come from the local mirror rather than MariaDB. Writes still go to
    /// the cloud through a normal context, so the server keeps assigning identity, but pulling
    /// 131k orders across a WAN link on every page load is what makes cloud-direct reads
    /// unusable at this data size. In Local/Network mode this is simply the live database.
    ///
    /// It is read-only on purpose: saving through it would write to the mirror, where the change
    /// would be silently overwritten by the next pull. Misuse throws instead of losing data.
    /// </summary>
    public static AppDbContext ForRead() => new() { ForceLocal = true, ReadOnly = true };

    /// <summary>
    /// Set to true by CloudSyncService when writing data pulled from the cloud so that
    /// StampLastModified does not overwrite the cloud-authoritative LastModified value.
    /// </summary>
    public bool SkipLastModifiedStamp { get; set; }

    /// <summary>
    /// Raised after a successful local SaveChanges in Cloud mode so CloudSyncService
    /// can push the change to cloud immediately (in addition to the periodic timer push).
    /// </summary>
    public static event Action? LocalSaved;

    /// <summary>
    /// Raised after a write that went to the cloud, so the mirror can be refreshed straight
    /// away. Without it a grid reading the mirror would not show an edit until the next
    /// timer tick, up to the sync interval later.
    /// </summary>
    public static event Action? CloudSaved;

    public DbSet<PM>                 PMs                  => Set<PM>();
    public DbSet<AssignmentRule>     AssignmentRules      => Set<AssignmentRule>();
    public DbSet<AppConfig>          AppConfigs           => Set<AppConfig>();
    public DbSet<RompStatus>         RompStatuses         => Set<RompStatus>();
    public DbSet<EquipmentWeight>    EquipmentWeights     => Set<EquipmentWeight>();
    public DbSet<EquipmentCategory>  EquipmentCategories  => Set<EquipmentCategory>();
    public DbSet<EquipmentKeyword>   EquipmentKeywords    => Set<EquipmentKeyword>();
    public DbSet<Location>           Locations            => Set<Location>();
    public DbSet<Site>               Sites                => Set<Site>();
    public DbSet<Romp>               Romps                => Set<Romp>();
    public DbSet<RompEquipmentType>  RompEquipmentTypes   => Set<RompEquipmentType>();
    public DbSet<Order>              Orders               => Set<Order>();
    public DbSet<SavedReport>        SavedReports         => Set<SavedReport>();
    public DbSet<CustomColumn>       CustomColumns        => Set<CustomColumn>();
    public DbSet<CustomRow>          CustomRows           => Set<CustomRow>();
    public DbSet<User>               Users                => Set<User>();
    public DbSet<AuditEntry>         AuditEntries         => Set<AuditEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // Cloud is authoritative in Cloud mode: every context reads and writes MariaDB
        // directly, and the local SQLite file is only a mirror for offline access.
        // ForceLocal opts out (startup DDL, mirror writes); ForceCloud opts in regardless
        // of the reachability probe.
        var useCloud = !ForceLocal
                       && DatabaseConfig.IsCloud
                       && !string.IsNullOrWhiteSpace(DatabaseConfig.CloudConnectionString)
                       && (ForceCloud || CloudRuntime.IsCloudActive);

        if (useCloud)
        {
            options.UseMySql(
                DatabaseConfig.CloudConnectionString,
                ServerVersion.AutoDetect(DatabaseConfig.CloudConnectionString),
                o => o.EnableRetryOnFailure(3));
        }
        else
        {
            // Local SQLite — the mirror in Cloud mode, the live database otherwise.
            var path = DatabaseConfig.ActiveSqlitePath;
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            options.UseSqlite($"Data Source={path}");
        }
    }

    // ── Auto-stamp + event ────────────────────────────────────────────────────

    public override int SaveChanges()
    {
        GuardReadOnly();
        StampLastModified();
        var result = base.SaveChanges();
        // Only a save that actually landed in the LOCAL file needs pushing later. When the
        // cloud is active the write already went to the server, so there is nothing to push.
        if (NeedsDeferredPush) LocalSaved?.Invoke();
        else if (WroteToCloud)  CloudSaved?.Invoke();
        return result;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardReadOnly();
        StampLastModified();
        var task = base.SaveChangesAsync(cancellationToken);
        if (NeedsDeferredPush) LocalSaved?.Invoke();
        else if (WroteToCloud)  CloudSaved?.Invoke();
        return task;
    }

    private void GuardReadOnly()
    {
        if (ReadOnly)
            throw new InvalidOperationException(
                "This AppDbContext came from ForRead() and cannot save. Reads are served from the "
              + "local mirror in Cloud mode; writes must use a normal context so they reach the server.");
    }

    /// <summary>
    /// True when this save went to the local mirror while Cloud mode is configured — i.e. the
    /// app is running on the offline fallback, so the change still owes the server an update.
    /// </summary>
    private bool NeedsDeferredPush =>
        !ForceCloud && !ForceLocal && DatabaseConfig.IsCloud && !CloudRuntime.IsCloudActive;

    /// <summary>True when this save landed on the server, so the mirror is now behind.</summary>
    private bool WroteToCloud =>
        !ForceLocal && DatabaseConfig.IsCloud && CloudRuntime.IsCloudActive;

    private void StampLastModified()
    {
        if (SkipLastModifiedStamp) return;
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var prop = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "LastModified");
            if (prop != null) prop.CurrentValue = now;

            // Same treatment for the "who" half, on entities that carry it. Skipped along with
            // the timestamp when SkipLastModifiedStamp is set, so a sync never rewrites the
            // author of a row it is merely copying.
            var byProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "LastModifiedBy");
            if (byProp != null) byProp.CurrentValue = CurrentUser.Email;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Site>()
            .HasOne(s => s.Location)
            .WithMany(l => l.Sites)
            .HasForeignKey(s => s.LocationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Romp>()
            .HasOne(r => r.Site)
            .WithMany(s => s.Romps)
            .HasForeignKey(r => r.SiteId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Site>()
            .HasOne(s => s.AssignedPm)
            .WithMany()
            .HasForeignKey(s => s.AssignedPmId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        modelBuilder.Entity<AppConfig>()
            .HasKey(c => c.Key);

        modelBuilder.Entity<EquipmentKeyword>()
            .HasOne(k => k.Category)
            .WithMany(c => c.Keywords)
            .HasForeignKey(k => k.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RompEquipmentType>()
            .HasOne(rt => rt.Romp)
            .WithMany(r => r.EquipmentTypes)
            .HasForeignKey(rt => rt.RompId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RompEquipmentType>()
            .HasOne(rt => rt.Category)
            .WithMany()
            .HasForeignKey(rt => rt.EquipmentCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RompEquipmentType>()
            .HasIndex(rt => new { rt.RompId, rt.EquipmentCategoryId })
            .IsUnique();

        modelBuilder.Entity<Romp>()
            .Ignore(r => r.GeographicalLocation);

        modelBuilder.Entity<Site>()
            .Ignore(s => s.SiteStatus);
    }
}
