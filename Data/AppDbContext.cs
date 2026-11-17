using System.IO;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Data;

public class AppDbContext : DbContext
{
    /// <summary>
    /// When true, connect to the MariaDB cloud database instead of local SQLite.
    /// Only CloudSyncService (and ImportLockService/IdAllocator) should set this; all other
    /// callers use local SQLite. Constructor-only on purpose — see the constructor.
    /// </summary>
    public bool ForceCloud { get; }

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

    /// <summary>When this process last saved anything through EF (UTC). Network mode's
    /// NetworkChangeWatcher uses it to tell its own writes apart from other computers'.</summary>
    public static DateTime LastSaveUtc => new(Interlocked.Read(ref _lastSaveTicks), DateTimeKind.Utc);
    private static long _lastSaveTicks;

    /// <summary>A specific SQLite file instead of <see cref="DatabaseConfig.ActiveSqlitePath"/> —
    /// used to reach the shared network file directly in Network mode (import lock, startup schema
    /// upgrade), since normal contexts open this computer's working copy. Constructor-only, like
    /// ForceCloud. No Id allocation on these contexts.</summary>
    public string? ExplicitSqlitePath { get; }

    /// <param name="forceCloud">True → the MariaDB cloud database (see <see cref="ForceCloud"/>).</param>
    /// <param name="sqlitePath">See <see cref="ExplicitSqlitePath"/>.</param>
    public AppDbContext(bool forceCloud = false, string? sqlitePath = null)
    {
        ExplicitSqlitePath = sqlitePath;
        // ForceCloud MUST be assigned before anything below touches ChangeTracker: that access
        // initializes the context, which runs OnConfiguring and permanently picks the database
        // right then. It used to be an `init` property (set by `new AppDbContext { ForceCloud =
        // true }`, i.e. AFTER this constructor) — once this constructor started touching
        // ChangeTracker, every "cloud" context silently connected to local SQLite instead, and
        // all cloud sync quietly stopped (2026-10-05). Hence a constructor argument.
        ForceCloud = forceCloud;

        // Per-machine Id ranges for new rows in Cloud mode — see IdAllocator. Local contexts
        // only: rows written to the cloud by CloudSyncService always carry their own Ids.
        if (!ForceCloud && ExplicitSqlitePath == null)
            ChangeTracker.Tracked += (_, e) => IdAllocator.OnTracked(this, e);
    }

