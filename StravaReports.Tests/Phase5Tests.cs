using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Analysis;
using StravaReports.Core.Archive;
using StravaReports.Core.Backup;
using StravaReports.Core.Data;
using StravaReports.Core.Reports;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;
using StravaReports.Core.Sync;

namespace StravaReports.Tests;

public class RouteTests
{
    [Fact]
    public void Decodes_the_reference_polyline()
    {
        // Google's documented example.
        var points = Polyline.Decode("_p~iF~ps|U_ulLnnqC_mqNvxq`@");
        Assert.Equal(3, points.Count);
        Assert.Equal(new LatLng(38.5, -120.2), points[0]);
        Assert.Equal(new LatLng(40.7, -120.95), points[1]);
        Assert.Equal(new LatLng(43.252, -126.453), points[2]);
        Assert.Empty(Polyline.Decode(null));
        Assert.Single(Polyline.Decode("_p~iF~ps|U_ulL")); // truncated input stops cleanly
    }

    [Fact]
    public void Fits_a_route_into_the_viewport_and_lists_covering_tiles()
    {
        LatLng[] route = [new(51.50, -0.15), new(51.52, -0.10), new(51.48, -0.05)];
        var view = MapLayout.Fit(route, 800, 480);
        Assert.InRange(view.Zoom, 10, 15);
        foreach (var p in route)
        {
            var (x, y) = MapLayout.ToView(p, view);
            Assert.InRange(x, 0, 800);
            Assert.InRange(y, 0, 480);
        }
        // One zoom level more would not fit.
        var tighter = MapLayout.Fit(route, 400, 240);
        Assert.Equal(view.Zoom - 1, tighter.Zoom);

        var tiles = MapLayout.Tiles(view).ToList();
        Assert.InRange(tiles.Count, 6, 20);
        Assert.Equal("https://t/12/2046/1362.png", MapLayout.TileUrl("https://t/{z}/{x}/{y}.png", 12, 2046, 1362));
        Assert.StartsWith("M", MapLayout.ToPath(route, view));
        Assert.Equal(2, MapLayout.ToPath(route, view, maxPoints: 1).Count(c => c is 'M' or 'L'));  // first and last kept
    }

    [Fact]
    public async Task Routes_report_groups_activities_by_area()
    {
        await using var host = await TestHost.CreateAsync();
        var london1 = Polyline.Encode([new(51.50, -0.12), new(51.51, -0.10)]);
        var london2 = Polyline.Encode([new(51.45, -0.20), new(51.46, -0.18)]);
        var paris = Polyline.Encode([new(48.85, 2.35), new(48.86, 2.36)]);
        var day = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
        host.Strava.Activities.AddRange(
        [
            new FakeActivity(1, day, Polyline: london1), new FakeActivity(2, day.AddDays(-1), Polyline: london2),
            new FakeActivity(3, day.AddDays(-2), Polyline: paris), new FakeActivity(4, day.AddDays(-3), "VirtualRide"),
        ]);
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { FetchDetail = false });
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);

        var r = await host.Get<RoutesReport>().RunAsync(new ReportFilter());
        Assert.Equal(2, r.Regions.Count);
        Assert.Equal(2, r.Selected!.Count);
        Assert.Equal(new long[] { 1, 2 }, r.Tracks.Select(t => t.StravaId).Order());
        Assert.Equal(1, r.WithoutRoute);

        var parisOnly = await host.Get<RoutesReport>().RunAsync(new ReportFilter(), r.Regions[1].Key);
        Assert.Equal(3, parisOnly.Tracks.Single().StravaId);
    }
}

