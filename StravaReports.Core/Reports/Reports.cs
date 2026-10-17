using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Settings;

namespace StravaReports.Core.Reports;

/// <summary>
/// A report: metadata the navigation reads, plus a query over the local database.
/// Adding a report = one IReport class + one Razor view + one registration line.
/// </summary>
public interface IReport
{
    string Id { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>Navigation group, e.g. "All sports", "Running", "Cycling".</summary>
    string Section { get; }
    int Order { get; }
}

public abstract class ReportBase(IDbContextFactory<StravaDbContext> dbFactory) : IReport
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract string Section { get; }
    public abstract int Order { get; }

    /// <summary>Set by AddReport when the archive database is registered.</summary>
    internal Archive.ArchiveRowSource? Archive { get; set; }

    protected async Task<List<ActivityRow>> LoadRowsAsync(ReportFilter filter, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Activities.AsNoTracking().ApplyFilter(filter).OrderBy(a => a.StartDateLocal).SelectRows().ToListAsync(ct);
        if (filter.IncludeArchive && Archive is not null)
        {
            var extra = await Archive.RowsAsync(filter, ct);
            if (extra.Count > 0) rows = [.. rows.Concat(extra).OrderBy(r => r.StartDateLocal)];
        }
        return rows;
    }

    protected async Task<StravaDbContext> CreateDbAsync(CancellationToken ct) => await dbFactory.CreateDbContextAsync(ct);

    protected static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}

// ---------------------------------------------------------------- Dashboard

public record DashboardResult(
    Totals Week, Totals Month, Totals Year, Totals AllTime,
    int CurrentDailyStreak, int LongestDailyStreak, int CurrentWeeklyStreak,
    ActivityRow? Latest, IReadOnlyList<ActivityRow> Recent, IReadOnlyList<PeriodTotals> Last12Weeks);

public class DashboardReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "dashboard";
    public override string Title => "Dashboard";
    public override string Description => "Totals this week, month and year; streaks; latest activity.";
    public override string Section => "All sports";
    public override int Order => 0;

    /// <summary>Uses the sport and gear filter but ignores its date range: periods are fixed.</summary>
    public async Task<DashboardResult> RunAsync(ReportFilter filter, AppSettingsModel s, DateOnly? today = null, CancellationToken ct = default)
    {
        var day = today ?? Today;
        var rows = await LoadRowsAsync(filter.WithoutDates(), ct);
        var weekStart = Calc.StartOfWeek(day, s.WeekStart);
        var monthStart = new DateOnly(day.Year, day.Month, 1);
        var yearStart = new DateOnly(day.Year, 1, 1);
        var days = rows.Select(r => r.Day).ToHashSet();

        return new DashboardResult(
            Totals.Of(rows.Where(r => r.Day >= weekStart && r.Day <= day)),
            Totals.Of(rows.Where(r => r.Day >= monthStart && r.Day <= day)),
            Totals.Of(rows.Where(r => r.Day >= yearStart && r.Day <= day)),
            Totals.Of(rows),
            Calc.CurrentDailyStreak(days, day),
            Calc.LongestDailyStreak(days),
            Calc.CurrentWeeklyStreak(days, day, s.WeekStart),
            rows.LastOrDefault(),
            rows.AsEnumerable().Reverse().Take(10).ToList(),
            Enumerable.Range(0, 12).Select(i => weekStart.AddDays(-7 * (11 - i)))
                .Select(w => new PeriodTotals(w, Totals.Of(rows.Where(r => r.Day >= w && r.Day < w.AddDays(7))))).ToList());
    }
}

// ---------------------------------------------------------------- Volume trends

public record PeriodTotals(DateOnly PeriodStart, Totals Totals);

public record YearSeries(int Year, IReadOnlyList<double> CumulativeDistanceByWeek);

public record VolumeTrendsResult(IReadOnlyList<PeriodTotals> Periods, IReadOnlyList<YearSeries> YearOverlay, Totals Total);

