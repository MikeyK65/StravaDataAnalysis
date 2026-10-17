using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StravaReports.Core.Archive;

// A separate SQLite database holding activities imported from Strava's "Download your data" export.
// It is never touched by sync or "Delete all data", and the API database is never touched by imports.

public class ArchivedActivity
{
    public int Id { get; set; }
    /// <summary>Strava's activity ID: the natural key that makes repeat imports idempotent.</summary>
    public long ActivityId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The "Activity Type" column as exported, e.g. "Virtual Ride".</summary>
    public string ActivityType { get; set; } = "";
    /// <summary>Activity type normalised to the API's SportType spelling, e.g. "VirtualRide".</summary>
    public string SportType { get; set; } = "";
    /// <summary>The export's "Activity Date", which Strava writes in UTC.</summary>
    public DateTime? StartDateUtc { get; set; }
    public string? Description { get; set; }
    public double? DistanceM { get; set; }
    public int? MovingTimeS { get; set; }
    public int? ElapsedTimeS { get; set; }
    public double? ElevationGainM { get; set; }
    public double? MaxSpeed { get; set; }
    public double? AvgSpeed { get; set; }
    public double? AvgHr { get; set; }
    public double? MaxHr { get; set; }
    public double? AvgWatts { get; set; }
    public double? WeightedAvgWatts { get; set; }
    public double? AvgCadence { get; set; }
    public double? Calories { get; set; }
    public double? RelativeEffort { get; set; }
    public bool? IsCommute { get; set; }
    public string? GearName { get; set; }
    public string? Filename { get; set; }
    /// <summary>Every column of the source row as a JSON object, so no exported field is ever lost.</summary>
    public string RawJson { get; set; } = "";
    /// <summary>SHA-256 of <see cref="RawJson"/>; tells "unchanged" from "updated" on a repeat import.</summary>
    public string RowHash { get; set; } = "";
    public int FirstBatchId { get; set; }
    public int LastBatchId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class ImportBatch
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int RowsRead { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    /// <summary>Rows repeated within this one file (the last copy wins).</summary>
    public int DuplicatesInFile { get; set; }
    public string Status { get; set; } = "";
    public string? Message { get; set; }
}

public class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
    public DbSet<ArchivedActivity> Activities => Set<ArchivedActivity>();
    public DbSet<ImportBatch> Batches => Set<ImportBatch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ArchivedActivity>(e =>
        {
            e.ToTable("ArchivedActivities");
            e.HasIndex(x => x.ActivityId).IsUnique();
            e.HasIndex(x => x.StartDateUtc);
            e.HasIndex(x => x.SportType);
            e.Property(x => x.RowHash).HasMaxLength(64);
        });
        b.Entity<ImportBatch>(e => e.ToTable("ImportBatches"));
    }
}

public class ArchiveDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ArchiveDbContext>
{
    public ArchiveDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ArchiveDbContext>().UseSqlite("Data Source=design-archive.db").Options);
}
