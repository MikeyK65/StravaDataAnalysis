namespace StravaReports.Core.Data;

// Storage rules: distances in metres, times in seconds, speeds in m/s. Convert for display only.
// All DateTime values ending in "Utc" are UTC; StartDateLocal is the athlete's wall-clock time.

public class Athlete
{
    public int Id { get; set; }
    public long StravaId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? ProfileImageUrl { get; set; }
    /// <summary>Strava's measurement_preference: "meters" or "feet".</summary>
    public string? MeasurementPreference { get; set; }
    public string? RawJson { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string DisplayName => $"{FirstName} {LastName}".Trim();
}

public class Activity
{
    public int Id { get; set; }
    public long StravaId { get; set; }
    public string Name { get; set; } = "";
    public string SportType { get; set; } = "";
    public DateTime StartDateUtc { get; set; }
    public DateTime StartDateLocal { get; set; }
    public string? Timezone { get; set; }
    public double DistanceM { get; set; }
    public int MovingTimeS { get; set; }
    public int ElapsedTimeS { get; set; }
    public double ElevationGainM { get; set; }
    public double AvgSpeed { get; set; }
    public double MaxSpeed { get; set; }
    public double? AvgHr { get; set; }
    public double? MaxHr { get; set; }
    public double? AvgWatts { get; set; }
    public double? WeightedAvgWatts { get; set; }
    public double? Kilojoules { get; set; }
    public double? AvgCadence { get; set; }
    public bool DeviceWatts { get; set; }
    public double? SufferScore { get; set; }
    public string? GearId { get; set; }
    public bool IsTrainer { get; set; }
    public bool IsCommute { get; set; }
    public bool IsManual { get; set; }
    public bool IsPrivate { get; set; }
    public string? SummaryPolyline { get; set; }
    public string RawJson { get; set; } = "";
    public DateTime SummaryFetchedAtUtc { get; set; }
    public DateTime? DetailFetchedAtUtc { get; set; }
    public DateTime? StreamsFetchedAtUtc { get; set; }

    public List<ActivityLap> Laps { get; set; } = [];
    public List<ActivityBestEffort> BestEfforts { get; set; } = [];
    public List<ActivityStream> Streams { get; set; } = [];
}

public class ActivityLap
{
    public int Id { get; set; }
    public int ActivityId { get; set; }
    public int LapIndex { get; set; }
    public string? Name { get; set; }
    public double DistanceM { get; set; }
    public int MovingTimeS { get; set; }
    public int ElapsedTimeS { get; set; }
    public double AvgSpeed { get; set; }
    public double? AvgHr { get; set; }
    public double? AvgWatts { get; set; }
    public double? AvgCadence { get; set; }
    public double ElevationGainM { get; set; }
}

public class ActivityBestEffort
{
    public int Id { get; set; }
    public int ActivityId { get; set; }
    public string Name { get; set; } = "";
    public double DistanceM { get; set; }
    public int MovingTimeS { get; set; }
    public int ElapsedTimeS { get; set; }
    public DateTime StartDateLocal { get; set; }
    public int? PrRank { get; set; }
}

public class ActivityStream
{
    public int Id { get; set; }
    public int ActivityId { get; set; }
    /// <summary>Strava stream key: time, distance, heartrate, watts, cadence, altitude, velocity_smooth, ...</summary>
    public string StreamType { get; set; } = "";
    public int PointCount { get; set; }
    /// <summary>GZip-compressed JSON array of the stream data.</summary>
    public byte[] Data { get; set; } = [];
}

public class Gear
{
    public int Id { get; set; }
    public string StravaId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"bike" or "shoe".</summary>
    public string GearType { get; set; } = "";
    public string? BrandName { get; set; }
    public string? ModelName { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsRetired { get; set; }
    /// <summary>Distance as reported by Strava (includes activities older than local history).</summary>
    public double DistanceM { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class SyncState
{
    public int Id { get; set; }
    /// <summary>Newest activity start (epoch seconds, UTC) seen by the incremental pass.</summary>
    public long? NewestActivityEpoch { get; set; }
    /// <summary>Backfill pages backwards from here (epoch seconds, UTC); null means start at "now".</summary>
    public long? BackfillCursorEpoch { get; set; }
    public bool BackfillComplete { get; set; }
    public DateTime? LastRunStartedUtc { get; set; }
    public DateTime? LastRunFinishedUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime? ResumeAfterUtc { get; set; }
    public DateTime? LastReconcileUtc { get; set; }

    // Last observed rate-limit headers.
    public int? ShortLimit { get; set; }
    public int? ShortUsage { get; set; }
    public int? DailyLimit { get; set; }
    public int? DailyUsage { get; set; }
    public DateTime? RateLimitObservedUtc { get; set; }
}

public enum SyncJobType { Detail = 0, Streams = 1 }

public enum SyncJobStatus { Pending = 0, Done = 1, Failed = 2 }

public class SyncJob
{
    public int Id { get; set; }
    public long ActivityStravaId { get; set; }
    public SyncJobType JobType { get; set; }
    public SyncJobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? LastError { get; set; }
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

public class LogEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = "";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
}

public enum WebhookEventStatus { Pending = 0, Done = 1, Ignored = 2, Failed = 3 }

/// <summary>
/// A Strava push notification, stored on receipt and processed by the next sync run. Events are only
/// hints: every change is confirmed against the API before anything local is changed or deleted.
/// </summary>
public class WebhookEvent
{
    public long Id { get; set; }
    /// <summary>"activity" or "athlete".</summary>
    public string ObjectType { get; set; } = "";
    public long ObjectId { get; set; }
    /// <summary>"create", "update" or "delete".</summary>
    public string AspectType { get; set; } = "";
    public long OwnerId { get; set; }
    public long SubscriptionId { get; set; }
    public long EventTime { get; set; }
    public string? UpdatesJson { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public WebhookEventStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Note { get; set; }
}
