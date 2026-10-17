using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Export;

namespace StravaReports.Core.Reports;

public enum DataSort { Date, Name, Sport, Gear, Distance, Time, Elapsed, Speed, Elevation, AvgHr, MaxHr, Watts, Kilojoules, Cadence, Effort }

/// <summary>
/// Filters applied on top of the shared <see cref="ReportFilter"/> (dates, sport, gear, trainer, commute, archive).
/// They run in memory over the rows already loaded, so changing them never touches the database.
/// </summary>
public record DataQuery
{
    /// <summary>Space-separated words; every word must appear in the name, sport or gear name.</summary>
    public string? Text { get; init; }
    public double? MinDistanceM { get; init; }
    public double? MaxDistanceM { get; init; }
    public int? MinTimeS { get; init; }
    public int? MaxTimeS { get; init; }
    public double? MinElevationM { get; init; }
    public double? MaxElevationM { get; init; }
    public DayOfWeek? Day { get; init; }
    public bool RequireHeartRate { get; init; }
    public bool RequirePower { get; init; }
    public DataSort Sort { get; init; } = DataSort.Date;
    public bool Descending { get; init; } = true;

    /// <summary>True when any filter beyond the shared filter bar is set (sorting does not count).</summary>
    public bool IsFiltered =>
        !string.IsNullOrWhiteSpace(Text) || MinDistanceM is not null || MaxDistanceM is not null || MinTimeS is not null || MaxTimeS is not null ||
        MinElevationM is not null || MaxElevationM is not null || Day is not null || RequireHeartRate || RequirePower;

    public string[] Terms() => (Text ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
}

public record DataSummary(
    int Count, int ActiveDays, DateOnly? First, DateOnly? Last, int PeriodDays,
    double DistanceM, int MovingTimeS, int ElapsedTimeS, double ElevationGainM,
    double? Kilojoules, double? EffortSum, double? AvgHr, double? MaxHr, double? AvgWatts, double? AvgCadence,
    ActivityRow? Longest, ActivityRow? LongestTime, ActivityRow? BiggestClimb, ActivityRow? Fastest,
    int TrainerCount, int CommuteCount, int ArchiveCount, int WithHrCount, int WithPowerCount)
{
    public static readonly DataSummary Empty = new(0, 0, null, null, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, null, null, 0, 0, 0, 0, 0);

    public double? AvgSpeedMps => MovingTimeS > 0 ? DistanceM / MovingTimeS : null;
    public int StoppedTimeS => Math.Max(0, ElapsedTimeS - MovingTimeS);
    public double AvgDistanceM => Count > 0 ? DistanceM / Count : 0;
    public int AvgMovingTimeS => Count > 0 ? MovingTimeS / Count : 0;
    public double WeeksInPeriod => PeriodDays / 7.0;
    public double DistancePerWeekM => PeriodDays > 0 ? DistanceM / WeeksInPeriod : 0;
    public double ActivitiesPerWeek => PeriodDays > 0 ? Count / WeeksInPeriod : 0;
    public double TimePerWeekS => PeriodDays > 0 ? MovingTimeS / WeeksInPeriod : 0;
    public double ElevationPerKm => DistanceM > 0 ? ElevationGainM / (DistanceM / 1000) : 0;
}

public record DataGroup(string Key, string Label, Totals Totals, double Share)
{
    public double? AvgSpeedMps => Totals.MovingTimeS > 0 ? Totals.DistanceM / Totals.MovingTimeS : null;
}

public record AllDataSource(ReportFilter Filter, IReadOnlyList<ActivityRow> Rows, IReadOnlyDictionary<string, string> GearNames, DateOnly Today)
{
    public string GearName(string? gearId) =>
        gearId is null ? ""
        : GearNames.TryGetValue(gearId, out var n) ? n
        : gearId.StartsWith(Archive.ArchiveRowSource.NamePrefix, StringComparison.Ordinal) ? gearId[Archive.ArchiveRowSource.NamePrefix.Length..]   // archive-only gear
        : "Unknown gear";
}

public record AllDataResult(
    int LoadedCount,
    IReadOnlyList<ActivityRow> Rows,
    DataSummary Summary,
    IReadOnlyList<DataGroup> BySport,
    IReadOnlyList<DataGroup> ByGear,
    IReadOnlyList<DataGroup> ByYear,
    IReadOnlyList<DataGroup> ByMonth,
    IReadOnlyList<DataGroup> ByWeekday);

/// <summary>
/// Every activity in one filterable, sortable table with totals for exactly what is showing.
/// Loading and analysing are separate so changing a filter or sort re-analyses the loaded rows in memory.
/// </summary>
public class AllDataReport(IDbContextFactory<StravaDbContext> dbFactory, TimeProvider time) : ReportBase(dbFactory)
{
    public override string Id => "all-data";
    public override string Title => "All data";
    public override string Description => "Every activity in one table. Filter by date, sport, gear, distance, name and more; the totals follow what you see.";
    public override string Section => "All sports";
    public override int Order => 5;