public class WebhookTests
{
    private static async Task<TestHost> SubscribedHostAsync()
    {
        var host = await TestHost.CreateAsync();
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { ClientId = "123", FetchStreams = false });
        await host.Get<ISecretStore>().SetAsync(SecretKeys.ClientSecret, "not-a-real-secret");
        host.AddActivities(3, new DateTime(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc), TimeSpan.FromDays(1));
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);   // athlete 42 and activities 1000-1002
        await host.Get<StravaWebhookService>().SubscribeAsync("https://reports.example.com/webhooks/strava");
        return host;
    }

    private static StravaWebhookPayload Event(string type, long id, string aspect, long owner = 42, long sub = 777, string? updates = null) => new()
    {
        ObjectType = type, ObjectId = id, AspectType = aspect, OwnerId = owner, SubscriptionId = sub, EventTime = 1,
        Updates = updates is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updates),
    };

    [Fact]
    public async Task Subscribing_stores_a_verify_token_that_answers_the_challenge()
    {
        await using var host = await SubscribedHostAsync();
        var hooks = host.Get<StravaWebhookService>();
        var token = host.Strava.LastSubscriptionForm["verify_token"];

        Assert.Equal("https://reports.example.com/webhooks/strava", host.Strava.LastSubscriptionForm["callback_url"]);
        Assert.Equal("abc", await hooks.ValidateChallengeAsync("subscribe", token, "abc"));
        Assert.Null(await hooks.ValidateChallengeAsync("subscribe", "wrong", "abc"));
        Assert.Null(await hooks.ValidateChallengeAsync("unsubscribe", token, "abc"));
        var status = await hooks.GetStatusAsync();
        Assert.Equal((true, 777L), (status.Subscribed, status.SubscriptionId));

        // Subscribing again adopts the existing subscription for the same URL.
        Assert.Equal(777, await hooks.SubscribeAsync("https://reports.example.com/webhooks/strava"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hooks.SubscribeAsync("https://elsewhere.example.com/webhooks/strava"));

        await hooks.UnsubscribeAsync();
        Assert.False((await hooks.GetStatusAsync()).Subscribed);
        Assert.Empty(host.Strava.Subscriptions);
        Assert.Null(await hooks.ValidateChallengeAsync("subscribe", token, "abc"));
    }

    [Fact]
    public async Task Events_are_filtered_then_confirmed_with_the_api_before_changing_anything()
    {
        await using var host = await SubscribedHostAsync();
        var hooks = host.Get<StravaWebhookService>();
        host.Strava.Activities.Add(new FakeActivity(5000, new DateTime(2025, 1, 1, 7, 0, 0, DateTimeKind.Utc)));  // old activity uploaded late
        host.Strava.DeletedDetail.Add(1001);

        Assert.True(await hooks.AcceptAsync(Event("activity", 5000, "create")));
        Assert.True(await hooks.AcceptAsync(Event("activity", 1001, "delete")));
        Assert.True(await hooks.AcceptAsync(Event("activity", 1000, "delete")));   // forged: still exists on Strava
        Assert.False(await hooks.AcceptAsync(Event("activity", 1002, "delete", sub: 1)));
        Assert.False(await hooks.AcceptAsync(Event("activity", 1002, "delete", owner: 99)));
        Assert.False(await hooks.AcceptAsync(Event("club", 1, "create")));

        var result = await host.Get<SyncEngine>().RunAsync(SyncTrigger.Webhook);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        await using var db = await host.DbAsync();
        var ids = await db.Activities.Select(a => a.StravaId).ToListAsync();
        Assert.Contains(5000L, ids);          // added, even though it is older than the incremental cursor
        Assert.DoesNotContain(1001L, ids);    // confirmed deleted
        Assert.Contains(1000L, ids);          // forged delete ignored
        Assert.Contains(1002L, ids);          // rejected events never stored
        Assert.NotNull((await db.Activities.SingleAsync(a => a.StravaId == 5000)).DetailFetchedAtUtc);
        Assert.Equal(3, await db.WebhookEvents.CountAsync(e => e.Status == WebhookEventStatus.Done));
    }

    [Fact]
    public async Task A_confirmed_deauthorization_disconnects()
    {
        await using var host = await SubscribedHostAsync();
        await host.Get<StravaWebhookService>().AcceptAsync(Event("athlete", 42, "update", updates: "{\"authorized\":\"false\"}"));
        host.Strava.Revoked = true;

        var result = await host.Get<SyncEngine>().RunAsync(SyncTrigger.Webhook);

        Assert.Equal(SyncOutcome.NotConnected, result.Outcome);
        Assert.False((await host.Get<StravaAuthService>().GetConnectionAsync()).Connected);
    }

    [Fact]
    public async Task An_unconfirmed_deauthorization_is_ignored()
    {
        await using var host = await SubscribedHostAsync();
        await host.Get<StravaWebhookService>().AcceptAsync(Event("athlete", 42, "update", updates: "{\"authorized\":\"false\"}"));

        var result = await host.Get<SyncEngine>().RunAsync(SyncTrigger.Webhook);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.True((await host.Get<StravaAuthService>().GetConnectionAsync()).Connected);
        await using var db = await host.DbAsync();
        Assert.Equal(WebhookEventStatus.Ignored, (await db.WebhookEvents.SingleAsync()).Status);
    }
}

