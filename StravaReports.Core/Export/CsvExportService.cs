using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Reports;
using StravaReports.Core.Settings;

namespace StravaReports.Core.Export;

/// <summary>RFC 4180 CSV writing with invariant formatting, so files open the same in any locale.</summary>
public sealed class CsvBuilder
{
    private readonly StringBuilder _sb = new();

    public CsvBuilder Row(params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) _sb.Append(',');
            _sb.Append(Escape(Format(values[i])));
        }
        _sb.Append("\r\n");
        return this;
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double d => double.IsFinite(d) ? Math.Round(d, 3).ToString(CultureInfo.InvariantCulture) : "",
        float f => Math.Round(f, 3).ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public static string Escape(string s)
    {
        // Leading = + - @ are neutralised so a spreadsheet never evaluates text from Strava as a formula.
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    /// <summary>UTF-8 with BOM so Excel detects the encoding.</summary>
    public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_sb.ToString())];

    public override string ToString() => _sb.ToString();
}

public record ExportFile(string FileName, byte[] Content, int Rows)
{
    public const string ContentType = "text/csv";
}

public enum ExportKind { Activities, Laps, BestEfforts, Gear, WeeklyTotals, MonthlyTotals, Records, HrZones, TrainingLoad }

/// <summary>CSV exports of raw data and report tables. Values are in storage units (metres, seconds, m/s).</summary>
public class CsvExportService(
    IDbContextFactory<StravaDbContext> dbFactory,
    VolumeTrendsReport volume, RecordsReport records, HrZonesReport zones, TrainingLoadReport load, TimeProvider time)
{
    public static readonly (ExportKind Kind, string Title, string Description, bool UsesFilter)[] Catalogue =
    [
        (ExportKind.Activities, "Activities", "One row per activity with every mapped field.", true),
        (ExportKind.Laps, "Laps", "Every lap of the filtered activities.", true),
        (ExportKind.BestEfforts, "Best efforts", "Every best effort Strava recorded on the filtered runs.", true),
        (ExportKind.Gear, "Gear", "Bikes and shoes with Strava and local distance.", false),
        (ExportKind.WeeklyTotals, "Weekly totals", "Volume trends grouped by week.", true),
        (ExportKind.MonthlyTotals, "Monthly totals", "Volume trends grouped by month.", true),
        (ExportKind.Records, "Running records", "Personal bests and the PB timeline.", true),
        (ExportKind.HrZones, "Heart-rate zones", "Seconds in each zone by week.", true),
        (ExportKind.TrainingLoad, "Training load", "Daily load, fitness, fatigue and form.", true),
    ];

    public async Task<ExportFile> ExportAsync(ExportKind kind, ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var csv = new CsvBuilder();
        var rows = kind switch
        {
            ExportKind.Activities => await ActivitiesAsync(csv, filter, ct),
            ExportKind.Laps => await LapsAsync(csv, filter, ct),
            ExportKind.BestEfforts => await BestEffortsAsync(csv, filter, ct),
            ExportKind.Gear => await GearAsync(csv, ct),
            ExportKind.WeeklyTotals => await TotalsAsync(csv, filter, Grouping.Week, s, ct),
            ExportKind.MonthlyTotals => await TotalsAsync(csv, filter, Grouping.Month, s, ct),
            ExportKind.Records => await RecordsAsync(csv, filter, s, ct),
            ExportKind.HrZones => await ZonesAsync(csv, filter, s, ct),
            ExportKind.TrainingLoad => await LoadAsync(csv, filter, s, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var name = kind.ToString().ToLowerInvariant();
        return new ExportFile($"strava-{name}-{stamp}.csv", csv.ToBytes(), rows);
    }

    private async Task<int> ActivitiesAsync(CsvBuilder csv, ReportFilter filter, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        csv.Row("strava_id", "name", "sport_type", "start_date_utc", "start_date_local", "timezone", "distance_m", "moving_time_s", "elapsed_time_s",
            "elevation_gain_m", "average_speed_mps", "max_speed_mps", "average_heartrate", "max_heartrate", "average_watts", "weighted_average_watts",
            "kilojoules", "average_cadence", "device_watts", "relative_effort", "gear_id", "trainer", "commute", "manual", "private", "url");
        var n = 0;
        // Streamed rather than loaded at once; RawJson is never read.
        await foreach (var a in db.Activities.AsNoTracking().ApplyFilter(filter).OrderBy(a => a.StartDateLocal)
                           .Select(a => new { a.StravaId, a.Name, a.SportType, a.StartDateUtc, a.StartDateLocal, a.Timezone, a.DistanceM, a.MovingTimeS, a.ElapsedTimeS,
                               a.ElevationGainM, a.AvgSpeed, a.MaxSpeed, a.AvgHr, a.MaxHr, a.AvgWatts, a.WeightedAvgWatts, a.Kilojoules, a.AvgCadence, a.DeviceWatts,
                               a.SufferScore, a.GearId, a.IsTrainer, a.IsCommute, a.IsManual, a.IsPrivate })
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            csv.Row(a.StravaId, a.Name, a.SportType, a.StartDateUtc, a.StartDateLocal, a.Timezone, a.DistanceM, a.MovingTimeS, a.ElapsedTimeS,
                a.ElevationGainM, a.AvgSpeed, a.MaxSpeed, a.AvgHr, a.MaxHr, a.AvgWatts, a.WeightedAvgWatts, a.Kilojoules, a.AvgCadence, a.DeviceWatts,
                a.SufferScore, a.GearId, a.IsTrainer, a.IsCommute, a.IsManual, a.IsPrivate, $"https://www.strava.com/activities/{a.StravaId}");
            n++;
        }
        return n;
    }

    private async Task<int> LapsAsync(CsvBuilder csv, ReportFilter filter, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var acts = db.Activities.AsNoTracking().ApplyFilter(filter);
        var laps = await (from l in db.ActivityLaps.AsNoTracking()
                          join a in acts on l.ActivityId equals a.Id
                          orderby a.StartDateLocal, l.LapIndex
                          select new { a.StravaId, ActivityName = a.Name, a.SportType, a.StartDateLocal, l.LapIndex, l.Name, l.DistanceM, l.MovingTimeS, l.ElapsedTimeS,
                              l.AvgSpeed, l.AvgHr, l.AvgWatts, l.AvgCadence, l.ElevationGainM }).ToListAsync(ct);
        csv.Row("strava_id", "activity_name", "sport_type", "start_date_local", "lap_index", "lap_name", "distance_m", "moving_time_s", "elapsed_time_s",
            "average_speed_mps", "average_heartrate", "average_watts", "average_cadence", "elevation_gain_m");
        foreach (var l in laps)
            csv.Row(l.StravaId, l.ActivityName, l.SportType, l.StartDateLocal, l.LapIndex, l.Name, l.DistanceM, l.MovingTimeS, l.ElapsedTimeS,
                l.AvgSpeed, l.AvgHr, l.AvgWatts, l.AvgCadence, l.ElevationGainM);
        return laps.Count;
    }

    private async Task<int> BestEffortsAsync(CsvBuilder csv, ReportFilter filter, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var acts = db.Activities.AsNoTracking().ApplyFilter(filter);
        var efforts = await (from e in db.ActivityBestEfforts.AsNoTracking()
                             join a in acts on e.ActivityId equals a.Id
                             orderby a.StartDateLocal, e.DistanceM
                             select new { a.StravaId, ActivityName = a.Name, a.StartDateLocal, e.Name, e.DistanceM, e.ElapsedTimeS, e.MovingTimeS, e.PrRank }).ToListAsync(ct);
        csv.Row("strava_id", "activity_name", "start_date_local", "effort", "distance_m", "elapsed_time_s", "moving_time_s", "pr_rank_at_the_time");
        foreach (var e in efforts) csv.Row(e.StravaId, e.ActivityName, e.StartDateLocal, e.Name, e.DistanceM, e.ElapsedTimeS, e.MovingTimeS, e.PrRank);
        return efforts.Count;
    }

    private async Task<int> GearAsync(CsvBuilder csv, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var gear = await db.Gear.AsNoTracking().OrderBy(g => g.GearType).ThenBy(g => g.Name).ToListAsync(ct);
        var local = await db.Activities.Where(a => a.GearId != null).GroupBy(a => a.GearId!)
            .Select(g => new { g.Key, Distance = g.Sum(a => a.DistanceM), Count = g.Count() }).ToDictionaryAsync(x => x.Key, ct);
        csv.Row("gear_id", "name", "type", "brand", "model", "primary", "retired", "strava_distance_m", "local_distance_m", "local_activities");
        foreach (var g in gear)
        {
            var l = local.GetValueOrDefault(g.StravaId);
            csv.Row(g.StravaId, g.Name, g.GearType, g.BrandName, g.ModelName, g.IsPrimary, g.IsRetired, g.DistanceM, l?.Distance ?? 0, l?.Count ?? 0);
        }
        return gear.Count;
    }

    private async Task<int> TotalsAsync(CsvBuilder csv, ReportFilter filter, Grouping grouping, AppSettingsModel s, CancellationToken ct)
    {
        var r = await volume.RunAsync(filter, grouping, s, ct);
        csv.Row(grouping == Grouping.Week ? "week_start" : "month_start", "activities", "distance_m", "moving_time_s", "elevation_gain_m");
        foreach (var p in r.Periods) csv.Row(p.PeriodStart, p.Totals.Count, p.Totals.DistanceM, p.Totals.MovingTimeS, p.Totals.ElevationGainM);
        return r.Periods.Count;
    }

    private async Task<int> RecordsAsync(CsvBuilder csv, ReportFilter filter, AppSettingsModel s, CancellationToken ct)
    {
        var r = await records.RunAsync(filter, s, ct);
        csv.Row("section", "effort", "distance_m", "elapsed_time_s", "date_local", "strava_id", "activity_name", "efforts_counted", "best_last_12_months_s");
        foreach (var b in r.Bests) csv.Row("personal best", b.Name, b.DistanceM, b.ElapsedTimeS, b.StartDateLocal, b.StravaId, b.ActivityName, b.Count, b.Last12MonthsBestS);
        foreach (var t in r.Timeline) csv.Row("pb timeline", t.Name, t.DistanceM, t.ElapsedTimeS, t.StartDateLocal, t.StravaId, null, null, null);
        return r.Bests.Count + r.Timeline.Count;
    }

    private async Task<int> ZonesAsync(CsvBuilder csv, ReportFilter filter, AppSettingsModel s, CancellationToken ct)
    {
        var r = await zones.RunAsync(filter, s, ct);
        csv.Row("week_start", "z1_s", "z2_s", "z3_s", "z4_s", "z5_s");
        foreach (var w in r.Weeks) csv.Row([w.Week, .. w.Seconds.Select(x => (object?)Math.Round(x))]);
        return r.Weeks.Count;
    }

    private async Task<int> LoadAsync(CsvBuilder csv, ReportFilter filter, AppSettingsModel s, CancellationToken ct)
    {
        var r = await load.RunAsync(filter, s, ct);
        csv.Row("date", "load", "fitness_ctl", "fatigue_atl", "form_tsb");
        foreach (var d in r.Days) csv.Row(d.Day, d.Load, d.Fitness, d.Fatigue, d.Form);
        return r.Days.Count;
    }
}
