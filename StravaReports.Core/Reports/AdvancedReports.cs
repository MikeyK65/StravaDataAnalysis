using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Analysis;
using StravaReports.Core.Data;
using StravaReports.Core.Settings;

namespace StravaReports.Core.Reports;

public record PeriodValue(DateOnly Period, double Value);

/// <summary>Helpers shared by the zone, load and efficiency reports.</summary>
public static class Metrics
{
    public const int DefaultRestingHr = 60;

    /// <summary>
    /// Zone seconds for one activity. From the HR stream when there is one (scaled to moving time),
    /// otherwise the whole moving time goes in the zone of the average HR and is flagged as estimated.
    /// </summary>
    public static (double[] Seconds, bool Estimated)? ZonesFor(ActivityRow a, StreamSummary? summary, HrZoneSet zones)
    {
        if (summary is { HrHistogram.Count: > 0 })
            return (StreamMath.ZoneSeconds(summary.HrHistogram, zones, a.MovingTimeS), false);
        if (a.AvgHr is { } hr && hr > 0 && a.MovingTimeS > 0)
        {
            var s = new double[5];
            s[zones.ZoneOf(hr)] = a.MovingTimeS;
            return (s, true);
        }
        return null;
    }

    /// <summary>
    /// One load number per activity, in roughly TSS-like units: power TSS when FTP and power are known,
    /// then HR TRIMP, then Strava's Relative Effort, then a duration estimate (30 per hour).
    /// </summary>
    public static ActivityLoad LoadFor(ActivityRow a, StreamSummary? summary, AppSettingsModel s, int? maxHr)
    {
        var np = summary?.NormalizedPower ?? (a.DeviceWatts ? a.WeightedAvgWatts : null);
        if (StreamMath.Tss(a.MovingTimeS, np, s.Ftp) is { } tss) return new ActivityLoad(a, tss, LoadMethod.Power);
        if (maxHr is { } m && StreamMath.Trimp(a.MovingTimeS, a.AvgHr, m, s.RestingHr ?? DefaultRestingHr) is { } trimp)
            return new ActivityLoad(a, trimp, LoadMethod.HeartRate);
        if (a.SufferScore is { } re && re > 0) return new ActivityLoad(a, re, LoadMethod.RelativeEffort);
        return new ActivityLoad(a, a.MovingTimeS / 60.0 * 0.5, LoadMethod.Duration);
    }

    public static DateOnly ClampTo(ReportFilter f, DateOnly today) => f.To is { } t && t < today ? t : today;
}

public abstract class AnalysisReportBase(IDbContextFactory<StravaDbContext> dbFactory, StreamAnalysisService streams) : ReportBase(dbFactory)
{
    protected StreamAnalysisService Streams => streams;

    protected Task<Dictionary<long, StreamSummary>> SummariesAsync(IEnumerable<ActivityRow> rows, CancellationToken ct) =>
        streams.SummariesAsync(rows.Select(r => r.StravaId).ToList(), ct);

    /// <summary>Highest max HR across all activities, used when Settings has no max HR.</summary>
    protected async Task<double?> ObservedMaxHrAsync(CancellationToken ct)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.Activities.MaxAsync(a => a.MaxHr, ct);
    }
}

// ---------------------------------------------------------------- Running records

public record EffortBest(string Name, double DistanceM, int ElapsedTimeS, DateTime StartDateLocal, long StravaId, string ActivityName, int Count, int? Last12MonthsBestS);

public record EffortPoint(string Name, double DistanceM, DateTime StartDateLocal, int ElapsedTimeS, long StravaId);

public enum HighlightKind { Distance, Elevation, Duration, Speed }

public record Highlight(string Label, HighlightKind Kind, double Value, DateTime Date, long? StravaId, string? ActivityName, bool FromArchive = false);

/// <summary>The fastest whole run at about a standard distance; works for archive runs, which have no best efforts.</summary>
public record DistanceRecord(string Label, double TargetM, ActivityRow Run);

public record RecordsResult(IReadOnlyList<EffortBest> Bests, IReadOnlyList<EffortPoint> Timeline, IReadOnlyList<Highlight> Highlights, int RunsWithoutDetail,
    IReadOnlyList<DistanceRecord> WholeRuns);

