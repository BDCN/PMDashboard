using System.IO;
using Microsoft.EntityFrameworkCore;
using PMDashboard.Data.Models;
using PMDashboard.Services;

namespace PMDashboard.Data;

public class AppDbContext : DbContext
{
    /// <summary>
    /// When true, connect to the MariaDB cloud database instead of local SQLite.
    /// Only CloudSyncService should set this; all other callers use local SQLite.
    /// </summary>
    public bool ForceCloud { get; init; }

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
            var path = DatabaseConfig.ActiveSqlitePath;
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            options.UseSqlite($"Data Source={path}");
        }
    }

    // ── Auto-stamp + event ────────────────────────────────────────────────────

    public override int SaveChanges()
    {
        StampLastModified();
        var result = base.SaveChanges();
        // Notify CloudSyncService after a local (non-cloud) save
        if (!ForceCloud && DatabaseConfig.IsCloud)
            LocalSaved?.Invoke();
        return result;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampLastModified();
        var task = base.SaveChangesAsync(cancellationToken);
        if (!ForceCloud && DatabaseConfig.IsCloud)
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
            if (prop != null) prop.CurrentValue = now;
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
