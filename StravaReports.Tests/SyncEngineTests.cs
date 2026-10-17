using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Settings;
using StravaReports.Core.Sync;

namespace StravaReports.Tests;

public class SyncEngineTests
{
    private static readonly DateTime Newest = new(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc);

    private static async Task DisableDetailAsync(TestHost host)
    {
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { FetchDetail = false, FetchStreams = false });
    }

    [Fact]
    public async Task Full_backfill_stores_every_activity_once()
    {
        await using var host = await TestHost.CreateAsync();
        await DisableDetailAsync(host);
        host.AddActivities(450, Newest, TimeSpan.FromDays(1));

        var result = await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        await using var db = await host.DbAsync();
        Assert.Equal(450, await db.Activities.CountAsync());
        var state = await db.SyncStates.SingleAsync();
        Assert.True(state.BackfillComplete);
        Assert.Equal(FakeStravaHandler.Epoch(Newest), state.NewestActivityEpoch);
        Assert.Equal(100, state.ShortLimit);
        Assert.Contains(await db.Gear.Select(g => g.StravaId).ToListAsync(), id => id == "b1");
        // Raw JSON is retained for every activity.
        Assert.DoesNotContain(await db.Activities.Select(a => a.RawJson).ToListAsync(), string.IsNullOrEmpty);
    }

    [Fact]
    public async Task Backfill_pauses_at_the_rate_limit_and_resumes_without_duplicates()
    {
        await using var host = await TestHost.CreateAsync();
        await DisableDetailAsync(host);
        host.AddActivities(450, Newest, TimeSpan.FromDays(1));
        // Leaves room for: athlete + 1 activity page, then the tracker's reserve stops the run.
        host.Strava.ShortUsage = 95;

        var engine = host.Get<SyncEngine>();
        var first = await engine.RunAsync(SyncTrigger.Manual);

        Assert.Equal(SyncOutcome.Paused, first.Outcome);
        Assert.Equal(new DateTime(2026, 10, 16, 12, 15, 0, DateTimeKind.Utc), first.ResumeAfterUtc);
        await using (var db = await host.DbAsync())
        {
            Assert.Equal(200, await db.Activities.CountAsync());
            var state = await db.SyncStates.SingleAsync();
            Assert.False(state.BackfillComplete);
            Assert.NotNull(state.BackfillCursorEpoch);
        }

        // Still inside the window: refuses to run.
        var blocked = await engine.RunAsync(SyncTrigger.Scheduled);
        Assert.Equal(SyncOutcome.Paused, blocked.Outcome);

        // Next window: counters reset and the backfill resumes from its cursor.
        host.Time.SetUtcNow(new DateTimeOffset(2026, 10, 16, 12, 15, 5, TimeSpan.Zero));
        host.Strava.ShortUsage = 0;
        var second = await engine.RunAsync(SyncTrigger.Scheduled);

        Assert.Equal(SyncOutcome.Completed, second.Outcome);
        await using (var db = await host.DbAsync())
        {
            Assert.Equal(450, await db.Activities.CountAsync());
            Assert.Equal(450, await db.Activities.Select(a => a.StravaId).Distinct().CountAsync());
            Assert.True((await db.SyncStates.SingleAsync()).BackfillComplete);
        }
    }

    [Fact]
    public async Task Incremental_sync_fetches_only_new_activities()
    {
        await using var host = await TestHost.CreateAsync();
        await DisableDetailAsync(host);
        host.AddActivities(10, Newest, TimeSpan.FromDays(1));
        var engine = host.Get<SyncEngine>();
        await engine.RunAsync(SyncTrigger.Manual);

        host.Strava.Activities.Add(new FakeActivity(9999, Newest.AddDays(1), "Ride", 40_000, 4800));
        while (host.Strava.Requests.TryDequeue(out _)) { }

        var result = await engine.RunAsync(SyncTrigger.Manual);

        Assert.Equal(1, result.ActivitiesUpserted);
        Assert.Contains(host.Strava.Requests, r => r.Contains($"after={FakeStravaHandler.Epoch(Newest)}"));
        await using var db = await host.DbAsync();
        Assert.Equal(11, await db.Activities.CountAsync());
        Assert.Equal("Ride", (await db.Activities.SingleAsync(a => a.StravaId == 9999)).SportType);
    }

    [Fact]
    public async Task Detail_queue_fetches_laps_and_removes_deleted_activities()
    {
        await using var host = await TestHost.CreateAsync();
        host.AddActivities(3, Newest, TimeSpan.FromDays(1));
        host.Strava.Activities[0] = host.Strava.Activities[0] with { GearId = "g99" };
        host.Strava.DeletedDetail.Add(1002);

        var result = await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.JobsProcessed);
        await using var db = await host.DbAsync();
        Assert.Equal(2, await db.Activities.CountAsync());
        var withLaps = await db.Activities.Include(a => a.Laps).Include(a => a.BestEfforts).SingleAsync(a => a.StravaId == 1000);
        Assert.Equal(2, withLaps.Laps.Count);
        Assert.Single(withLaps.BestEfforts);
        Assert.NotNull(withLaps.DetailFetchedAtUtc);
        Assert.True((await db.Gear.SingleAsync(g => g.StravaId == "g99")).IsRetired);
        Assert.Equal(0, await db.SyncJobs.CountAsync(j => j.Status == SyncJobStatus.Pending));
    }

    [Fact]
    public async Task Streams_are_stored_compressed()
    {
        await using var host = await TestHost.CreateAsync();
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { FetchDetail = false, FetchStreams = true });
        host.AddActivities(1, Newest, TimeSpan.FromDays(1));

        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        await using var db = await host.DbAsync();
        var hr = await db.ActivityStreams.SingleAsync(s => s.StreamType == "heartrate");
        Assert.Equal(3, hr.PointCount);
        Assert.Equal("[120,121,122]", SyncEngine.Decompress(hr.Data));
    }

    [Fact]
    public async Task Not_connected_reports_cleanly()
    {
        await using var host = await TestHost.CreateAsync(connected: false);
        var result = await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);
        Assert.Equal(SyncOutcome.NotConnected, result.Outcome);
        Assert.Empty(host.Strava.Requests);
    }

    [Fact]
    public async Task Delete_all_data_empties_activity_tables_but_keeps_settings()
    {
        await using var host = await TestHost.CreateAsync();
        await DisableDetailAsync(host);
        host.AddActivities(5, Newest, TimeSpan.FromDays(1));
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        await host.Get<DataManagementService>().DeleteAllDataAsync(includeSettings: false);

        await using var db = await host.DbAsync();
        Assert.Equal(0, await db.Activities.CountAsync());
        Assert.Equal(0, await db.Gear.CountAsync());
        Assert.False((await host.Get<AppSettingsService>().GetAsync()).FetchDetail);
    }
}