public class RecordsReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "records";
    public override string Title => "Running records";
    public override string Description => "Personal bests at standard distances, PB timeline and longest/biggest activities.";
    public override string Section => "Running";
    public override int Order => 32;

    public async Task<RecordsResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var runFilter = filter.WithGroup(SportGroup.Run);
        await using var db = await CreateDbAsync(ct);
        var runs = db.Activities.AsNoTracking().ApplyFilter(runFilter);
        // Strava ranks best efforts on elapsed time, so records use it too.
        var efforts = await (from e in db.ActivityBestEfforts.AsNoTracking()
                             join a in runs on e.ActivityId equals a.Id
                             select new { e.Name, e.DistanceM, e.ElapsedTimeS, a.StartDateLocal, a.StravaId, ActivityName = a.Name })
                            .ToListAsync(ct);
        var withoutDetail = await runs.CountAsync(a => a.DetailFetchedAtUtc == null && !a.IsManual, ct);

        var yearAgo = Metrics.ClampTo(filter, Today).AddYears(-1).ToDateTime(TimeOnly.MinValue);
        var bests = new List<EffortBest>();
        var timeline = new List<EffortPoint>();
        foreach (var g in efforts.Where(e => e.ElapsedTimeS > 0).GroupBy(e => e.Name).OrderBy(g => g.Min(e => e.DistanceM)))
        {
            var best = g.OrderBy(e => e.ElapsedTimeS).ThenBy(e => e.StartDateLocal).First();
            var recent = g.Where(e => e.StartDateLocal >= yearAgo).Select(e => (int?)e.ElapsedTimeS).Min();
            bests.Add(new EffortBest(g.Key, best.DistanceM, best.ElapsedTimeS, best.StartDateLocal, best.StravaId, best.ActivityName, g.Count(), recent));

            // A PB timeline: every effort that beat all earlier ones.
            var pb = int.MaxValue;
            foreach (var e in g.OrderBy(e => e.StartDateLocal))
            {
                if (e.ElapsedTimeS >= pb) continue;
                pb = e.ElapsedTimeS;
                timeline.Add(new EffortPoint(g.Key, e.DistanceM, e.StartDateLocal, e.ElapsedTimeS, e.StravaId));
            }
        }

        // This page has no sport selector, so a Ride or Run choice made on another page must not hide the other sport here.
        var all = await LoadRowsAsync(filter.WithGroup(SportGroup.All), ct);
        var highlights = new List<Highlight>();
        void Add(string label, HighlightKind kind, IEnumerable<ActivityRow> rows, Func<ActivityRow, double> value)
        {
            var top = rows.MaxBy(value);
            if (top is not null && value(top) > 0) highlights.Add(new Highlight(label, kind, value(top), top.StartDateLocal, top.StravaId, top.Name, top.FromArchive));
        }
        var runRows = all.Where(r => SportGroups.Classify(r.SportType) == SportGroup.Run).ToList();
        var rideRows = all.Where(r => SportGroups.Classify(r.SportType) == SportGroup.Ride).ToList();
        Add("Longest run", HighlightKind.Distance, runRows, r => r.DistanceM);
        Add("Most climbing on a run", HighlightKind.Elevation, runRows, r => r.ElevationGainM);
        Add("Longest ride", HighlightKind.Distance, rideRows, r => r.DistanceM);
        Add("Most climbing on a ride", HighlightKind.Elevation, rideRows, r => r.ElevationGainM);
        Add("Fastest ride (20 km+, outdoors)", HighlightKind.Speed, rideRows.Where(r => r.DistanceM >= 20_000 && !r.IsTrainer), r => r.AvgSpeed);
        Add("Longest activity", HighlightKind.Duration, all, r => r.MovingTimeS);

        var weeks = all.GroupBy(r => Calc.StartOfWeek(r.Day, s.WeekStart)).Select(g => (Week: g.Key, Run: g.Where(r => SportGroups.Classify(r.SportType) == SportGroup.Run).Sum(r => r.DistanceM), Ride: g.Where(r => SportGroups.Classify(r.SportType) == SportGroup.Ride).Sum(r => r.DistanceM))).ToList();
        if (weeks.Count > 0 && weeks.MaxBy(w => w.Run) is { Run: > 0 } bw) highlights.Add(new Highlight("Biggest running week", HighlightKind.Distance, bw.Run, bw.Week.ToDateTime(TimeOnly.MinValue), null, null));
        if (weeks.Count > 0 && weeks.MaxBy(w => w.Ride) is { Ride: > 0 } rw) highlights.Add(new Highlight("Biggest cycling week", HighlightKind.Distance, rw.Ride, rw.Week.ToDateTime(TimeOnly.MinValue), null, null));

        // Whole-run records: a run within 3% under to 10% over the distance (GPS rarely reads exactly 5.00 km), fastest by speed.
        var wholeRuns = new List<DistanceRecord>();
        foreach (var (label, target) in new[] { ("5K", 5000.0), ("10K", 10000.0), ("Half marathon", 21097.5), ("Marathon", 42195.0) })
        {
            var best = runRows.Where(r => r.MovingTimeS > 0 && r.DistanceM >= target * 0.97 && r.DistanceM <= target * 1.10)
                .OrderByDescending(r => r.DistanceM / r.MovingTimeS).FirstOrDefault();
            if (best is not null) wholeRuns.Add(new DistanceRecord(label, target, best));
        }

        return new RecordsResult(bests, timeline, highlights, withoutDetail, wholeRuns);
    }
}