public class ScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduleInput Idle(Func<ScheduleInput, ScheduleInput>? change = null)
    {
        var i = new ScheduleInput(Now, AutoSync: true, Connected: true, Running: false, LastFinishedUtc: Now.AddMinutes(-10),
            ResumeAfterUtc: null, BackfillComplete: true, DueJobs: 0, IntervalMinutes: 60, WebhooksActive: false,
            PendingWebhookEvents: 0, LastWebhookUtc: null, ConsecutiveFailures: 0);
        return change is null ? i : change(i);
    }

    [Fact]
    public void Applies_the_rules_in_order()
    {
        Assert.False(SyncSchedule.Evaluate(Idle()).Due);
        Assert.True(SyncSchedule.Evaluate(Idle(i => i with { LastFinishedUtc = Now.AddMinutes(-61) })).Due);
        Assert.False(SyncSchedule.Evaluate(Idle(i => i with { LastFinishedUtc = Now.AddHours(-2), Connected = false })).Due);

        // Rate-limit pause wins over everything.
        Assert.False(SyncSchedule.Evaluate(Idle(i => i with { ResumeAfterUtc = Now.AddMinutes(5), PendingWebhookEvents = 3 })).Due);
        Assert.True(SyncSchedule.Evaluate(Idle(i => i with { ResumeAfterUtc = Now.AddSeconds(-1) })).Due);

        // Webhooks: debounced, then due; the regular interval stretches to a 6-hour safety net.
        Assert.False(SyncSchedule.Evaluate(Idle(i => i with { PendingWebhookEvents = 1, LastWebhookUtc = Now.AddSeconds(-5) })).Due);
        Assert.True(SyncSchedule.Evaluate(Idle(i => i with { PendingWebhookEvents = 1, LastWebhookUtc = Now.AddSeconds(-25) })).Due);
        Assert.False(SyncSchedule.Evaluate(Idle(i => i with { WebhooksActive = true, LastFinishedUtc = Now.AddHours(-2) })).Due);
        Assert.True(SyncSchedule.Evaluate(Idle(i => i with { WebhooksActive = true, LastFinishedUtc = Now.AddHours(-6.1) })).Due);

        // Backlog every 5 minutes; failures back off 5, 10, 20... minutes.
        Assert.True(SyncSchedule.Evaluate(Idle(i => i with { DueJobs = 10, LastFinishedUtc = Now.AddMinutes(-6) })).Due);
        Assert.False(SyncSchedule.Evaluate(Idle(i => i with { DueJobs = 10, LastFinishedUtc = Now.AddMinutes(-6), ConsecutiveFailures = 2 })).Due);
        Assert.Equal(TimeSpan.FromMinutes(20), SyncSchedule.FailureBackoff(3));
        Assert.Equal(TimeSpan.FromHours(6), SyncSchedule.FailureBackoff(20));
    }
}

public class BackupTests
{
    private static readonly DateTime Newest = new(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc);