    /// <summary>Loads the rows matching the shared filter (including archive rows when asked for) and the gear names.</summary>
    public async Task<AllDataSource> LoadAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var rows = await LoadRowsAsync(filter, ct);
        await using var db = await CreateDbAsync(ct);
        var gear = await db.Gear.AsNoTracking().ToDictionaryAsync(g => g.StravaId, g => g.Name, ct);
        return new AllDataSource(filter, rows, gear, DateOnly.FromDateTime(time.GetLocalNow().DateTime));
    }

    public static AllDataResult Analyse(AllDataSource src, DataQuery q)
    {
        var terms = q.Terms();
        var rows = src.Rows.Where(r => Matches(r, q, terms, src)).ToList();
        var summary = rows.Count == 0 ? DataSummary.Empty : Summarise(rows, src);
        var sorted = Sort(rows, q, src);

        return new AllDataResult(src.Rows.Count, sorted, summary,
            Group(rows, r => r.SportType, k => k, byDistance: true),
            Group(rows, r => r.GearId ?? "", k => k == "" ? "No gear" : src.GearName(k), byDistance: true),
            Group(rows, r => r.StartDateLocal.Year.ToString(CultureInfo.InvariantCulture), k => k, byDistance: false),
            Group(rows, r => r.StartDateLocal.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                k => DateTime.ParseExact(k, "yyyy-MM", CultureInfo.InvariantCulture).ToString("MMM yyyy", CultureInfo.InvariantCulture), byDistance: false),
            Weekdays(rows));
    }

