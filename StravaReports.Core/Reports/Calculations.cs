using StravaReports.Core.Settings;

namespace StravaReports.Core.Reports;

public enum Grouping { Week, Month, Year }

public record Totals(int Count, double DistanceM, int MovingTimeS, double ElevationGainM)
{
    public static readonly Totals Empty = new(0, 0, 0, 0);

    public static Totals Of(IEnumerable<ActivityRow> rows)
    {
        int c = 0, t = 0; double d = 0, e = 0;
        foreach (var r in rows) { c++; d += r.DistanceM; t += r.MovingTimeS; e += r.ElevationGainM; }
        return new Totals(c, d, t, e);
    }
}

/// <summary>Pure calculations, kept free of EF so they are easy to unit test.</summary>
public static class Calc
{
    public static DateOnly StartOfWeek(DateOnly day, DayOfWeek weekStart)
    {
        var diff = ((int)day.DayOfWeek - (int)weekStart + 7) % 7;
        return day.AddDays(-diff);
    }

    public static DateOnly PeriodStart(DateOnly day, Grouping g, DayOfWeek weekStart) => g switch
    {
        Grouping.Week => StartOfWeek(day, weekStart),
        Grouping.Month => new DateOnly(day.Year, day.Month, 1),
        _ => new DateOnly(day.Year, 1, 1),
    };

    public static DateOnly NextPeriod(DateOnly start, Grouping g) => g switch
    {
        Grouping.Week => start.AddDays(7),
        Grouping.Month => start.AddMonths(1),
        _ => start.AddYears(1),
    };

    /// <summary>All period starts from the first to the last, inclusive, so charts show empty periods as zero.</summary>
    public static List<DateOnly> PeriodRange(DateOnly from, DateOnly to, Grouping g, DayOfWeek weekStart)
    {
        var list = new List<DateOnly>();
        for (var p = PeriodStart(from, g, weekStart); p <= to; p = NextPeriod(p, g)) list.Add(p);
        return list;
    }

    /// <summary>Consecutive active days ending today (or yesterday, so a streak survives until the day is over).</summary>
    public static int CurrentDailyStreak(IEnumerable<DateOnly> activeDays, DateOnly today)
    {
        var set = activeDays as ISet<DateOnly> ?? activeDays.ToHashSet();
        var day = set.Contains(today) ? today : today.AddDays(-1);
        var n = 0;
        while (set.Contains(day)) { n++; day = day.AddDays(-1); }
        return n;
    }

    public static int LongestDailyStreak(IEnumerable<DateOnly> activeDays)
    {
        var sorted = activeDays.Distinct().Order().ToList();
        int best = 0, run = 0;
        DateOnly? prev = null;
        foreach (var d in sorted)
        {
            run = prev is { } p && p.AddDays(1) == d ? run + 1 : 1;
            best = Math.Max(best, run);
            prev = d;
        }
        return best;
    }

    /// <summary>Consecutive weeks with at least one activity, ending with the current or previous week.</summary>
    public static int CurrentWeeklyStreak(IEnumerable<DateOnly> activeDays, DateOnly today, DayOfWeek weekStart)
    {
        var weeks = activeDays.Select(d => StartOfWeek(d, weekStart)).ToHashSet();
        var week = StartOfWeek(today, weekStart);
        if (!weeks.Contains(week)) week = week.AddDays(-7);
        var n = 0;
        while (weeks.Contains(week)) { n++; week = week.AddDays(-7); }
        return n;
    }

    public static int LongestWeeklyStreak(IEnumerable<DateOnly> activeDays, DayOfWeek weekStart)
    {
        var weeks = activeDays.Select(d => StartOfWeek(d, weekStart)).Distinct().Order().ToList();
        int best = 0, run = 0;
        DateOnly? prev = null;
        foreach (var w in weeks)
        {
            run = prev is { } p && p.AddDays(7) == w ? run + 1 : 1;
            best = Math.Max(best, run);
            prev = w;
        }
        return best;
    }

    /// <summary>Seconds per metre-based unit (km or mile). Null when there is no distance.</summary>
    public static double? PaceSecondsPerUnit(double distanceM, int movingTimeS, UnitSystem units)
    {
        if (distanceM <= 0 || movingTimeS <= 0) return null;
        return movingTimeS / Units.DistanceValue(distanceM, units);
    }

    public static double? AvgSpeedMps(double distanceM, int movingTimeS) =>
        movingTimeS > 0 ? distanceM / movingTimeS : null;
}

public static class Units
{
    public const double MetresPerMile = 1609.344;
    public const double FeetPerMetre = 3.28084;

    public static string DistanceUnit(UnitSystem u) => u == UnitSystem.Imperial ? "mi" : "km";
    public static string ElevationUnit(UnitSystem u) => u == UnitSystem.Imperial ? "ft" : "m";
    public static string SpeedUnit(UnitSystem u) => u == UnitSystem.Imperial ? "mph" : "km/h";
    public static string PaceUnit(UnitSystem u) => u == UnitSystem.Imperial ? "/mi" : "/km";

    public static double DistanceValue(double metres, UnitSystem u) => u == UnitSystem.Imperial ? metres / MetresPerMile : metres / 1000.0;
    public static double ElevationValue(double metres, UnitSystem u) => u == UnitSystem.Imperial ? metres * FeetPerMetre : metres;
    public static double SpeedValue(double mps, UnitSystem u) => u == UnitSystem.Imperial ? mps * 3600 / MetresPerMile : mps * 3.6;

    public static string FormatDistance(double metres, UnitSystem u, int decimals = 1) =>
        $"{DistanceValue(metres, u).ToString($"N{decimals}")} {DistanceUnit(u)}";

    public static string FormatElevation(double metres, UnitSystem u) => $"{ElevationValue(metres, u):N0} {ElevationUnit(u)}";

    public static string FormatSpeed(double mps, UnitSystem u) => $"{SpeedValue(mps, u):N1} {SpeedUnit(u)}";

    public static string FormatPace(double? secondsPerUnit, UnitSystem u)
    {
        if (secondsPerUnit is not { } s || double.IsInfinity(s)) return "–";
        var total = (int)Math.Round(s);
        return $"{total / 60}:{total % 60:00} {PaceUnit(u)}";
    }

    public static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    public static string FormatClock(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