public class VolumeTrendsReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "volume";
    public override string Title => "Volume trends";
    public override string Description => "Distance, time and elevation by week, month or year, with a year-on-year overlay.";
    public override string Section => "All sports";
    public override int Order => 10;

    public async Task<VolumeTrendsResult> RunAsync(ReportFilter filter, Grouping grouping, AppSettingsModel s, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(filter, ct);
        var periods = new List<PeriodTotals>();
        if (rows.Count > 0)
        {
            var from = filter.From ?? rows[0].Day;
            var to = filter.To is { } t && t < Today ? t : Today;
            var byPeriod = rows.GroupBy(r => Calc.PeriodStart(r.Day, grouping, s.WeekStart)).ToDictionary(g => g.Key, g => Totals.Of(g));
            foreach (var p in Calc.PeriodRange(from, to, grouping, s.WeekStart))
                periods.Add(new PeriodTotals(p, byPeriod.GetValueOrDefault(p, Totals.Empty)));
        }

        // Year-on-year overlay ignores the date range so earlier years are always comparable.
        var all = filter.From is null && filter.To is null ? rows : await LoadRowsAsync(filter.WithoutDates(), ct);
        var overlay = all.GroupBy(r => r.StartDateLocal.Year)
            .OrderByDescending(g => g.Key).Take(5).OrderBy(g => g.Key)
            .Select(g =>
            {
                var weekly = new double[53];
                foreach (var r in g) weekly[Math.Min(52, (r.StartDateLocal.DayOfYear - 1) / 7)] += r.DistanceM;
                var cumulative = new List<double>(53);
                double sum = 0;
                var lastWeek = g.Key == Today.Year ? (Today.DayOfYear - 1) / 7 : 52;
                for (var w = 0; w <= lastWeek; w++) { sum += weekly[w]; cumulative.Add(sum); }
                return new YearSeries(g.Key, cumulative);
            }).ToList();

        return new VolumeTrendsResult(periods, overlay, Totals.Of(rows));
    }
}

// ---------------------------------------------------------------- Consistency

public record DayCount(DateOnly Day, int Count, double DistanceM, int MovingTimeS);

public record ConsistencyResult(
    DateOnly From, DateOnly To,
    IReadOnlyList<DayCount> Days,
    IReadOnlyList<(DateOnly Week, int Count)> ActivitiesPerWeek,
    int ActiveDays, int CurrentDailyStreak, int LongestDailyStreak, int CurrentWeeklyStreak, int LongestWeeklyStreak);

public class ConsistencyReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "consistency";
    public override string Title => "Consistency";
    public override string Description => "Calendar heatmap, activities per week and streaks.";
    public override string Section => "All sports";
    public override int Order => 20;

    public async Task<ConsistencyResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var to = filter.To is { } t && t < Today ? t : Today;
        var from = filter.From ?? to.AddDays(-364);
        var rows = await LoadRowsAsync(filter with { From = from, To = to }, ct);

        var byDay = rows.GroupBy(r => r.Day).ToDictionary(g => g.Key, g => g.ToList());
        var days = new List<DayCount>();
        for (var d = Calc.StartOfWeek(from, s.WeekStart); d <= to; d = d.AddDays(1))
        {
            var list = byDay.GetValueOrDefault(d);
            days.Add(new DayCount(d, list?.Count ?? 0, list?.Sum(r => r.DistanceM) ?? 0, list?.Sum(r => r.MovingTimeS) ?? 0));
        }

        var weekCounts = rows.GroupBy(r => Calc.StartOfWeek(r.Day, s.WeekStart)).ToDictionary(g => g.Key, g => g.Count());
        var perWeek = Calc.PeriodRange(from, to, Grouping.Week, s.WeekStart).Select(w => (w, weekCounts.GetValueOrDefault(w))).ToList();
        var active = byDay.Keys.ToHashSet();

        return new ConsistencyResult(from, to, days, perWeek, active.Count,
            Calc.CurrentDailyStreak(active, to), Calc.LongestDailyStreak(active),
            Calc.CurrentWeeklyStreak(active, to, s.WeekStart), Calc.LongestWeeklyStreak(active, s.WeekStart));
    }
}