    private static async Task<TestHost> SourceAsync()
    {
        var host = await TestHost.CreateAsync();
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { FetchStreams = true, Units = UnitSystem.Imperial });
        host.AddActivities(5, Newest, TimeSpan.FromDays(1));
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);
        const string csv = "Activity ID,Activity Date,Activity Name,Activity Type,Distance\r\n77,\"Jan 5, 2020, 7:30:12 AM\",Old run,Run,10.0\r\n";
        await host.Get<ArchiveService>().ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "activities.csv");
        await using var db = await host.DbAsync();
        db.AppSettings.Add(new AppSetting { Key = "webhook.subscriptionId", Value = "777" });
        await db.SaveChangesAsync();
        return host;
    }

    [Fact]
    public async Task Backup_restores_into_another_install_keeping_its_settings()
    {
        await using var source = await SourceAsync();
        var file = await source.Get<BackupService>().CreateAsync(new BackupOptions());
        Assert.Equal((5, 1, true), (file.Manifest.Activities, file.Manifest.ArchivedActivities, file.Manifest.IncludesStreams));

        await using var target = await TestHost.CreateAsync(connected: false);
        var settings = target.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { MobileRelayBaseUrl = "https://relay.example.com" });

        var inspection = await target.Get<BackupService>().InspectAsync(file.Path);
        Assert.True(inspection.IsValid, string.Join(" ", inspection.Errors));
        var result = await target.Get<BackupService>().RestoreAsync(file.Path, new BackupRestoreOptions());
        File.Delete(file.Path);

        Assert.Equal((5, 1, true), (result.Activities, result.ArchivedActivities, result.ArchiveRestored));
        Assert.True(File.Exists(result.SafetyCopyPath));
        Assert.StartsWith(target.Dir, result.SafetyCopyPath);
        await using var db = await target.DbAsync();
        Assert.Equal(10, await db.ActivityStreams.CountAsync());   // 5 activities x (time, heartrate)
        Assert.True((await db.SyncStates.SingleAsync()).BackfillComplete);
        Assert.Equal("Test", (await db.Athletes.SingleAsync()).FirstName);
        Assert.False(await db.AppSettings.AnyAsync(s => s.Key.StartsWith("webhook.")));
        var restored = await settings.GetAsync();
        Assert.Equal("https://relay.example.com", restored.MobileRelayBaseUrl);   // this device's settings kept
        Assert.Equal(UnitSystem.Metric, restored.Units);
        // Reports work straight after a restore.
        Assert.Equal(5, (await target.Get<DashboardReport>().RunAsync(new ReportFilter(), restored)).AllTime.Count);
    }

    [Fact]
    public async Task Backup_without_streams_requeues_them()
    {
        await using var source = await SourceAsync();
        var file = await source.Get<BackupService>().CreateAsync(new BackupOptions(IncludeArchive: false, IncludeStreams: false));
        await using var target = await TestHost.CreateAsync();
        var result = await target.Get<BackupService>().RestoreAsync(file.Path, new BackupRestoreOptions(KeepDeviceSettings: false));
        File.Delete(file.Path);

        Assert.False(result.ArchiveRestored);
        await using var db = await target.DbAsync();
        Assert.Equal(0, await db.ActivityStreams.CountAsync());
        Assert.Equal(5, await db.Activities.CountAsync(a => a.StreamsFetchedAtUtc == null));
        Assert.Equal(UnitSystem.Imperial, (await target.Get<AppSettingsService>().GetAsync()).Units);   // source settings taken
        // The source's data is still intact.
        await using var src = await source.DbAsync();
        Assert.Equal(5, await src.Activities.CountAsync(a => a.StreamsFetchedAtUtc != null));
    }

    [Fact]
    public async Task Rejects_files_that_are_not_backups_or_come_from_a_newer_version()
    {
        await using var host = await SourceAsync();
        var backups = host.Get<BackupService>();
        var notBackup = Path.Combine(host.Dir, "random.zip");
        using (var zip = ZipFile.Open(notBackup, ZipArchiveMode.Create)) zip.CreateEntry("hello.txt");
        Assert.False((await backups.InspectAsync(notBackup)).IsValid);
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(notBackup, new BackupRestoreOptions()));

        var file = await backups.CreateAsync(new BackupOptions(IncludeArchive: false));
        var work = Path.Combine(host.Dir, "edit");
        ZipFile.ExtractToDirectory(file.Path, work);
        await using (var conn = new SqliteConnection($"Data Source={Path.Combine(work, BackupService.MainEntry)};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO __EFMigrationsHistory VALUES ('29991231000000_FromTheFuture', '99.0');";
            await cmd.ExecuteNonQueryAsync();
        }
        var future = Path.Combine(host.Dir, "future.zip");
        ZipFile.CreateFromDirectory(work, future);
        File.Delete(file.Path);

        var inspection = await backups.InspectAsync(future);
        Assert.False(inspection.IsValid);
        Assert.Contains(inspection.Errors, e => e.Contains("newer version"));
        await using var db = await host.DbAsync();
        Assert.Equal(5, await db.Activities.CountAsync());   // nothing touched
    }

    [Fact]
    public async Task Transfer_codes_work_once_expire_and_lock_out_guessing()
    {
        await using var host = await SourceAsync();
        var transfers = host.Get<TransferCodeService>();
        var ticket = await transfers.CreateAsync(new BackupOptions());
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", ticket.Code);

        var ok = transfers.Redeem(ticket.Code.ToLowerInvariant().Replace("-", " "));
        Assert.Equal(RedeemStatus.Ok, ok.Status);
        Assert.True(File.Exists(ok.Path));
        File.Delete(ok.Path!);
        Assert.Equal(RedeemStatus.NotFound, transfers.Redeem(ticket.Code).Status);   // single use

        var late = await transfers.CreateAsync(new BackupOptions());
        host.Time.Advance(TransferCodeService.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(RedeemStatus.NotFound, transfers.Redeem(late.Code).Status);     // swept once expired

        for (var i = 0; i < TransferCodeService.MaxFailures; i++) transfers.Redeem("AAAA-AAAA-AAAA");
        var locked = await transfers.CreateAsync(new BackupOptions());
        Assert.Equal(RedeemStatus.LockedOut, transfers.Redeem(locked.Code).Status);
        host.Time.Advance(TransferCodeService.LockoutWindow + TimeSpan.FromSeconds(1));
        var after = await transfers.CreateAsync(new BackupOptions());
        var redeemed = transfers.Redeem(after.Code);
        Assert.Equal(RedeemStatus.Ok, redeemed.Status);
        File.Delete(redeemed.Path!);

        // Unused codes' files are removed when the service shuts down.
        var before = Directory.GetFiles(Path.GetTempPath(), "*-strava-reports-backup-*.zip").ToHashSet();
        await transfers.CreateAsync(new BackupOptions());
        var created = Assert.Single(Directory.GetFiles(Path.GetTempPath(), "*-strava-reports-backup-*.zip"), f => !before.Contains(f));
        transfers.Dispose();
        Assert.False(File.Exists(created));
    }

    [Fact]
    public async Task Transfer_client_downloads_over_https_only()
    {
        var client = new TransferClient(new HttpClient(new StaticFileHandler("zipbytes"u8.ToArray())));
        var dest = Path.Combine(Path.GetTempPath(), $"transfer-{Guid.NewGuid():N}.zip");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync("http://reports.example.com", "AAAA", dest));
        await client.DownloadAsync("https://reports.example.com/", "abcd-efgh-jkmn", dest);
        Assert.Equal("zipbytes", await File.ReadAllTextAsync(dest));
        File.Delete(dest);

        // A login page or other HTML is never saved as a backup.
        var html = new TransferClient(new HttpClient(new HtmlHandler()));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => html.DownloadAsync("https://reports.example.com", "abcd-efgh-jkmn", dest));
        Assert.Contains("did not send a backup", ex.Message);
        Assert.False(File.Exists(dest));
    }

    private sealed class HtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") });
    }

    private sealed class StaticFileHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/transfer/ABCDEFGHJKMN", request.RequestUri!.AbsolutePath);
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}