    private static bool Matches(ActivityRow r, DataQuery q, string[] terms, AllDataSource src)
    {
        if (q.MinDistanceM is { } minD && r.DistanceM < minD) return false;
        if (q.MaxDistanceM is { } maxD && r.DistanceM > maxD) return false;
        if (q.MinTimeS is { } minT && r.MovingTimeS < minT) return false;
        if (q.MaxTimeS is { } maxT && r.MovingTimeS > maxT) return false;
        if (q.MinElevationM is { } minE && r.ElevationGainM < minE) return false;
        if (q.MaxElevationM is { } maxE && r.ElevationGainM > maxE) return false;
        if (q.Day is { } day && r.StartDateLocal.DayOfWeek != day) return false;
        if (q.RequireHeartRate && r.AvgHr is null) return false;
        if (q.RequirePower && r.AvgWatts is null) return false;
        if (terms.Length > 0)
        {
            var gear = src.GearName(r.GearId);
            foreach (var t in terms)
            {
                if (!r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) && !r.SportType.Contains(t, StringComparison.OrdinalIgnoreCase) &&
                    !gear.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        return true;
    }

    private static List<ActivityRow> Sort(List<ActivityRow> rows, DataQuery q, AllDataSource src)
    {
        Func<ActivityRow, IComparable> key = q.Sort switch
        {
            DataSort.Name => r => r.Name.ToLowerInvariant(),
            DataSort.Sport => r => r.SportType,
            // Activities with no gear sort after the named gear when ascending.
            DataSort.Gear => r => r.GearId is null ? "￿" : src.GearName(r.GearId).ToLowerInvariant(),
            DataSort.Distance => r => r.DistanceM,
            DataSort.Time => r => r.MovingTimeS,
            DataSort.Elapsed => r => r.ElapsedTimeS,
            DataSort.Speed => r => r.AvgSpeed,
            DataSort.Elevation => r => r.ElevationGainM,
            DataSort.AvgHr => r => r.AvgHr ?? -1,
            DataSort.MaxHr => r => r.MaxHr ?? -1,
            DataSort.Watts => r => r.AvgWatts ?? -1,
            DataSort.Kilojoules => r => r.Kilojoules ?? -1,
            DataSort.Cadence => r => r.AvgCadence ?? -1,
            DataSort.Effort => r => r.SufferScore ?? -1,
            _ => r => r.StartDateLocal,
        };
        // Ties fall back to newest first so the order is always stable.
        var ordered = q.Descending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return ordered.ThenByDescending(r => r.StartDateLocal).ThenBy(r => r.StravaId).ToList();
    }

    private static DataSummary Summarise(List<ActivityRow> rows, AllDataSource src)
    {
        double distance = 0, elevation = 0;
        int moving = 0, elapsed = 0, trainer = 0, commute = 0, archive = 0;
        double kj = 0, effort = 0;
        bool anyKj = false, anyEffort = false;
        double hrSum = 0, wattSum = 0, cadSum = 0;
        int hrTime = 0, wattTime = 0, cadTime = 0, withHr = 0, withPower = 0;
        double? maxHr = null;
        ActivityRow? longest = null, longestTime = null, climb = null, fastest = null;

        foreach (var r in rows)
        {
            distance += r.DistanceM; moving += r.MovingTimeS; elapsed += r.ElapsedTimeS; elevation += r.ElevationGainM;
            if (r.IsTrainer) trainer++;
            if (r.IsCommute) commute++;
            if (r.FromArchive) archive++;
            if (r.Kilojoules is { } k) { kj += k; anyKj = true; }
            if (r.SufferScore is { } s) { effort += s; anyEffort = true; }
            if (r.AvgHr is { } hr) { hrSum += hr * r.MovingTimeS; hrTime += r.MovingTimeS; withHr++; }
            if (r.MaxHr is { } mh && (maxHr is null || mh > maxHr)) maxHr = mh;
            if (r.AvgWatts is { } w) { wattSum += w * r.MovingTimeS; wattTime += r.MovingTimeS; withPower++; }
            if (r.AvgCadence is { } c) { cadSum += c * r.MovingTimeS; cadTime += r.MovingTimeS; }
            if (longest is null || r.DistanceM > longest.DistanceM) longest = r;
            if (longestTime is null || r.MovingTimeS > longestTime.MovingTimeS) longestTime = r;
            if (climb is null || r.ElevationGainM > climb.ElevationGainM) climb = r;
            // Speed records only count real efforts, not a 50 m walk to the shop.
            if (r.DistanceM >= 1000 && r.MovingTimeS > 0 && (fastest is null || r.AvgSpeed > fastest.AvgSpeed)) fastest = r;
        }

        var first = rows.Min(r => r.Day);
        var last = rows.Max(r => r.Day);
        // Per-week figures use the period asked for (to today at most), not just the days with activities.
        var from = src.Filter.From ?? first;
        var to = src.Filter.To is { } t && t < src.Today ? t : src.Today;
        if (to < last) to = last;
        var days = Math.Max(1, to.DayNumber - from.DayNumber + 1);

        return new DataSummary(rows.Count, rows.Select(r => r.Day).Distinct().Count(), first, last, days,
            distance, moving, elapsed, elevation,
            anyKj ? kj : null, anyEffort ? effort : null,
            hrTime > 0 ? hrSum / hrTime : null, maxHr, wattTime > 0 ? wattSum / wattTime : null, cadTime > 0 ? cadSum / cadTime : null,
            longest, longestTime, climb, fastest, trainer, commute, archive, withHr, withPower);
    }

    /// <summary>Biggest first for sport and gear; newest first for periods (the keys sort chronologically).</summary>
    private static List<DataGroup> Group(List<ActivityRow> rows, Func<ActivityRow, string> keyOf, Func<string, string> label, bool byDistance)
    {
        var total = rows.Sum(r => r.DistanceM);
        var groups = rows.GroupBy(keyOf).Select(g =>
        {
            var t = Totals.Of(g);
            return new DataGroup(g.Key, label(g.Key), t, total > 0 ? t.DistanceM / total : 0);
        });
        return byDistance
            ? [.. groups.OrderByDescending(g => g.Totals.DistanceM).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)]
            : [.. groups.OrderByDescending(g => g.Key, StringComparer.Ordinal)];
    }

    private static List<DataGroup> Weekdays(List<ActivityRow> rows)
    {
        var total = rows.Sum(r => r.DistanceM);
        // Monday first, which is how most people think of the week.
        DayOfWeek[] order = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        return [.. order.Select(d =>
        {
            var t = Totals.Of(rows.Where(r => r.StartDateLocal.DayOfWeek == d));
            return new DataGroup(((int)d).ToString(CultureInfo.InvariantCulture), d.ToString(), t, total > 0 ? t.DistanceM / total : 0);
        })];
    }

    /// <summary>The rows exactly as shown (filtered and sorted), in storage units like the other CSV exports.</summary>
    public ExportFile Export(AllDataResult result, AllDataSource src)
    {
        var csv = new CsvBuilder();
        csv.Row("strava_id", "name", "sport_type", "start_date_local", "gear", "distance_m", "moving_time_s", "elapsed_time_s", "elevation_gain_m",
            "average_speed_mps", "average_heartrate", "max_heartrate", "average_watts", "weighted_average_watts", "kilojoules", "average_cadence",
            "relative_effort", "trainer", "commute", "source", "url");
        foreach (var r in result.Rows)
        {
            csv.Row(r.StravaId, r.Name, r.SportType, r.StartDateLocal, src.GearName(r.GearId), r.DistanceM, r.MovingTimeS, r.ElapsedTimeS, r.ElevationGainM,
                r.AvgSpeed, r.AvgHr, r.MaxHr, r.AvgWatts, r.WeightedAvgWatts, r.Kilojoules, r.AvgCadence,
                r.SufferScore, r.IsTrainer, r.IsCommute, r.FromArchive ? "archive" : "sync",
                r.FromArchive ? "" : $"https://www.strava.com/activities/{r.StravaId}");
        }
        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return new ExportFile($"strava-all-data-{stamp}.csv", csv.ToBytes(), result.Rows.Count);
    }
}