// ---------------------------------------------------------------- Heart-rate zones

public record ZoneWeek(DateOnly Week, double[] Seconds);

public record ZoneGroup(string Group, double[] Seconds);

public record HrZonesResult(HrZoneSet? Zones, double[] TotalSeconds, IReadOnlyList<ZoneWeek> Weeks, IReadOnlyList<ZoneGroup> BySport,
    int FromStreams, int Estimated, int WithoutHr);

public class HrZonesReport(IDbContextFactory<StravaDbContext> dbFactory, StreamAnalysisService streams) : AnalysisReportBase(dbFactory, streams)
{
    public override string Id => "hr-zones";
    public override string Title => "Heart-rate zones";
    public override string Description => "Time in each heart-rate zone by week and by sport.";
    public override string Section => "All sports";
    public override int Order => 25;

    public async Task<HrZonesResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(filter, ct);
        var zones = HrZoneSet.Resolve(s, await ObservedMaxHrAsync(ct));
        if (zones is null) return new HrZonesResult(null, new double[5], [], [], 0, 0, rows.Count);

        var summaries = await SummariesAsync(rows.Where(r => r.AvgHr is not null), ct);
        var total = new double[5];
        var byWeek = new Dictionary<DateOnly, double[]>();
        var byGroup = new Dictionary<SportGroup, double[]>();
        int fromStreams = 0, estimated = 0, without = 0;
        foreach (var r in rows)
        {
            if (Metrics.ZonesFor(r, summaries.GetValueOrDefault(r.StravaId), zones) is not { } z) { without++; continue; }
            if (z.Estimated) estimated++; else fromStreams++;
            var week = Calc.StartOfWeek(r.Day, s.WeekStart);
            var w = byWeek.TryGetValue(week, out var ws) ? ws : byWeek[week] = new double[5];
            var grp = SportGroups.Classify(r.SportType);
            var gs = byGroup.TryGetValue(grp, out var g) ? g : byGroup[grp] = new double[5];
            for (var i = 0; i < 5; i++) { total[i] += z.Seconds[i]; w[i] += z.Seconds[i]; gs[i] += z.Seconds[i]; }
        }

        var weeks = new List<ZoneWeek>();
        if (rows.Count > 0)
            foreach (var wk in Calc.PeriodRange(filter.From ?? rows[0].Day, Metrics.ClampTo(filter, Today), Grouping.Week, s.WeekStart))
                weeks.Add(new ZoneWeek(wk, byWeek.GetValueOrDefault(wk) ?? new double[5]));

        var groups = byGroup.OrderBy(kv => kv.Key).Select(kv => new ZoneGroup(kv.Key switch
        {
            SportGroup.Run => "Running", SportGroup.Ride => "Cycling", _ => "Other",
        }, kv.Value)).ToList();
        return new HrZonesResult(zones, total, weeks, groups, fromStreams, estimated, without);
    }
}

// ---------------------------------------------------------------- Training load

public enum LoadMethod { Power, HeartRate, RelativeEffort, Duration }

public record ActivityLoad(ActivityRow Activity, double Load, LoadMethod Method);

public record TrainingLoadResult(IReadOnlyList<LoadDay> Days, IReadOnlyList<PeriodValue> WeeklyLoad, IReadOnlyList<ActivityLoad> Activities,
    LoadDay? Latest, double? RampRate, IReadOnlyDictionary<LoadMethod, int> Methods, int? MaxHrUsed);