public class ArchiveInReportsTests
{
    [Fact]
    public async Task Archive_only_activities_join_the_reports_without_double_counting()
    {
        await using var host = await TestHost.CreateAsync();
        var settings = host.Get<AppSettingsService>();
        await settings.SaveAsync((await settings.GetAsync()) with { FetchDetail = false });
        host.AddActivities(2, new DateTime(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc), TimeSpan.FromDays(1));   // 1000, 1001
        await host.Get<SyncEngine>().RunAsync(SyncTrigger.Manual);
        // 1000 is in both; 77 and 78 only in the archive (one is a commute ride).
        const string csv = "Activity ID,Activity Date,Activity Name,Activity Type,Distance,Moving Time,Commute,Distance\r\n" +
                           "1000,\"Oct 10, 2026, 7:00:00 AM\",Same run,Run,10.0,3000,false,10000\r\n" +
                           "77,\"Jan 5, 2020, 7:30:12 AM\",Old run,Run,8.0,2400,false,8000\r\n" +
                           "78,\"Jan 6, 2020, 7:30:12 AM\",Old commute,Ride,5.0,900,true,5000\r\n";
        await host.Get<ArchiveService>().ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "activities.csv");
        var s = await settings.GetAsync();
        var dashboard = host.Get<DashboardReport>();

        Assert.Equal(2, (await dashboard.RunAsync(new ReportFilter(), s)).AllTime.Count);
        var all = (await dashboard.RunAsync(new ReportFilter { IncludeArchive = true }, s)).AllTime;
        Assert.Equal(4, all.Count);
        Assert.Equal(20_000 + 8_000 + 5_000, all.DistanceM);
        Assert.Equal(3, (await dashboard.RunAsync(new ReportFilter { IncludeArchive = true, IncludeCommute = false }, s)).AllTime.Count);
        Assert.Equal(1, (await dashboard.RunAsync(new ReportFilter { IncludeArchive = true, Group = SportGroup.Ride }, s)).AllTime.Count);
        Assert.Equal(0, (await dashboard.RunAsync(new ReportFilter { IncludeArchive = true, GearId = "g1" }, s)).AllTime.Count);   // the export has no gear IDs

