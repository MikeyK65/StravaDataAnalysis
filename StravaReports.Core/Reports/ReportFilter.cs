using StravaReports.Core.Data;

namespace StravaReports.Core.Reports;

public enum SportGroup { All, Run, Ride, Other }

public static class SportGroups
{
    public static readonly string[] Run = ["Run", "TrailRun", "VirtualRun"];
    public static readonly string[] Ride = ["Ride", "GravelRide", "MountainBikeRide", "EBikeRide", "EMountainBikeRide", "VirtualRide", "Velomobile", "Handcycle"];
    private static readonly string[] RunAndRide = [.. Run, .. Ride];

    public static SportGroup Classify(string sportType) =>
        Run.Contains(sportType) ? SportGroup.Run : Ride.Contains(sportType) ? SportGroup.Ride : SportGroup.Other;

    public static IQueryable<Activity> WhereGroup(this IQueryable<Activity> q, SportGroup group) => group switch
    {
        SportGroup.Run => q.Where(a => Run.Contains(a.SportType)),
        SportGroup.Ride => q.Where(a => Ride.Contains(a.SportType)),
        SportGroup.Other => q.Where(a => !RunAndRide.Contains(a.SportType)),
        _ => q,
    };
}

/// <summary>The shared filter every report takes. Dates are in the athlete's local time.</summary>
public record ReportFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public SportGroup Group { get; init; } = SportGroup.All;
    /// <summary>Optional exact Strava SportType, narrower than <see cref="Group"/>.</summary>
    public string? SportType { get; init; }
    public string? GearId { get; init; }
    public bool IncludeTrainer { get; init; } = true;
    public bool IncludeCommute { get; init; } = true;
    /// <summary>Also count activities that exist only in the export archive (see ArchiveRowSource).</summary>
    public bool IncludeArchive { get; init; }

    public ReportFilter WithoutDates() => this with { From = null, To = null };
    public ReportFilter WithGroup(SportGroup g) => this with { Group = g, SportType = SportType is not null && SportGroups.Classify(SportType) == g ? SportType : null };
}

public static class ActivityQueryExtensions
{
    public static IQueryable<Activity> ApplyFilter(this IQueryable<Activity> q, ReportFilter f)
    {
        if (f.From is { } from)
        {
            var start = from.ToDateTime(TimeOnly.MinValue);
            q = q.Where(a => a.StartDateLocal >= start);
        }
        if (f.To is { } to)
        {
            var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
            q = q.Where(a => a.StartDateLocal < end);
        }
        q = q.WhereGroup(f.Group);
        if (!string.IsNullOrEmpty(f.SportType)) q = q.Where(a => a.SportType == f.SportType);
        if (!string.IsNullOrEmpty(f.GearId)) q = q.Where(a => a.GearId == f.GearId);
        if (!f.IncludeTrainer) q = q.Where(a => !a.IsTrainer);
        if (!f.IncludeCommute) q = q.Where(a => !a.IsCommute);
        return q;
    }

    /// <summary>Slim projection used by every report: never loads RawJson.</summary>
    public static IQueryable<ActivityRow> SelectRows(this IQueryable<Activity> q) => q.Select(a => new ActivityRow
    {
        StravaId = a.StravaId,
        Name = a.Name,
        SportType = a.SportType,
        StartDateLocal = a.StartDateLocal,
        DistanceM = a.DistanceM,
        MovingTimeS = a.MovingTimeS,
        ElapsedTimeS = a.ElapsedTimeS,
        ElevationGainM = a.ElevationGainM,
        AvgSpeed = a.AvgSpeed,
        AvgHr = a.AvgHr,
        MaxHr = a.MaxHr,
        SufferScore = a.SufferScore,
        DeviceWatts = a.DeviceWatts,
        AvgWatts = a.AvgWatts,
        WeightedAvgWatts = a.WeightedAvgWatts,
        Kilojoules = a.Kilojoules,
        AvgCadence = a.AvgCadence,
        GearId = a.GearId,
        IsTrainer = a.IsTrainer,
        IsCommute = a.IsCommute,
    });
}

public record ActivityRow
{
    public long StravaId { get; init; }
    public string Name { get; init; } = "";
    public string SportType { get; init; } = "";
    public DateTime StartDateLocal { get; init; }
    public double DistanceM { get; init; }
    public int MovingTimeS { get; init; }
    public int ElapsedTimeS { get; init; }
    public double ElevationGainM { get; init; }
    public double AvgSpeed { get; init; }
    public double? AvgHr { get; init; }
    public double? MaxHr { get; init; }
    public double? SufferScore { get; init; }
    public bool DeviceWatts { get; init; }
    public double? AvgWatts { get; init; }
    public double? WeightedAvgWatts { get; init; }
    public double? Kilojoules { get; init; }
    public double? AvgCadence { get; init; }
    public string? GearId { get; init; }
    public bool IsTrainer { get; init; }
    public bool IsCommute { get; init; }
    /// <summary>From the export archive rather than the API; has no detail page or streams.</summary>
    public bool FromArchive { get; init; }

    public DateOnly Day => DateOnly.FromDateTime(StartDateLocal);
}