public class TrainingLoadReport(IDbContextFactory<StravaDbContext> dbFactory, StreamAnalysisService streams) : AnalysisReportBase(dbFactory, streams)
{
    public override string Id => "training-load";
    public override string Title => "Training load";
    public override string Description => "Fitness (42-day), fatigue (7-day), form and ramp rate from power, heart rate or effort.";
    public override string Section => "All sports";
    public override int Order => 27;

    /// <summary>Days of history loaded before the range so fitness has warmed up by the first day shown.</summary>
    public const int WarmUpDays = 180;

    public async Task<TrainingLoadResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var to = Metrics.ClampTo(filter, Today);
        var rows = await LoadRowsAsync(filter with { From = filter.From?.AddDays(-WarmUpDays), To = to }, ct);
        var observed = await ObservedMaxHrAsync(ct);
        int? maxHr = s.MaxHr ?? (observed is { } o && o > 100 ? (int)Math.Round(o) : null);
        var summaries = await SummariesAsync(rows, ct);

        var loads = rows.Select(r => Metrics.LoadFor(r, summaries.GetValueOrDefault(r.StravaId), s, maxHr)).ToList();
        if (loads.Count == 0) return new TrainingLoadResult([], [], [], null, null, new Dictionary<LoadMethod, int>(), maxHr);

        var byDay = loads.GroupBy(l => l.Activity.Day).ToDictionary(g => g.Key, g => g.Sum(l => l.Load));
        var first = loads.Min(l => l.Activity.Day);
        var daily = new List<(DateOnly, double)>();
        for (var d = first; d <= to; d = d.AddDays(1)) daily.Add((d, byDay.GetValueOrDefault(d)));
        var series = StreamMath.FitnessFatigue(daily);

        var from = filter.From ?? first;
        var shown = series.Where(d => d.Day >= from).ToList();
        var inRange = loads.Where(l => l.Activity.Day >= from).ToList();
        var weekly = Calc.PeriodRange(from, to, Grouping.Week, s.WeekStart)
            .Select(w => new PeriodValue(w, inRange.Where(l => l.Activity.Day >= w && l.Activity.Day < w.AddDays(7)).Sum(l => l.Load))).ToList();
        var latest = series[^1];
        double? ramp = series.Count > 7 ? latest.Fitness - series[^8].Fitness : null;
        var methods = inRange.GroupBy(l => l.Method).ToDictionary(g => g.Key, g => g.Count());
        return new TrainingLoadResult(shown, weekly, inRange.OrderByDescending(l => l.Activity.StartDateLocal).ToList(), latest, ramp, methods, maxHr);
    }
}

// ---------------------------------------------------------------- Cycling power

public record PowerRide(ActivityRow Ride, double? NormalizedPower, double? Tss, double? IntensityFactor, double Kilojoules);

public record PowerCurvePoint(int Seconds, double Watts, long StravaId, DateTime Date);

public record FtpPoint(DateOnly Month, double EstimatedFtp, string Basis);

public record PowerResult(IReadOnlyList<PowerRide> Rides, IReadOnlyList<PeriodValue> WeeklyKj, IReadOnlyList<PowerCurvePoint> Curve,
    IReadOnlyList<FtpPoint> FtpTrend, double? CurrentFtpEstimate, int RidesWithStreams, int RidesWithoutPower);

public class PowerReport(IDbContextFactory<StravaDbContext> dbFactory, StreamAnalysisService streams) : AnalysisReportBase(dbFactory, streams)
{
    public override string Id => "power";
    public override string Title => "Cycling power";
    public override string Description => "Average and normalised power, kilojoules, power curve and estimated FTP trend.";
    public override string Section => "Cycling";
    public override int Order => 45;

    public async Task<PowerResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(filter.WithGroup(SportGroup.Ride), ct);
        var powered = rows.Where(r => r.AvgWatts is > 0).ToList();
        var summaries = await SummariesAsync(powered, ct);

        var rides = powered.Select(r =>
        {
            var sum = summaries.GetValueOrDefault(r.StravaId);
            var np = sum?.NormalizedPower ?? (r.DeviceWatts ? r.WeightedAvgWatts : null);
            var tss = StreamMath.Tss(r.MovingTimeS, np, s.Ftp);
            double? intensity = np is { } n && s.Ftp is > 0 ? n / s.Ftp.Value : null;
            var kj = r.Kilojoules ?? r.AvgWatts!.Value * r.MovingTimeS / 1000;
            return new PowerRide(r, np, tss, intensity, kj);
        }).ToList();