        var volume = await host.Get<VolumeTrendsReport>().RunAsync(new ReportFilter { IncludeArchive = true, From = new DateOnly(2020, 1, 1), To = new DateOnly(2020, 12, 31) }, Grouping.Year, s);
        Assert.Equal(2, volume.Total.Count);
        Assert.Equal(3, await host.Get<ArchiveRowSource>().CountAsync());
        Assert.Contains((await host.Get<RunningReport>().RunAsync(new ReportFilter { IncludeArchive = true }, s)).Runs, r => r.FromArchive);
    }
}

public class ArchiveGearTests
{
    [Fact]
    public async Task Archive_gear_names_match_synced_gear_and_filter_by_either()
    {
        await using var host = await TestHost.CreateAsync();
        await using (var db = await host.Get<IDbContextFactory<StravaDbContext>>().CreateDbContextAsync())
        {
            db.Gear.Add(new Gear { StravaId = "b9", Name = "Trek Fuel EX", GearType = "bike" });
            await db.SaveChangesAsync();
        }
        const string csv = "Activity ID,Activity Date,Activity Name,Activity Type,Distance,Moving Time,Activity Gear\r\n" +
                           "81,\"Jan 5, 2020, 7:30:12 AM\",On the Trek,Ride,40.0,5000,trek fuel ex\r\n" +
                           "82,\"Jan 6, 2020, 7:30:12 AM\",On the old bike,Ride,30.0,4000,Old Bike\r\n" +
                           "83,\"Jan 7, 2020, 7:30:12 AM\",No gear,Run,5.0,1500,\r\n";
        await host.Get<ArchiveService>().ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "activities.csv");
        var source = host.Get<ArchiveRowSource>();

        var all = await source.RowsAsync(new ReportFilter());
        Assert.Equal("b9", all.Single(r => r.StravaId == 81).GearId);                       // name matches a synced gear item, ignoring case
        Assert.Equal("name:Old Bike", all.Single(r => r.StravaId == 82).GearId);
        Assert.Null(all.Single(r => r.StravaId == 83).GearId);

        Assert.Equal(new[] { 81L }, (await source.RowsAsync(new ReportFilter { GearId = "b9" })).Select(r => r.StravaId).ToArray());
        Assert.Equal(new[] { 82L }, (await source.RowsAsync(new ReportFilter { GearId = "name:Old Bike" })).Select(r => r.StravaId).ToArray());
        Assert.Equal(["Old Bike"], await source.GearNamesAsync());                           // only gear the synced list lacks

        var src = await host.Get<AllDataReport>().LoadAsync(new ReportFilter { IncludeArchive = true });
        Assert.Equal("Old Bike", src.GearName("name:Old Bike"));
        Assert.Equal("Trek Fuel EX", src.GearName("b9"));
    }
}

public class RecordsFilterTests
{
    [Fact]
    public async Task Running_records_ignore_a_hidden_ride_filter_and_use_archive_runs()
    {
        await using var host = await TestHost.CreateAsync();
        const string csv = "Activity ID,Activity Date,Activity Name,Activity Type,Distance,Moving Time,Commute,Distance\r\n" +
                           "91,\"Jan 5, 2020, 7:30:12 AM\",Fast 5k,Run,5.0,1500,false,5000\r\n" +
                           "92,\"Jan 6, 2020, 7:30:12 AM\",Slow 5k,Run,5.1,1650,false,5100\r\n" +
                           "93,\"Jan 7, 2020, 7:30:12 AM\",Long ride,Ride,40.0,5000,false,40000\r\n";
        await host.Get<ArchiveService>().ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "activities.csv");
        var s = await host.Get<AppSettingsService>().GetAsync();

        // A Ride filter left over from another page must not hide the runs on a page with no sport selector.
        var r = await host.Get<RecordsReport>().RunAsync(new ReportFilter { IncludeArchive = true, Group = SportGroup.Ride }, s);

        Assert.Contains(r.Highlights, h => h.Label == "Longest run" && h.StravaId == 92 && h.FromArchive);
        Assert.Contains(r.Highlights, h => h.Label == "Longest ride" && h.StravaId == 93);
        var fiveK = Assert.Single(r.WholeRuns);
        Assert.Equal("5K", fiveK.Label);
        Assert.Equal(91, fiveK.Run.StravaId);                      // the faster of the two 5Ks
        Assert.True(fiveK.Run.FromArchive);
    }
}

public class PolylineRoundTripTests
{
    [Fact]
    public void Encode_is_the_inverse_of_decode()
    {
        const string reference = "_p~iF~ps|U_ulLnnqC_mqNvxq`@";
        Assert.Equal(reference, Polyline.Encode(Polyline.Decode(reference)));
    }
}
