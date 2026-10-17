using System.Text.Json;
using System.Text.Json.Serialization;

namespace StravaReports.Core.Strava;

// DTOs for the subset of the Strava v3 API this app reads. Unknown fields are ignored but the raw
// JSON for each activity is retained in the database, so new metrics never need a re-download.

public static class StravaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public class StravaTokenResponse
{
    public string TokenType { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public long ExpiresAt { get; set; }
    public StravaAthlete? Athlete { get; set; }
}

public class StravaAthlete
{
    public long Id { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Profile { get; set; }
    public string? MeasurementPreference { get; set; }
    public List<StravaGearSummary>? Bikes { get; set; }
    public List<StravaGearSummary>? Shoes { get; set; }
}

public class StravaGearSummary
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool Primary { get; set; }
    public bool Retired { get; set; }
    public double Distance { get; set; }
    public string? BrandName { get; set; }
    public string? ModelName { get; set; }
}

public class StravaMap
{
    public string? SummaryPolyline { get; set; }
}

public class StravaActivity
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? SportType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime StartDateLocal { get; set; }
    public string? Timezone { get; set; }
    public double Distance { get; set; }
    public int MovingTime { get; set; }
    public int ElapsedTime { get; set; }
    public double TotalElevationGain { get; set; }
    public double AverageSpeed { get; set; }
    public double MaxSpeed { get; set; }
    public bool HasHeartrate { get; set; }
    public double? AverageHeartrate { get; set; }
    public double? MaxHeartrate { get; set; }
    public double? AverageWatts { get; set; }
    public double? WeightedAverageWatts { get; set; }
    public double? Kilojoules { get; set; }
    public bool DeviceWatts { get; set; }
    public double? AverageCadence { get; set; }
    public double? SufferScore { get; set; }
    public string? GearId { get; set; }
    public bool Trainer { get; set; }
    public bool Commute { get; set; }
    public bool Manual { get; set; }
    public bool Private { get; set; }
    public StravaMap? Map { get; set; }

    // Present on the detailed representation only.
    public List<StravaLap>? Laps { get; set; }
    public List<StravaBestEffort>? BestEfforts { get; set; }
    public StravaGearSummary? Gear { get; set; }
}

public class StravaLap
{
    public int LapIndex { get; set; }
    public string? Name { get; set; }
    public double Distance { get; set; }
    public int MovingTime { get; set; }
    public int ElapsedTime { get; set; }
    public double AverageSpeed { get; set; }
    public double? AverageHeartrate { get; set; }
    public double? AverageWatts { get; set; }
    public double? AverageCadence { get; set; }
    public double TotalElevationGain { get; set; }
}

public class StravaBestEffort
{
    public string Name { get; set; } = "";
    public double Distance { get; set; }
    public int MovingTime { get; set; }
    public int ElapsedTime { get; set; }
    public DateTime StartDateLocal { get; set; }
    public int? PrRank { get; set; }
}

/// <summary>An activity as parsed, plus the exact JSON Strava returned for it.</summary>
public record StravaActivityWithJson(StravaActivity Activity, string RawJson);

/// <summary>Streams keyed by type, each holding the raw JSON array of data points.</summary>
public record StravaStreamSet(IReadOnlyDictionary<string, (int Count, string DataJson)> Streams);
