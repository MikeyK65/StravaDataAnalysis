using StravaReports.Core.Data;
using StravaReports.Core.Strava;

namespace StravaReports.Core.Sync;

public static class ActivityMapper
{
    /// <summary>Copies summary fields onto the entity. Detail-only fields are left untouched.</summary>
    public static void ApplySummary(Activity e, StravaActivity a, string rawJson, DateTime nowUtc)
    {
        e.StravaId = a.Id;
        e.Name = a.Name ?? "";
        e.SportType = a.SportType ?? a.Type ?? "Unknown";
        e.StartDateUtc = DateTime.SpecifyKind(a.StartDate.ToUniversalTime(), DateTimeKind.Utc);
        // start_date_local is serialised with a 'Z' but is wall-clock time: drop the kind.
        e.StartDateLocal = DateTime.SpecifyKind(a.StartDateLocal, DateTimeKind.Unspecified);
        e.Timezone = a.Timezone;
        e.DistanceM = a.Distance;
        e.MovingTimeS = a.MovingTime;
        e.ElapsedTimeS = a.ElapsedTime;
        e.ElevationGainM = a.TotalElevationGain;
        e.AvgSpeed = a.AverageSpeed;
        e.MaxSpeed = a.MaxSpeed;
        e.AvgHr = a.HasHeartrate ? a.AverageHeartrate : null;
        e.MaxHr = a.HasHeartrate ? a.MaxHeartrate : null;
        e.AvgWatts = a.AverageWatts;
        e.WeightedAvgWatts = a.WeightedAverageWatts;
        e.Kilojoules = a.Kilojoules;
        e.DeviceWatts = a.DeviceWatts;
        e.AvgCadence = a.AverageCadence;
        e.SufferScore = a.SufferScore;
        e.GearId = a.GearId;
        e.IsTrainer = a.Trainer;
        e.IsCommute = a.Commute;
        e.IsManual = a.Manual;
        e.IsPrivate = a.Private;
        e.SummaryPolyline = a.Map?.SummaryPolyline ?? e.SummaryPolyline;
        // Never overwrite richer detail JSON with a summary.
        if (e.DetailFetchedAtUtc is null) e.RawJson = rawJson;
        e.SummaryFetchedAtUtc = nowUtc;
    }

    public static void ApplyDetail(Activity e, StravaActivity a, string rawJson, DateTime nowUtc)
    {
        ApplySummary(e, a, rawJson, nowUtc);
        e.RawJson = rawJson;
        e.DetailFetchedAtUtc = nowUtc;

        e.Laps.Clear();
        foreach (var l in a.Laps ?? [])
        {
            e.Laps.Add(new ActivityLap
            {
                LapIndex = l.LapIndex,
                Name = l.Name,
                DistanceM = l.Distance,
                MovingTimeS = l.MovingTime,
                ElapsedTimeS = l.ElapsedTime,
                AvgSpeed = l.AverageSpeed,
                AvgHr = l.AverageHeartrate,
                AvgWatts = l.AverageWatts,
                AvgCadence = l.AverageCadence,
                ElevationGainM = l.TotalElevationGain,
            });
        }
        // Strava lap_index can repeat for some devices; keep the unique index stable.
        for (var i = 0; i < e.Laps.Count; i++) e.Laps[i].LapIndex = i + 1;

        e.BestEfforts.Clear();
        foreach (var b in a.BestEfforts ?? [])
        {
            e.BestEfforts.Add(new ActivityBestEffort
            {
                Name = b.Name,
                DistanceM = b.Distance,
                MovingTimeS = b.MovingTime,
                ElapsedTimeS = b.ElapsedTime,
                StartDateLocal = DateTime.SpecifyKind(b.StartDateLocal, DateTimeKind.Unspecified),
                PrRank = b.PrRank,
            });
        }
    }

    public static void ApplyGear(Gear g, StravaGearSummary s, string gearType, DateTime nowUtc)
    {
        g.StravaId = s.Id;
        g.Name = string.IsNullOrWhiteSpace(s.Name) ? g.Name : s.Name!;
        g.GearType = gearType;
        g.BrandName = s.BrandName ?? g.BrandName;
        g.ModelName = s.ModelName ?? g.ModelName;
        g.IsPrimary = s.Primary;
        g.IsRetired = s.Retired;
        g.DistanceM = s.Distance;
        g.UpdatedAtUtc = nowUtc;
    }

    /// <summary>Strava gear ids start with 'b' for bikes and 'g' for shoes.</summary>
    public static string GearTypeFromId(string id) => id.StartsWith('b') ? "bike" : "shoe";
}
