using StravaReports.Core.Reports;

namespace StravaReports.Tests;

public class AllDataTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);

    private static ActivityRow Row(long id, string name, string sport, DateTime date, double km, int minutes, string? gear = null, double elev = 0,
        double? hr = null, double? watts = null, bool archive = false) => new()
    {
        StravaId = id, Name = name, SportType = sport, StartDateLocal = date, DistanceM = km * 1000, MovingTimeS = minutes * 60, ElapsedTimeS = minutes * 60 + 120,
        ElevationGainM = elev, AvgSpeed = km * 1000 / (minutes * 60.0), AvgHr = hr, AvgWatts = watts, GearId = gear, FromArchive = archive,
    };

    private static AllDataSource Source(ReportFilter? filter = null) => new(filter ?? new ReportFilter(),
    [
        Row(1, "Morning ride", "Ride", new DateTime(2024, 8, 20, 7, 0, 0), 40, 100, "trek", 500, 130, 180),   // before the window
        Row(2, "Trek loop", "Ride", new DateTime(2024, 9, 5, 7, 0, 0), 50, 120, "trek", 800, 140, 190),
        Row(3, "Evening ride", "Ride", new DateTime(2025, 3, 2, 18, 0, 0), 30, 75, "trek", 300, null, null),
        Row(4, "Road ride", "Ride", new DateTime(2025, 6, 7, 9, 0, 0), 70, 160, "road", 600, 135, 200),
        Row(5, "Park run", "Run", new DateTime(2025, 6, 8, 8, 0, 0), 5, 25, null, 40, 160),
        Row(6, "Old run", "Run", new DateTime(2019, 1, 5, 8, 0, 0), 8, 45, null, 60, archive: true),
    ],
    new Dictionary<string, string> { ["trek"] = "Trek Fuel EX", ["road"] = "Canyon Endurace" }, Today);

    [Fact]
    public void Gear_and_date_totals_answer_how_far_on_the_bike_since_a_date()
    {
        // The question that motivated the page: miles on the Trek since 1 September 2024.
        var src = Source();
        var onTrek = src with { Rows = src.Rows.Where(r => r.GearId == "trek" && r.Day >= new DateOnly(2024, 9, 1)).ToList() };
        var result = AllDataReport.Analyse(onTrek, new DataQuery());

        Assert.Equal(2, result.Summary.Count);
        Assert.Equal(80_000, result.Summary.DistanceM);
        Assert.Equal((120 + 75) * 60, result.Summary.MovingTimeS);
        Assert.Equal(1100, result.Summary.ElevationGainM);
        Assert.Equal(50_000, result.Summary.Longest!.DistanceM);
        Assert.Equal(2, result.ByGear.Single().Totals.Count);
        Assert.Equal("Trek Fuel EX", result.ByGear.Single().Label);
    }

    [Fact]
    public void Text_search_matches_name_sport_and_gear_and_needs_every_word()
    {
        var src = Source();
        Assert.Equal([2L], Ids(src, new DataQuery { Text = "loop" }));
        Assert.Equal([1L, 2L, 3L], Ids(src, new DataQuery { Text = "trek", Sort = DataSort.Date, Descending = false }));   // gear name
        Assert.Equal([2L], Ids(src, new DataQuery { Text = "TREK fuel loop" }));
        Assert.Empty(Ids(src, new DataQuery { Text = "trek canyon" }));
        Assert.Equal(2, AllDataReport.Analyse(src, new DataQuery { Text = "run" }).Rows.Count);                                    // sport "Run" and name "Park run"
    }

    [Fact]
    public void Range_day_and_data_filters_combine()
    {
        var src = Source();
        Assert.Equal([4L, 2L], Ids(src, new DataQuery { MinDistanceM = 45_000 }));
        Assert.Equal([5L], Ids(src, new DataQuery { MaxTimeS = 30 * 60, RequireHeartRate = true }));
        Assert.Equal([1L, 2L, 4L], Ids(src, new DataQuery { RequirePower = true, Sort = DataSort.Date, Descending = false }));
        Assert.Equal([5L], Ids(src, new DataQuery { Day = DayOfWeek.Sunday, MaxElevationM = 100 }));
        Assert.Equal([2L], Ids(src, new DataQuery { MinElevationM = 700, MaxElevationM = 900 }));
        Assert.True(new DataQuery { MinElevationM = 1 }.IsFiltered);
        Assert.False(new DataQuery { Sort = DataSort.Distance }.IsFiltered);
    }

    [Fact]
    public void Sorting_handles_missing_values_and_ties()
    {
        var src = Source();
        // Highest heart rate first; rows without one come last, newest of those first.
        Assert.Equal([5L, 2L, 4L, 1L, 3L, 6L], Ids(src, new DataQuery { Sort = DataSort.AvgHr }));
        Assert.Equal([6L, 1L, 2L, 3L, 4L, 5L], Ids(src, new DataQuery { Sort = DataSort.Date, Descending = false }));
        Assert.Equal(4, Ids(src, new DataQuery { Sort = DataSort.Distance })[0]);
        Assert.Equal(["Canyon Endurace", "Trek Fuel EX"], AllDataReport.Analyse(src, new DataQuery { Sort = DataSort.Gear, Descending = false }).Rows
            .Select(r => src.GearName(r.GearId)).Where(n => n != "").Distinct().ToArray());
    }

    [Fact]
    public void Summary_has_weighted_averages_per_week_rates_and_breakdowns()
    {
        var src = Source(new ReportFilter { From = new DateOnly(2025, 6, 1), To = new DateOnly(2025, 6, 30) });
        src = src with { Rows = src.Rows.Where(r => r.Day >= new DateOnly(2025, 6, 1) && r.Day <= new DateOnly(2025, 6, 30)).ToList() };
        var r = AllDataReport.Analyse(src, new DataQuery());
        var s = r.Summary;

        Assert.Equal(2, s.Count);
        Assert.Equal(2, s.ActiveDays);
        Assert.Equal(30, s.PeriodDays);                        // the period asked for, not just the days used
        Assert.Equal(75_000, s.DistanceM);
        Assert.Equal(75_000.0 / (185 * 60), s.AvgSpeedMps!.Value, 6);
        Assert.Equal(75_000.0 / (30 / 7.0), s.DistancePerWeekM, 3);
        Assert.Equal((135.0 * 160 + 160.0 * 25) / 185, s.AvgHr!.Value, 6);          // weighted by moving time
        Assert.Null(s.MaxHr);                                   // neither row has a max heart rate
        Assert.Equal(200, s.AvgWatts);                          // only the ride has power
        Assert.Equal(4, s.Longest!.StravaId);
        Assert.Equal(4, s.Fastest!.StravaId);
        Assert.Equal(["Ride", "Run"], r.BySport.Select(g => g.Label).ToArray());       // biggest distance first
        Assert.Equal(70.0 / 75, r.BySport[0].Share, 6);
        Assert.Equal("Jun 2025", Assert.Single(r.ByMonth).Label);
        Assert.Equal("2025", Assert.Single(r.ByYear).Label);
        Assert.Equal(7, r.ByWeekday.Count);
        Assert.Equal(1, r.ByWeekday.Single(g => g.Label == "Saturday").Totals.Count);
        Assert.Equal(1, r.ByWeekday.Single(g => g.Label == "Sunday").Totals.Count);
    }

    [Fact]
    public void Empty_results_do_not_throw_and_archive_rows_are_counted()
    {
        var src = Source();
        var none = AllDataReport.Analyse(src, new DataQuery { Text = "nothing matches this" });
        Assert.Equal(0, none.Summary.Count);
        Assert.Empty(none.Rows);
        Assert.Equal(6, none.LoadedCount);
        Assert.Equal(0, none.Summary.DistancePerWeekM);

        var all = AllDataReport.Analyse(src, new DataQuery());
        Assert.Equal(1, all.Summary.ArchiveCount);
        Assert.Equal(3, all.ByGear.Count);                       // trek, road and "No gear"
        Assert.Contains(all.ByGear, g => g.Label == "No gear" && g.Totals.Count == 2);
    }

    [Fact]
    public void Export_writes_exactly_the_filtered_sorted_rows_in_storage_units()
    {
        var src = Source();
        var report = new AllDataReport(null!, TimeProvider.System);
        var result = AllDataReport.Analyse(src, new DataQuery { Text = "trek", Sort = DataSort.Distance });
        var file = report.Export(result, src);
        var lines = System.Text.Encoding.UTF8.GetString(file.Content).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, file.Rows);
        Assert.Equal(4, lines.Length);                          // header + 3
        Assert.StartsWith("2,Trek loop,Ride,2024-09-05 07:00:00,Trek Fuel EX,50000,7200,", lines[1]);
        Assert.EndsWith("sync,https://www.strava.com/activities/2", lines[1]);
        Assert.Matches(@"^strava-all-data-\d{8}-\d{4}\.csv$", file.FileName);
    }

    private static long[] Ids(AllDataSource src, DataQuery q) => AllDataReport.Analyse(src, q).Rows.Select(r => r.StravaId).ToArray();
}