// ---------------------------------------------------------------- Running

public record RunWeek(DateOnly Week, double DistanceM, int MovingTimeS, int Count, double LongestRunM)
{
    public double? PaceSecondsPerKm => DistanceM > 0 ? MovingTimeS / (DistanceM / 1000) : null;
}

public record DistanceBand(string Label, double MinM, double MaxM, int Count, double DistanceM, int MovingTimeS, double? FastestPaceSecPerKm);

public record RunningResult(IReadOnlyList<RunWeek> Weeks, IReadOnlyList<DistanceBand> Bands, IReadOnlyList<ActivityRow> Runs, Totals Total);

public class RunningReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "running";
    public override string Title => "Running pace & distance";
    public override string Description => "Pace trend, weekly mileage, long-run progression and pace by distance band.";
    public override string Section => "Running";
    public override int Order => 30;

    public static readonly (string Label, double Min, double Max)[] BandDefs =
    [
        ("Under 5K", 0, 5000), ("5–10K", 5000, 10000), ("10–15K", 10000, 15000),
        ("15K–half", 15000, 21097.5), ("Half–30K", 21097.5, 30000), ("30K+", 30000, double.MaxValue),
    ];

    public async Task<RunningResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(filter.WithGroup(SportGroup.Run), ct);
        var weeks = new List<RunWeek>();
        if (rows.Count > 0)
        {
            var to = filter.To is { } t && t < Today ? t : Today;
            var byWeek = rows.GroupBy(r => Calc.StartOfWeek(r.Day, s.WeekStart)).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var w in Calc.PeriodRange(filter.From ?? rows[0].Day, to, Grouping.Week, s.WeekStart))
            {
                var list = byWeek.GetValueOrDefault(w) ?? [];
                weeks.Add(new RunWeek(w, list.Sum(r => r.DistanceM), list.Sum(r => r.MovingTimeS), list.Count, list.Count == 0 ? 0 : list.Max(r => r.DistanceM)));
            }
        }

        var bands = BandDefs.Select(b =>
        {
            var inBand = rows.Where(r => r.DistanceM >= b.Min && r.DistanceM < b.Max && r.MovingTimeS > 0).ToList();
            return new DistanceBand(b.Label, b.Min, b.Max, inBand.Count, inBand.Sum(r => r.DistanceM), inBand.Sum(r => r.MovingTimeS),
                inBand.Count == 0 ? null : inBand.Min(r => r.MovingTimeS / (r.DistanceM / 1000)));
        }).ToList();

        return new RunningResult(weeks, bands, rows, Totals.Of(rows));
    }
}

// ---------------------------------------------------------------- Cycling

public record RideWeek(DateOnly Week, double DistanceM, int MovingTimeS, double ElevationGainM, int Count)
{
    public double? AvgSpeedMps => MovingTimeS > 0 ? DistanceM / MovingTimeS : null;
}

public record CyclingResult(IReadOnlyList<RideWeek> Weeks, IReadOnlyList<ActivityRow> Rides, Totals Total, double? AvgPowerW, double? TotalKj);

public class CyclingReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "cycling";
    public override string Title => "Cycling volume & speed";
    public override string Description => "Distance, average speed and elevation per ride; climbing per week.";
    public override string Section => "Cycling";
    public override int Order => 40;

    public async Task<CyclingResult> RunAsync(ReportFilter filter, AppSettingsModel s, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(filter.WithGroup(SportGroup.Ride), ct);
        var weeks = new List<RideWeek>();
        if (rows.Count > 0)
        {
            var to = filter.To is { } t && t < Today ? t : Today;
            var byWeek = rows.GroupBy(r => Calc.StartOfWeek(r.Day, s.WeekStart)).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var w in Calc.PeriodRange(filter.From ?? rows[0].Day, to, Grouping.Week, s.WeekStart))
            {
                var list = byWeek.GetValueOrDefault(w) ?? [];
                weeks.Add(new RideWeek(w, list.Sum(r => r.DistanceM), list.Sum(r => r.MovingTimeS), list.Sum(r => r.ElevationGainM), list.Count));
            }
        }
        var powered = rows.Where(r => r.AvgWatts is not null).ToList();
        double? avgPower = powered.Count == 0 ? null : powered.Sum(r => r.AvgWatts!.Value * r.MovingTimeS) / Math.Max(1, powered.Sum(r => r.MovingTimeS));
        double? kj = rows.Any(r => r.Kilojoules is not null) ? rows.Sum(r => r.Kilojoules ?? 0) : null;
        return new CyclingResult(weeks, rows, Totals.Of(rows), avgPower, kj);
    }
}