        var weekly = new List<PeriodValue>();
        if (rows.Count > 0)
            foreach (var w in Calc.PeriodRange(filter.From ?? rows[0].Day, Metrics.ClampTo(filter, Today), Grouping.Week, s.WeekStart))
                weekly.Add(new PeriodValue(w, rides.Where(p => p.Ride.Day >= w && p.Ride.Day < w.AddDays(7)).Sum(p => p.Kilojoules)));

        var curve = new List<PowerCurvePoint>();
        foreach (var d in StreamMath.PeakDurations)
        {
            // Nullable so an empty sequence gives null rather than MaxBy throwing on a value tuple.
            var best = powered.Select(r => ((ActivityRow Ride, double Watts)?)(r, summaries.GetValueOrDefault(r.StravaId)?.PowerPeaks.GetValueOrDefault(d) ?? 0))
                .Where(x => x!.Value.Watts > 0).MaxBy(x => x!.Value.Watts);
            if (best is { } b) curve.Add(new PowerCurvePoint(d, b.Watts, b.Ride.StravaId, b.Ride.StartDateLocal));
        }

        // FTP estimate per month: 95% of the best 20-minute power, or normalised power of an hour-plus ride.
        var trend = rides.GroupBy(p => new DateOnly(p.Ride.Day.Year, p.Ride.Day.Month, 1)).OrderBy(g => g.Key)
            .Select(g =>
            {
                var twenty = g.Select(p => summaries.GetValueOrDefault(p.Ride.StravaId)?.PowerPeaks.GetValueOrDefault(1200) ?? 0).Max() * 0.95;
                var hour = g.Where(p => p.Ride.MovingTimeS >= 3300).Select(p => p.NormalizedPower ?? 0).DefaultIfEmpty().Max();
                return twenty >= hour
                    ? new FtpPoint(g.Key, twenty, "95% of best 20 min")
                    : new FtpPoint(g.Key, hour, "normalised power, 1 h+ ride");
            })
            .Where(p => p.EstimatedFtp > 0).ToList();
        var recentCutoff = Metrics.ClampTo(filter, Today).AddMonths(-3);
        double? current = trend.Where(t => t.Month >= new DateOnly(recentCutoff.Year, recentCutoff.Month, 1)).Select(t => (double?)t.EstimatedFtp).Max();

        return new PowerResult(rides, weekly, curve, trend, current, powered.Count(r => summaries.ContainsKey(r.StravaId) && summaries[r.StravaId].PowerPeaks.Count > 0), rows.Count - powered.Count);
    }
}

// ---------------------------------------------------------------- Running efficiency

public record EfficiencyRun(ActivityRow Run, double PaceSecPerKm, double AvgHr, double EfficiencyFactor, double? CadenceSpm);

public record EfficiencyWeek(DateOnly Week, double? EfficiencyFactor, double? CadenceSpm, double? AvgHr, double? PaceSecPerKm);

public record RunningEfficiencyResult(IReadOnlyList<EfficiencyRun> Runs, IReadOnlyList<EfficiencyWeek> Weeks, HrZoneSet? Zones, double[] ZoneSeconds, int RunsWithoutHr);

public class RunningEfficiencyReport(IDbContextFactory<StravaDbContext> dbFactory, StreamAnalysisService streams) : AnalysisReportBase(dbFactory, streams)
{
    public override string Id => "running-efficiency";
    public override string Title => "Running efficiency";
    public override string Description => "Pace versus heart rate, efficiency factor, cadence trend and zone time for runs.";
    public override string Section => "Running";
    public override int Order => 34;

    public async Task<RunningEfficiencyResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(filter.WithGroup(SportGroup.Run), ct);
        // Strava's run cadence is per leg; double it for steps per minute.
        var runs = rows.Where(r => r.AvgHr is > 0 && r.DistanceM > 0 && r.MovingTimeS > 0)
            .Select(r => new EfficiencyRun(r, r.MovingTimeS / (r.DistanceM / 1000), r.AvgHr!.Value,
                r.DistanceM / (r.MovingTimeS / 60.0) / r.AvgHr.Value, r.AvgCadence is > 0 ? r.AvgCadence * 2 : null))
            .ToList();

