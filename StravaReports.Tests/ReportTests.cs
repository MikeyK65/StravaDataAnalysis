using StravaReports.Core.Reports;
using StravaReports.Core.Settings;
using StravaReports.Core.Sync;

namespace StravaReports.Tests;

public class ReportTests
{
    [Fact]
    public async Task Volume_and_running_reports_aggregate_by_week_with_filters()
    {
        await using var host = await TestHost.CreateAsync();
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { FetchDetail = false });

        // Two runs in the week of Mon 5 Oct 2026, one ride in the week of 12 Oct, one run a year earlier.
        host.Strava.Activities.AddRange(
        [
            new FakeActivity(1, new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc), "Run", 10_000, 3000),
            new FakeActivity(2, new DateTime(2026, 10, 7, 7, 0, 0, DateTimeKind.Utc), "TrailRun", 5_000, 1800),
            new FakeActivity(3, new DateTime(2026, 10, 13, 7, 0, 0, DateTimeKind.Utc), "Ride", 40_000, 4800),
            new FakeActivity(4, new DateTime(2025, 10, 6, 7, 0, 0, DateTimeKind.Utc), "Run", 8_000, 2400),
        ]);
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);
        var s = await settings.GetAsync();
        var filter = new ReportFilter { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 16) };

        var volume = await host.Get<VolumeTrendsReport>().RunAsync(filter, Grouping.Week, s);
        Assert.Equal(3, volume.Total.Count);
        var week5 = volume.Periods.Single(p => p.PeriodStart == new DateOnly(2026, 10, 5));
        Assert.Equal(15_000, week5.Totals.DistanceM);
        Assert.Equal(new DateOnly(2026, 9, 28), volume.Periods[0].PeriodStart);
        Assert.Contains(volume.YearOverlay, y => y.Year == 2025);

        var running = await host.Get<RunningReport>().RunAsync(filter, s);
        Assert.Equal(2, running.Total.Count);
        Assert.Equal(10_000, running.Weeks.Single(w => w.Week == new DateOnly(2026, 10, 5)).LongestRunM);
        Assert.Equal(1, running.Bands.Single(b => b.Label == "5–10K").Count);   // 5 km counts as 5–10K
        Assert.Equal(1, running.Bands.Single(b => b.Label == "10–15K").Count);

        var cycling = await host.Get<CyclingReport>().RunAsync(filter, s);
        Assert.Single(cycling.Rides);

        var noRuns = await host.Get<VolumeTrendsReport>().RunAsync(filter with { Group = SportGroup.Ride }, Grouping.Month, s);
        Assert.Equal(40_000, noRuns.Total.DistanceM);

        var dash = await host.Get<DashboardReport>().RunAsync(new ReportFilter(), s, new DateOnly(2026, 10, 16));
        Assert.Equal(1, dash.Week.Count);   // the ride on 13 Oct
        Assert.Equal(3, dash.Month.Count);
        Assert.Equal(4, dash.AllTime.Count);
        Assert.Equal(3L, dash.Latest!.StravaId);
    }
}
