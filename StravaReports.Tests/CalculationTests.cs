using StravaReports.Core.Reports;
using StravaReports.Core.Settings;

namespace StravaReports.Tests;

public class CalculationTests
{
    [Theory]
    [InlineData("2026-10-16", DayOfWeek.Monday, "2026-10-12")] // Friday -> Monday
    [InlineData("2026-10-12", DayOfWeek.Monday, "2026-10-12")] // Monday stays
    [InlineData("2026-10-18", DayOfWeek.Monday, "2026-10-12")] // Sunday belongs to the Monday week
    [InlineData("2026-10-18", DayOfWeek.Sunday, "2026-10-18")]
    [InlineData("2026-10-16", DayOfWeek.Sunday, "2026-10-11")]
    public void StartOfWeek_respects_week_start(string day, DayOfWeek start, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), Calc.StartOfWeek(DateOnly.Parse(day), start));

    [Fact]
    public void PeriodRange_includes_empty_periods()
    {
        var weeks = Calc.PeriodRange(new DateOnly(2026, 1, 7), new DateOnly(2026, 1, 28), Grouping.Week, DayOfWeek.Monday);
        Assert.Equal([new(2026, 1, 5), new(2026, 1, 12), new(2026, 1, 19), new(2026, 1, 26)], weeks);

        var months = Calc.PeriodRange(new DateOnly(2025, 11, 20), new DateOnly(2026, 2, 1), Grouping.Month, DayOfWeek.Monday);
        Assert.Equal(4, months.Count);
    }

    [Fact]
    public void Daily_streaks()
    {
        var today = new DateOnly(2026, 10, 16);
        var days = new[] { today, today.AddDays(-1), today.AddDays(-2), today.AddDays(-5), today.AddDays(-6), today.AddDays(-7), today.AddDays(-8) };

        Assert.Equal(3, Calc.CurrentDailyStreak(days, today));
        Assert.Equal(4, Calc.LongestDailyStreak(days));
        // A streak survives until today is over.
        Assert.Equal(3, Calc.CurrentDailyStreak(days.Where(d => d != today.AddDays(-0)).Append(today.AddDays(-3)), today));
        Assert.Equal(0, Calc.CurrentDailyStreak([today.AddDays(-2)], today));
    }

    [Fact]
    public void Weekly_streaks()
    {
        var today = new DateOnly(2026, 10, 16); // week of Mon 12 Oct
        var days = new[] { new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 6), new DateOnly(2026, 9, 29), new DateOnly(2026, 9, 15) };
        Assert.Equal(3, Calc.CurrentWeeklyStreak(days, today, DayOfWeek.Monday));
        Assert.Equal(3, Calc.LongestWeeklyStreak(days, DayOfWeek.Monday));
    }

    [Fact]
    public void Pace_and_units()
    {
        // 10 km in 50 min = 5:00 /km
        Assert.Equal(300, Calc.PaceSecondsPerUnit(10_000, 3000, UnitSystem.Metric));
        Assert.Equal("5:00 /km", Units.FormatPace(300, UnitSystem.Metric));
        Assert.Equal("8:03 /mi", Units.FormatPace(Calc.PaceSecondsPerUnit(10_000, 3000, UnitSystem.Imperial), UnitSystem.Imperial));
        Assert.Null(Calc.PaceSecondsPerUnit(0, 100, UnitSystem.Metric));

        Assert.Equal(36, Units.SpeedValue(10, UnitSystem.Metric), 6);
        Assert.Equal(22.369, Units.SpeedValue(10, UnitSystem.Imperial), 3);
        Assert.Equal(1, Units.DistanceValue(1609.344, UnitSystem.Imperial), 6);
        Assert.Equal("1:02:03", Units.FormatClock(3723));
        Assert.Equal("1h 02m", Units.FormatDuration(3723));
    }

    [Theory]
    [InlineData("Run", SportGroup.Run)]
    [InlineData("TrailRun", SportGroup.Run)]
    [InlineData("VirtualRide", SportGroup.Ride)]
    [InlineData("GravelRide", SportGroup.Ride)]
    [InlineData("Swim", SportGroup.Other)]
    public void Sport_classification(string sport, SportGroup expected) => Assert.Equal(expected, SportGroups.Classify(sport));
}