        var weeks = new List<EfficiencyWeek>();
        if (rows.Count > 0)
        {
            var byWeek = runs.GroupBy(r => Calc.StartOfWeek(r.Run.Day, s.WeekStart)).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var w in Calc.PeriodRange(filter.From ?? rows[0].Day, Metrics.ClampTo(filter, Today), Grouping.Week, s.WeekStart))
            {
                if (byWeek.GetValueOrDefault(w) is not { Count: > 0 } list) { weeks.Add(new EfficiencyWeek(w, null, null, null, null)); continue; }
                // Time-weighted so one short jog does not swing the week.
                double time = list.Sum(r => r.Run.MovingTimeS);
                var cad = list.Where(r => r.CadenceSpm is not null).ToList();
                weeks.Add(new EfficiencyWeek(w,
                    list.Sum(r => r.EfficiencyFactor * r.Run.MovingTimeS) / time,
                    cad.Count == 0 ? null : cad.Sum(r => r.CadenceSpm!.Value * r.Run.MovingTimeS) / cad.Sum(r => (double)r.Run.MovingTimeS),
                    list.Sum(r => r.AvgHr * r.Run.MovingTimeS) / time,
                    list.Sum(r => (double)r.Run.MovingTimeS) / (list.Sum(r => r.Run.DistanceM) / 1000)));
            }
        }

        var zones = HrZoneSet.Resolve(s, await ObservedMaxHrAsync(ct));
        var zoneSeconds = new double[5];
        if (zones is not null)
        {
            var summaries = await SummariesAsync(runs.Select(r => r.Run), ct);
            foreach (var r in runs)
                if (Metrics.ZonesFor(r.Run, summaries.GetValueOrDefault(r.Run.StravaId), zones) is { } z)
                    for (var i = 0; i < 5; i++) zoneSeconds[i] += z.Seconds[i];
        }
        return new RunningEfficiencyResult(runs, weeks, zones, zoneSeconds, rows.Count - runs.Count);
    }
}

// ---------------------------------------------------------------- Activity detail (streams)

public record SeriesPoint(double X, double? Y);

public record ActivityStreamCharts(
    bool XIsDistance,
    IReadOnlyList<SeriesPoint> Altitude, IReadOnlyList<SeriesPoint> Speed, IReadOnlyList<SeriesPoint> HeartRate,
    IReadOnlyList<SeriesPoint> Watts, IReadOnlyList<SeriesPoint> Cadence,
    HrZoneSet? Zones, double[]? ZoneSeconds, IReadOnlyDictionary<int, double> PowerPeaks, double? NormalizedPower);

public class ActivityAnalysis(StreamAnalysisService streams, IDbContextFactory<StravaDbContext> dbFactory)
{
    public const int MaxPoints = 500;

    /// <summary>Chart series in storage units (metres, m/s, bpm, W) against distance, or time when there is no distance stream.</summary>
    public async Task<ActivityStreamCharts?> ChartsAsync(Activity a, AppSettingsModel s, CancellationToken ct = default)
    {
        if (a.StreamsFetchedAtUtc is null) return null;
        var d = await streams.LoadAsync(a.StravaId, ct);
        if (d is null || d.Count < 2) return null;

        var byDistance = d.Distance is { } dist && dist[^1] > 0;
        IReadOnlyList<double> x = byDistance ? d.Distance! : d.Time.Select(t => (double)(t - d.Time[0])).ToArray();
        List<SeriesPoint> Series(IReadOnlyList<double?>? y) =>
            y is null || y.All(v => v is null or 0) ? [] : StreamMath.Downsample(x, y, MaxPoints).Select(p => new SeriesPoint(p.X, p.Y)).ToList();
        static double?[]? Nullable(double[]? v) => v?.Select(x => (double?)x).ToArray();

        var summary = StreamAnalysisService.Summarise(d);
        double? observed;
        await using (var db = await dbFactory.CreateDbContextAsync(ct)) observed = await db.Activities.MaxAsync(x => x.MaxHr, ct);
        var zones = HrZoneSet.Resolve(s, observed);
        var zoneSeconds = zones is not null && summary.HrHistogram.Count > 0 ? StreamMath.ZoneSeconds(summary.HrHistogram, zones, a.MovingTimeS) : null;

        return new ActivityStreamCharts(byDistance,
            Series(Nullable(d.Altitude)), Series(Nullable(d.Velocity)), Series(d.HeartRate), Series(d.Watts), Series(d.Cadence),
            zones, zoneSeconds, summary.PowerPeaks, summary.NormalizedPower);
    }
}