// ---------------------------------------------------------------- Gear mileage

public record GearUsage(Gear Gear, double LocalDistanceM, int ActivityCount, DateTime? LastUsedLocal, bool NeedsRetirement);

public class GearReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "gear";
    public override string Title => "Gear mileage";
    public override string Description => "Distance per bike and shoe, with retirement alerts.";
    public override string Section => "All sports";
    public override int Order => 50;

    public async Task<List<GearUsage>> RunAsync(AppSettingsModel s, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var gear = await db.Gear.AsNoTracking().ToListAsync(ct);
        var usage = await db.Activities.AsNoTracking().Where(a => a.GearId != null)
            .GroupBy(a => a.GearId!)
            .Select(g => new { GearId = g.Key, Distance = g.Sum(a => a.DistanceM), Count = g.Count(), Last = g.Max(a => a.StartDateLocal) })
            .ToDictionaryAsync(x => x.GearId, ct);

        return gear.Select(g =>
        {
            var u = usage.GetValueOrDefault(g.StravaId);
            var distance = Math.Max(g.DistanceM, u?.Distance ?? 0);
            var retire = g.GearType == "shoe" && !g.IsRetired && distance / 1000 >= s.ShoeRetirementKm;
            return new GearUsage(g, u?.Distance ?? 0, u?.Count ?? 0, u?.Last, retire);
        })
        .OrderBy(x => x.Gear.IsRetired).ThenBy(x => x.Gear.GearType).ThenByDescending(x => x.Gear.DistanceM)
        .ToList();
    }
}

// ---------------------------------------------------------------- Activities list (shared lookups)

public class ActivityQueries(IDbContextFactory<StravaDbContext> dbFactory, Archive.ArchiveRowSource? archive = null)
{
    public async Task<List<ActivityRow>> ListAsync(ReportFilter filter, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var synced = db.Activities.AsNoTracking().ApplyFilter(filter);
        if (filter.IncludeArchive && archive is not null)
        {
            // Archive-only rows are merged in memory, so page the combined, newest-first list.
            var rows = await synced.SelectRows().ToListAsync(ct);
            rows.AddRange(await archive.RowsAsync(filter, ct));
            return [.. rows.OrderByDescending(r => r.StartDateLocal).Skip(skip).Take(take)];
        }
        return await synced.OrderByDescending(a => a.StartDateLocal).SelectRows().Skip(skip).Take(take).ToListAsync(ct);
    }

    public async Task<int> CountAsync(ReportFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.Activities.ApplyFilter(filter).CountAsync(ct);
        if (filter.IncludeArchive && archive is not null) n += (await archive.RowsAsync(filter, ct)).Count;
        return n;
    }

    public async Task<List<Gear>> GearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Gear.AsNoTracking().OrderBy(g => g.IsRetired).ThenBy(g => g.Name).ToListAsync(ct);
    }

    public async Task<List<string>> SportTypesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Activities.Select(a => a.SportType).Distinct().OrderBy(x => x).ToListAsync(ct);
    }

    public async Task<Activity?> GetWithLapsAsync(long stravaId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Activities.AsNoTracking().Include(a => a.Laps).Include(a => a.BestEfforts).AsSplitQuery()
            .FirstOrDefaultAsync(a => a.StravaId == stravaId, ct);
    }
}
