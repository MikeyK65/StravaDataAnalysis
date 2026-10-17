using System.Text;
using StravaReports.Core.Export;
using StravaReports.Core.Reports;
using StravaReports.Core.Settings;
using StravaReports.Core.Sync;

namespace StravaReports.Tests;

public class AdvancedReportTests
{
    private static readonly DateTime Day = new(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
    private static readonly ReportFilter AllTime = new();

    private static async Task<AppSettingsModel> ConfigureAsync(TestHost host, Func<AppSettingsModel, AppSettingsModel> change)
    {
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync(change(await settings.GetAsync()));
        return await settings.GetAsync();
    }

    [Fact]
    public async Task Records_report_personal_bests_and_the_pb_timeline()
    {
        await using var host = await TestHost.CreateAsync();
        host.Strava.Activities.AddRange(
        [
            new FakeActivity(1, Day.AddDays(-20)), new FakeActivity(2, Day.AddDays(-10)), new FakeActivity(3, Day),
            new FakeActivity(4, Day.AddDays(-5), "Ride", 60_000, 7200),
        ]);
        var times = new Dictionary<long, int> { [1] = 1500, [2] = 1450, [3] = 1480 };
        host.Strava.FiveKTime = a => times.GetValueOrDefault(a.Id, 1400);
        var s = await ConfigureAsync(host, x => x with { FetchStreams = false });
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        var r = await host.Get<RecordsReport>().RunAsync(AllTime, s);

        var fiveK = Assert.Single(r.Bests);
        Assert.Equal((1450, 2L, 3), (fiveK.ElapsedTimeS, fiveK.StravaId, fiveK.Count));
        Assert.Equal(new[] { 1500, 1450 }, r.Timeline.Select(t => t.ElapsedTimeS));
        Assert.Equal(0, r.RunsWithoutDetail);
        Assert.Equal(60_000, r.Highlights.Single(h => h.Label == "Longest ride").Value);
        Assert.Equal(10_000, r.Highlights.Single(h => h.Label == "Longest run").Value);
    }

    [Fact]
    public async Task Hr_zone_times_from_streams_sum_to_moving_time()
    {
        await using var host = await TestHost.CreateAsync();
        host.Strava.Activities.Add(new FakeActivity(1, Day, MovingTime: 3000));
        var s = await ConfigureAsync(host, x => x with { FetchDetail = false, FetchStreams = true, MaxHr = 200 });
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        var r = await host.Get<HrZonesReport>().RunAsync(AllTime, s);

        Assert.Equal(1, r.FromStreams);
        Assert.Equal(3000, r.TotalSeconds.Sum(), 6);
        Assert.Equal(3000, r.TotalSeconds[1], 6);   // the fake stream is 121–122 bpm: zone 2 of 120/140/160/180
        Assert.Equal(3000, r.Weeks.Sum(w => w.Seconds.Sum()), 6);

        var efficiency = await host.Get<RunningEfficiencyReport>().RunAsync(AllTime, s);
        Assert.Equal(3000, efficiency.ZoneSeconds.Sum(), 6);
        Assert.Equal(10_000 / 50.0 / 150, efficiency.Runs.Single().EfficiencyFactor, 6);
    }

    [Fact]
    public async Task Power_curve_ftp_estimate_and_training_load_from_power_streams()
    {
        await using var host = await TestHost.CreateAsync();
        host.Strava.Activities.Add(new FakeActivity(7, Day, "Ride", 36_000, 1300, Watts: 250));
        host.Strava.Activities.Add(new FakeActivity(8, Day.AddDays(-1), "Run", 10_000, 3000));
        host.Strava.StreamsFor = id => id == 7
            ? new { time = new { data = Enumerable.Range(0, 1300).ToArray() }, watts = new { data = Enumerable.Repeat(250, 1300).ToArray() } }
            : new { time = new { data = new[] { 0, 1, 2 } }, heartrate = new { data = new[] { 150, 150, 150 } } };
        var s = await ConfigureAsync(host, x => x with { FetchDetail = false, FetchStreams = true, Ftp = 250, MaxHr = 190, RestingHr = 50 });
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        var power = await host.Get<PowerReport>().RunAsync(AllTime, s);
        var ride = Assert.Single(power.Rides);
        Assert.Equal(250, ride.NormalizedPower!.Value, 6);
        Assert.Equal(1300 / 3600.0 * 100, ride.Tss!.Value, 6);
        Assert.Equal(250, power.Curve.Single(c => c.Seconds == 1200).Watts, 6);
        Assert.Equal(237.5, power.CurrentFtpEstimate!.Value, 6);
        Assert.Equal(1, power.RidesWithStreams);

        var load = await host.Get<TrainingLoadReport>().RunAsync(AllTime, s);
        Assert.Equal(1, load.Methods[LoadMethod.Power]);
        Assert.Equal(1, load.Methods[LoadMethod.HeartRate]);
        Assert.True(load.Latest!.Fitness > 0);
        Assert.Equal(ride.Tss!.Value, load.Activities.Single(a => a.Method == LoadMethod.Power).Load, 6);
    }

    [Fact]
    public async Task Csv_exports_activities_and_totals()
    {
        await using var host = await TestHost.CreateAsync();
        host.Strava.Activities.Add(new FakeActivity(1, Day));
        host.Strava.Activities.Add(new FakeActivity(2, Day.AddDays(-1), "Ride", 40_000, 4800));
        var s = await ConfigureAsync(host, x => x with { FetchDetail = false });
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);
        var export = host.Get<CsvExportService>();

        var activities = await export.ExportAsync(ExportKind.Activities, AllTime with { Group = SportGroup.Run }, s);
        var lines = Encoding.UTF8.GetString(activities.Content).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, activities.Rows);
        Assert.StartsWith("strava_id,name,sport_type", lines[0]);
        Assert.StartsWith("1,Activity 1,Run,", lines[1]);
        Assert.EndsWith(".csv", activities.FileName);

        foreach (var (kind, _, _, _) in CsvExportService.Catalogue)
            Assert.NotEmpty((await export.ExportAsync(kind, AllTime, s)).Content);
    }
}

public class EmptyDatabaseReportTests
{
    [Fact]
    public async Task Every_new_report_handles_an_empty_database()
    {
        await using var host = await TestHost.CreateAsync();
        var s = await host.Get<AppSettingsService>().GetAsync();
        var f = new ReportFilter();
        Assert.Empty((await host.Get<RecordsReport>().RunAsync(f, s)).Highlights);
        Assert.Null((await host.Get<HrZonesReport>().RunAsync(f, s)).Zones);
        Assert.Null((await host.Get<TrainingLoadReport>().RunAsync(f, s)).Latest);
        Assert.Empty((await host.Get<PowerReport>().RunAsync(f, s)).Rides);
        Assert.Empty((await host.Get<RunningEfficiencyReport>().RunAsync(f, s)).Runs);
        Assert.Equal(0, (await host.Get<StravaReports.Core.Archive.ArchiveService>().SummaryAsync()).Activities);
    }
}