    public DbSet<PM>                 PMs                  => Set<PM>();
    public DbSet<AssignmentRule>     AssignmentRules      => Set<AssignmentRule>();
    public DbSet<AppConfig>          AppConfigs           => Set<AppConfig>();
    public DbSet<RompStatus>         RompStatuses         => Set<RompStatus>();
    public DbSet<EquipmentWeight>    EquipmentWeights     => Set<EquipmentWeight>();
    public DbSet<EquipmentCategory>  EquipmentCategories  => Set<EquipmentCategory>();
    public DbSet<EquipmentKeyword>   EquipmentKeywords    => Set<EquipmentKeyword>();
    public DbSet<Location>           Locations            => Set<Location>();
    public DbSet<Site>               Sites                => Set<Site>();
    public DbSet<Campus>             Campuses             => Set<Campus>();
    public DbSet<SubRegion>          SubRegions           => Set<SubRegion>();
    public DbSet<TeamMember>         TeamMembers          => Set<TeamMember>();
    public DbSet<Romp>               Romps                => Set<Romp>();
    public DbSet<RompEquipmentType>  RompEquipmentTypes   => Set<RompEquipmentType>();
    public DbSet<Order>              Orders               => Set<Order>();
    public DbSet<SavedReport>        SavedReports         => Set<SavedReport>();
    public DbSet<CustomColumn>       CustomColumns        => Set<CustomColumn>();
    public DbSet<CustomRow>          CustomRows           => Set<CustomRow>();
    public DbSet<User>               Users                => Set<User>();
    public DbSet<AuditEntry>         AuditEntries         => Set<AuditEntry>();
    public DbSet<ForecastType>           ForecastTypes           => Set<ForecastType>();
    public DbSet<ForecastField>          ForecastFields          => Set<ForecastField>();
    public DbSet<ForecastHeaderMapping>  ForecastHeaderMappings  => Set<ForecastHeaderMapping>();
    public DbSet<ForecastImport>         ForecastImports         => Set<ForecastImport>();
    public DbSet<ForecastRow>            ForecastRows            => Set<ForecastRow>();
    public DbSet<AssignmentHistory>  AssignmentHistories  => Set<AssignmentHistory>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (ForceCloud && DatabaseConfig.IsCloud
                       && !string.IsNullOrWhiteSpace(DatabaseConfig.CloudConnectionString))
        {
            // Cloud context used exclusively by CloudSyncService
            options.UseMySql(
                DatabaseConfig.CloudConnectionString,
                ServerVersion.AutoDetect(DatabaseConfig.CloudConnectionString),
                o => o.EnableRetryOnFailure(3));
        }
        else
        {
            // Local SQLite — used by ALL regular app operations regardless of mode.
            // Cloud mode reads/writes local SQLite for speed; CloudSyncService syncs
            // those changes to MariaDB in the background.
            var path = ExplicitSqlitePath ?? DatabaseConfig.ActiveSqlitePath;
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // NetworkDb.ConnectionString adds a long busy timeout: in Network mode another
            // computer may be holding the shared file's write lock (e.g. during an import).
            options.UseSqlite(NetworkDb.ConnectionString(path),
                              o => o.CommandTimeout(NetworkDb.BusyTimeoutSeconds));
        }
    }

    // ── Auto-stamp + event ────────────────────────────────────────────────────

    public override int SaveChanges()
    {
        StampLastModified();
        Interlocked.Exchange(ref _lastSaveTicks, DateTime.UtcNow.Ticks);
        var result = base.SaveChanges();
        // Notify CloudSyncService after a local (non-cloud) save
        if (!ForceCloud && ExplicitSqlitePath == null && (DatabaseConfig.IsCloud || DatabaseConfig.UsesLocalCache))
            LocalSaved?.Invoke();
        return result;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampLastModified();
        Interlocked.Exchange(ref _lastSaveTicks, DateTime.UtcNow.Ticks);
        var task = base.SaveChangesAsync(cancellationToken);
        if (!ForceCloud && ExplicitSqlitePath == null && (DatabaseConfig.IsCloud || DatabaseConfig.UsesLocalCache))
            LocalSaved?.Invoke();
        return task;
    }

    private void StampLastModified()
    {
        if (SkipLastModifiedStamp) return;
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var prop = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "LastModified");
            if (prop != null)
            {
                // Never move a row's timestamp backwards. Since pushes restamp rows with the
                // cloud SERVER's clock (CloudSyncService._lastPullServerUtc), a row's current
                // value can be a little ahead of this machine's clock; an edit stamped plain
                // "now" right after a push would then look OLDER than the pushed copy and lose
                // the last-writer-wins comparison — the edit would never upload.
                var stamp = now;
                if (entry.State == EntityState.Modified
                    && prop.OriginalValue is DateTime prev && prev >= stamp)
                    stamp = prev.AddMilliseconds(1);
                prop.CurrentValue = stamp;
            }

            // Same treatment for the "who" half, on the entities that carry it (Order/Romp/Site).
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

        // Cluster → Campus → Site. Deleting a cluster deletes its campuses (as it does its sites);
        // deleting a campus only detaches its sites (SetNull) — they're re-placed, never lost.
        modelBuilder.Entity<Campus>()
            .HasOne(c => c.Location)
            .WithMany(l => l.Campuses)
            .HasForeignKey(c => c.LocationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Campus>()
            .HasOne(c => c.AssignedPm)
            .WithMany()
            .HasForeignKey(c => c.AssignedPmId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        modelBuilder.Entity<Site>()
            .HasOne(s => s.Campus)
            .WithMany(c => c.Sites)
            .HasForeignKey(s => s.CampusId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        // Teams: Director › PE (sub-region) › PM (PMs.PeId) › PA (TeamMembers.PmId). Removing a
        // sub-region or person only unlinks what pointed at it (SetNull) — nothing is deleted.
        modelBuilder.Entity<TeamMember>()
            .HasOne(t => t.SubRegion)
            .WithMany()
            .HasForeignKey(t => t.SubRegionId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        modelBuilder.Entity<TeamMember>()
            .HasOne(t => t.Pm)
            .WithMany()
            .HasForeignKey(t => t.PmId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        modelBuilder.Entity<PM>()
            .HasOne(p => p.Pe)
            .WithMany()
            .HasForeignKey(p => p.PeId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        modelBuilder.Entity<Site>()
            .HasOne(s => s.SubRegion)
            .WithMany()
            .HasForeignKey(s => s.SubRegionId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        modelBuilder.Entity<Campus>()
            .HasOne(c => c.SubRegion)
            .WithMany()
            .HasForeignKey(c => c.SubRegionId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        modelBuilder.Entity<Location>()
            .HasOne(l => l.SubRegion)
            .WithMany()
            .HasForeignKey(l => l.SubRegionId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

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

        modelBuilder.Entity<Location>()
            .HasOne(l => l.AssignedPm)
            .WithMany()
            .HasForeignKey(l => l.AssignedPmId)
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
        modelBuilder.Entity<Site>()
            .Ignore(s => s.PmDisplay);
        modelBuilder.Entity<Site>().Ignore(s => s.TeamAreaLabel);
        modelBuilder.Entity<Campus>().Ignore(c => c.SiteCount);
        modelBuilder.Entity<Campus>().Ignore(c => c.RompCount);
        modelBuilder.Entity<Campus>().Ignore(c => c.OverrideCount);
        modelBuilder.Entity<Campus>().Ignore(c => c.CampusStatus);
        modelBuilder.Entity<Campus>().Ignore(c => c.PmDisplay);
        modelBuilder.Entity<Campus>().Ignore(c => c.SubRegionDisplay);
        modelBuilder.Entity<PM>().Ignore(p => p.TeamLabel);
        modelBuilder.Entity<PM>().Ignore(p => p.OtherSubRegionIdList);

        modelBuilder.Entity<Location>()
            .Ignore(l => l.IsFullyClosed);
        modelBuilder.Entity<Location>()
            .Ignore(l => l.AssignedPmDisplay);

        modelBuilder.Entity<AssignmentHistory>()
            .HasOne(h => h.Site)
            .WithMany()
            .HasForeignKey(h => h.SiteId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AssignmentHistory>()
            .HasOne(h => h.Pm)
            .WithMany()
            .HasForeignKey(h => h.PmId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssignmentHistory>()
            .HasOne(h => h.Romp)
            .WithMany()
            .HasForeignKey(h => h.RompId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired(false);

        modelBuilder.Entity<Romp>()
            .HasOne(r => r.AssignedPm)
            .WithMany()
            .HasForeignKey(r => r.AssignedPmId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        // ── Forecast Planning ────────────────────────────────────────────────

        modelBuilder.Entity<ForecastField>()
            .HasOne(f => f.ForecastType)
            .WithMany()
            .HasForeignKey(f => f.ForecastTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        modelBuilder.Entity<ForecastHeaderMapping>()
            .HasOne(m => m.ForecastType)
            .WithMany()
            .HasForeignKey(m => m.ForecastTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ForecastHeaderMapping>()
            .HasOne(m => m.ForecastField)
            .WithMany()
            .HasForeignKey(m => m.ForecastFieldId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ForecastImport>()
            .HasOne(i => i.ForecastType)
            .WithMany()
            .HasForeignKey(i => i.ForecastTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ForecastRow>()
            .HasOne(r => r.ForecastImport)
            .WithMany()
            .HasForeignKey(r => r.ForecastImportId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ForecastRow>()
            .HasOne(r => r.ForecastType)
            .WithMany()
            .HasForeignKey(r => r.ForecastTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ForecastRow>()
            .HasOne(r => r.Site)
            .WithMany()
            .HasForeignKey(r => r.SiteId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        modelBuilder.Entity<ForecastField>()
            .Ignore(f => f.ScopeLabel);

        modelBuilder.Entity<ForecastField>()
            .Ignore(f => f.ExcelColumn);
    }
}
