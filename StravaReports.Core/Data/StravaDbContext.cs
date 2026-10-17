using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StravaReports.Core.Data;

public class StravaDbContext(DbContextOptions<StravaDbContext> options) : DbContext(options)
{
    public DbSet<Athlete> Athletes => Set<Athlete>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ActivityLap> ActivityLaps => Set<ActivityLap>();
    public DbSet<ActivityBestEffort> ActivityBestEfforts => Set<ActivityBestEffort>();
    public DbSet<ActivityStream> ActivityStreams => Set<ActivityStream>();
    public DbSet<Gear> Gear => Set<Gear>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<LogEntry> LogEntries => Set<LogEntry>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Athlete>().HasIndex(x => x.StravaId).IsUnique();

        b.Entity<Activity>(e =>
        {
            e.HasIndex(x => x.StravaId).IsUnique();
            e.HasIndex(x => new { x.SportType, x.StartDateLocal });
            e.HasIndex(x => x.GearId);
            e.HasIndex(x => x.StartDateUtc);
            e.Property(x => x.SportType).HasMaxLength(64);
            e.HasMany(x => x.Laps).WithOne().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.BestEfforts).WithOne().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Streams).WithOne().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityLap>().HasIndex(x => new { x.ActivityId, x.LapIndex }).IsUnique();
        b.Entity<ActivityBestEffort>().HasIndex(x => new { x.Name, x.MovingTimeS });
        b.Entity<ActivityStream>().HasIndex(x => new { x.ActivityId, x.StreamType }).IsUnique();
        b.Entity<Gear>().HasIndex(x => x.StravaId).IsUnique();

        b.Entity<SyncJob>(e =>
        {
            e.HasIndex(x => new { x.ActivityStravaId, x.JobType }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
        });

        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<LogEntry>().HasIndex(x => x.TimestampUtc);
        b.Entity<WebhookEvent>(e =>
        {
            e.HasIndex(x => new { x.Status, x.Id });
            e.Property(x => x.ObjectType).HasMaxLength(16);
            e.Property(x => x.AspectType).HasMaxLength(16);
            e.Property(x => x.UpdatesJson).HasMaxLength(2048);
        });
    }
}

/// <summary>Used only by "dotnet ef" to create migrations.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<StravaDbContext>
{
    public StravaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StravaDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new StravaDbContext(options);
    }
}
