using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Logging;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;

namespace StravaReports.Core.Sync;

public enum SyncTrigger { Manual, Launch, Scheduled, Webhook, Resume }

public enum SyncOutcome { Completed, Paused, Failed, Cancelled, NotConnected, AlreadyRunning }

public record SyncRunResult(SyncOutcome Outcome, int ActivitiesUpserted, int JobsProcessed, string Message, DateTime? ResumeAfterUtc = null);

public record SyncProgress(bool IsRunning, string Stage, string Message);

/// <summary>
/// Staged, resumable sync: refresh token, incremental list, backfill, reconcile, detail/stream queue.
/// Every stage commits as it goes, so a crash or a rate-limit pause loses at most one request.
/// </summary>
public class SyncEngine(
    IDbContextFactory<StravaDbContext> dbFactory,
    IStravaClient client,
    StravaAuthService auth,
    AppSettingsService settings,
    RateLimitTracker rateLimits,
    AppLog log,
    TimeProvider? timeProvider = null)
{
    public static readonly string[] StreamKeys = ["time", "distance", "latlng", "altitude", "velocity_smooth", "heartrate", "cadence", "watts", "temp", "moving", "grade_smooth"];
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromDays(7);
    public static readonly TimeSpan ReconcileWindow = TimeSpan.FromDays(30);
    public const int MaxJobAttempts = 5;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private CancellationTokenSource? _runCts;

    public SyncProgress Progress { get; private set; } = new(false, "Idle", "");
    public event Action<SyncProgress>? ProgressChanged;
    public event Action<SyncRunResult>? RunCompleted;

    public bool IsRunning => Progress.IsRunning;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Fire-and-forget run for UI buttons and app launch. Ignored if a run is in progress.</summary>
    public void StartInBackground(SyncTrigger trigger)
    {
        if (IsRunning) return;
        _ = Task.Run(() => RunAsync(trigger));
    }

    /// <summary>Cancels the current run, whoever started it.</summary>
    public void Cancel()
    {
        try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Stops any run and blocks new ones until disposed. Used by restore, which replaces the database.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken ct = default)
    {
        Cancel();
        await _runLock.WaitAsync(ct);
        Progress = new SyncProgress(false, "Paused", "Database maintenance in progress");
        ProgressChanged?.Invoke(Progress);
        return new Releaser(this);
    }

    private sealed class Releaser(SyncEngine engine) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                engine.Progress = new SyncProgress(false, "Idle", "");
                engine.ProgressChanged?.Invoke(engine.Progress);
                engine._runLock.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    public async Task<SyncRunResult> RunAsync(SyncTrigger trigger, CancellationToken cancellationToken = default)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, 0, 0, "A sync is already running.");

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = runCts;
        var ct = runCts.Token;
        var upserted = 0;
        var jobs = 0;
        SyncRunResult result;
        try
        {
            var state = await LoadStateAsync(ct);
            rateLimits.Restore(new RateLimitSnapshot(state.ShortLimit, state.ShortUsage, state.DailyLimit, state.DailyUsage, state.RateLimitObservedUtc));

            if (state.ResumeAfterUtc is { } resume && resume > Now)
            {
                result = new SyncRunResult(SyncOutcome.Paused, 0, 0, $"Paused for rate limits until {resume:u}.", resume);
                return result;
            }

            await UpdateStateAsync(s => { s.LastRunStartedUtc = Now; s.LastStatus = "Running"; s.ResumeAfterUtc = null; }, ct);
            await log.InfoAsync("Sync", $"Sync started ({trigger}).");
            var opts = await settings.GetAsync(ct);

            try
            {
                Report("Auth", "Checking access token");
                var token = await auth.GetAccessTokenAsync(ct);

                await RefreshAthleteAsync(token, ct);

                upserted += await ProcessWebhookEventsAsync(token, opts, ct);

                Report("Incremental", "Fetching new activities");
                upserted += await IncrementalAsync(token, ct);

                Report("Backfill", "Fetching history");
                upserted += await BackfillAsync(token, ct);

                upserted += await ReconcileIfDueAsync(token, ct);

                if (opts.FetchDetail || opts.FetchStreams)
                {
                    Report("Detail", "Fetching activity detail");
                    jobs = await ProcessQueueAsync(token, opts, ct);
                }

                result = new SyncRunResult(SyncOutcome.Completed, upserted, jobs, $"Sync complete: {upserted} activities updated, {jobs} detail jobs.");
            }
            catch (RateLimitExceededException ex)
            {
                result = new SyncRunResult(SyncOutcome.Paused, upserted, jobs, $"{ex.Message} Resuming after {ex.ResumeAfterUtc:u}.", ex.ResumeAfterUtc);
            }
            catch (NotConnectedException ex)
            {
                result = new SyncRunResult(SyncOutcome.NotConnected, upserted, jobs, ex.Message);
            }
            catch (OperationCanceledException)
            {
                result = new SyncRunResult(SyncOutcome.Cancelled, upserted, jobs, "Sync cancelled.");
            }
            catch (Exception ex)
            {
                result = new SyncRunResult(SyncOutcome.Failed, upserted, jobs, ex.Message);
            }

            var snapshot = rateLimits.Current;
            await UpdateStateAsync(s =>
            {
                s.LastRunFinishedUtc = Now;
                s.LastStatus = result.Outcome.ToString();
                s.LastError = result.Outcome is SyncOutcome.Failed or SyncOutcome.NotConnected ? result.Message : null;
                s.ResumeAfterUtc = result.ResumeAfterUtc;
                s.ShortLimit = snapshot.ShortLimit;
                s.ShortUsage = snapshot.ShortUsage;
                s.DailyLimit = snapshot.DailyLimit;
                s.DailyUsage = snapshot.DailyUsage;
                s.RateLimitObservedUtc = snapshot.ObservedUtc;
            }, CancellationToken.None);

            if (result.Outcome is SyncOutcome.Failed) await log.ErrorAsync("Sync", result.Message);
            else if (result.Outcome is SyncOutcome.Paused or SyncOutcome.NotConnected) await log.WarnAsync("Sync", result.Message);
            else await log.InfoAsync("Sync", result.Message);
            RunCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _runCts = null;
            Progress = new SyncProgress(false, "Idle", "");
            ProgressChanged?.Invoke(Progress);
            _runLock.Release();
        }
    }

    /// <summary>
    /// Stage 1b: apply webhook events. Each affected activity is re-read from the API, so an event can
    /// only ever cause the same change a normal sync would; a forged "delete" for an activity that still
    /// exists just refreshes it.
    /// </summary>
    private async Task<int> ProcessWebhookEventsAsync(string token, AppSettingsModel opts, CancellationToken ct)
    {
        List<WebhookEvent> events;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            events = await db.WebhookEvents.AsNoTracking().Where(e => e.Status == WebhookEventStatus.Pending)
                .OrderBy(e => e.Id).Take(200).ToListAsync(ct);
        if (events.Count == 0) return 0;

        Report("Webhooks", $"Applying {events.Count} Strava events");
        var changed = 0;
        // Several events for one object (create then update...) collapse into one API call.
        foreach (var group in events.GroupBy(e => (e.ObjectType, e.ObjectId)))
        {
            ct.ThrowIfCancellationRequested();
            var (note, count, done) = group.Key.ObjectType == "activity"
                ? await ApplyActivityEventAsync(token, group.Key.ObjectId, opts, ct)
                : await ApplyAthleteEventAsync(token, group.ToList(), ct);
            changed += count;

            var ids = group.Select(e => e.Id).ToList();
            var status = done ? WebhookEventStatus.Done : WebhookEventStatus.Ignored;
            var at = Now;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.WebhookEvents.Where(e => ids.Contains(e.Id)).ExecuteUpdateAsync(u => u
                .SetProperty(e => e.Status, status)
                .SetProperty(e => e.ProcessedUtc, at)
                .SetProperty(e => e.Attempts, e => e.Attempts + 1)
                .SetProperty(e => e.Note, note), CancellationToken.None);
        }
        // Keep the table small: processed events older than 30 days go.
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cutoff = Now.AddDays(-30);
            await db.WebhookEvents.Where(e => e.Status != WebhookEventStatus.Pending && e.ReceivedUtc < cutoff).ExecuteDeleteAsync(ct);
        }
        await log.InfoAsync("Webhook", $"Applied {events.Count} webhook events ({changed} activities changed).");
        return changed;
    }

    private async Task<(string Note, int Changed, bool Done)> ApplyActivityEventAsync(string token, long stravaId, AppSettingsModel opts, CancellationToken ct)
    {
        var detail = await client.GetActivityAsync(token, stravaId, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Activities.Include(a => a.Laps).Include(a => a.BestEfforts).AsSplitQuery()
            .FirstOrDefaultAsync(a => a.StravaId == stravaId, ct);

        if (detail is null)
        {
            if (row is null) return ("Not visible on Strava and not stored; nothing to do.", 0, true);
            db.Activities.Remove(row);
            await db.SaveChangesAsync(ct);
            await db.SyncJobs.Where(j => j.ActivityStravaId == stravaId).ExecuteDeleteAsync(ct);
            return ("Deleted locally; confirmed gone on Strava.", 1, true);
        }

        var created = row is null;
        if (row is null) db.Activities.Add(row = new Activity());
        if (opts.FetchDetail) ActivityMapper.ApplyDetail(row, detail.Activity, detail.RawJson, Now);
        else ActivityMapper.ApplySummary(row, detail.Activity, detail.RawJson, Now);
        await EnsureGearAsync(db, token, detail.Activity.GearId, ct);
        await db.SaveChangesAsync(ct);
        return (created ? "Added from Strava." : "Updated from Strava.", 1, true);
    }

    private async Task<(string Note, int Changed, bool Done)> ApplyAthleteEventAsync(string token, List<WebhookEvent> events, CancellationToken ct)
    {
        var deauthorized = events.Any(e => e.UpdatesJson is { } u && u.Replace(" ", "").Contains("\"authorized\":\"false\"", StringComparison.OrdinalIgnoreCase));
        if (!deauthorized) return ("Athlete profile update; nothing to do.", 0, true);
        try
        {
            await client.GetAthleteAsync(token, ct);
            return ("Deauthorization not confirmed: the token still works.", 0, false);
        }
        catch (StravaApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await auth.DisconnectAsync(ct);
            throw new NotConnectedException("Access was revoked on Strava; disconnected.");
        }
    }

    private async Task EnsureGearAsync(StravaDbContext db, string token, string? gearId, CancellationToken ct)
    {
        if (gearId is null || db.Gear.Local.Any(g => g.StravaId == gearId) || await db.Gear.AnyAsync(g => g.StravaId == gearId, ct)) return;
        var gear = await client.GetGearAsync(token, gearId, ct);
        if (gear is null) return;
        var g = new Gear();
        ActivityMapper.ApplyGear(g, gear, ActivityMapper.GearTypeFromId(gearId), Now);
        db.Gear.Add(g);
    }

    private void Report(string stage, string message)
    {
        Progress = new SyncProgress(true, stage, message);
        ProgressChanged?.Invoke(Progress);
    }

    private async Task RefreshAthleteAsync(string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var athlete = await db.Athletes.AsNoTracking().OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        if (athlete?.RawJson is not null && Now - athlete.UpdatedAtUtc < TimeSpan.FromDays(1)) return;

        var (a, raw) = await client.GetAthleteAsync(token, ct);
        await auth.UpsertAthleteAsync(a, raw, ct);

        foreach (var g in a.Bikes ?? []) await UpsertGearAsync(db, g, "bike");
        foreach (var g in a.Shoes ?? []) await UpsertGearAsync(db, g, "shoe");
        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertGearAsync(StravaDbContext db, StravaGearSummary g, string type)
    {
        var row = await db.Gear.FirstOrDefaultAsync(x => x.StravaId == g.Id);
        if (row is null) db.Gear.Add(row = new Gear());
        ActivityMapper.ApplyGear(row, g, type, Now);
    }

    /// <summary>Stage 2: everything after the newest stored activity, oldest first.</summary>
    private async Task<int> IncrementalAsync(string token, CancellationToken ct)
    {
        var state = await LoadStateAsync(ct);
        if (state.NewestActivityEpoch is not { } after) return 0; // first run: backfill covers it

        var total = 0;
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            var items = await client.ListActivitiesAsync(token, after, null, page, StravaClient.MaxPageSize, ct);
            if (items.Count == 0) break;
            total += await UpsertPageAsync(items, backfillCursor: false, ct);
            Report("Incremental", $"{total} new activities");
            if (items.Count < StravaClient.MaxPageSize) break;
        }
        return total;
    }

    /// <summary>Stage 3: page backwards through history, saving a cursor after every page.</summary>
    private async Task<int> BackfillAsync(string token, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var state = await LoadStateAsync(ct);
            if (state.BackfillComplete) break;

            var items = await client.ListActivitiesAsync(token, null, state.BackfillCursorEpoch, 1, StravaClient.MaxPageSize, ct);
            if (items.Count == 0)
            {
                await UpdateStateAsync(s => s.BackfillComplete = true, ct);
                await log.InfoAsync("Sync", "Backfill complete.");
                break;
            }
            total += await UpsertPageAsync(items, backfillCursor: true, ct);
            Report("Backfill", $"{total} activities; reached {items.Min(i => i.Activity.StartDate):yyyy-MM-dd}");
        }
        return total;
    }

    /// <summary>Stage 5: weekly, compare recent remote IDs with local ones to catch deletions and edits.</summary>
    private async Task<int> ReconcileIfDueAsync(string token, CancellationToken ct)
    {
        var state = await LoadStateAsync(ct);
        if (!state.BackfillComplete) return 0;
        if (state.LastReconcileUtc is { } last && Now - last < ReconcileInterval) return 0;

        Report("Reconcile", "Checking for deleted or edited activities");
        var since = Now - ReconcileWindow;
        var after = new DateTimeOffset(since).ToUnixTimeSeconds();
        var remoteIds = new HashSet<long>();
        var updated = 0;
        for (var page = 1; ; page++)
        {
            var items = await client.ListActivitiesAsync(token, after, null, page, StravaClient.MaxPageSize, ct);
            foreach (var i in items) remoteIds.Add(i.Activity.Id);
            if (items.Count > 0) updated += await UpsertPageAsync(items, backfillCursor: false, ct);
            if (items.Count < StravaClient.MaxPageSize) break;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var localIds = await db.Activities.Where(a => a.StartDateUtc >= since).Select(a => a.StravaId).ToListAsync(ct);
        var gone = localIds.Where(id => !remoteIds.Contains(id)).ToList();
        if (gone.Count > 0)
        {
            await db.Activities.Where(a => gone.Contains(a.StravaId)).ExecuteDeleteAsync(ct);
            await db.SyncJobs.Where(j => gone.Contains(j.ActivityStravaId)).ExecuteDeleteAsync(ct);
            await log.InfoAsync("Sync", $"Reconcile removed {gone.Count} activities deleted on Strava.");
        }
        await UpdateStateAsync(s => s.LastReconcileUtc = Now, ct);
        return updated;
    }

    /// <summary>Upserts one page in a single transaction and advances the cursors with it.</summary>
    internal async Task<int> UpsertPageAsync(IReadOnlyList<StravaActivityWithJson> items, bool backfillCursor, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = items.Select(i => i.Activity.Id).ToList();
        var existing = await db.Activities.Where(a => ids.Contains(a.StravaId)).ToDictionaryAsync(a => a.StravaId, ct);

        foreach (var item in items)
        {
            if (!existing.TryGetValue(item.Activity.Id, out var row))
            {
                row = new Activity();
                db.Activities.Add(row);
                existing[item.Activity.Id] = row;
            }
            ActivityMapper.ApplySummary(row, item.Activity, item.RawJson, Now);
        }

        var state = await db.SyncStates.SingleAsync(ct);
        var epochs = items.Select(i => new DateTimeOffset(DateTime.SpecifyKind(i.Activity.StartDate.ToUniversalTime(), DateTimeKind.Utc)).ToUnixTimeSeconds()).ToList();
        state.NewestActivityEpoch = Math.Max(state.NewestActivityEpoch ?? 0, epochs.Max());
        if (backfillCursor) state.BackfillCursorEpoch = epochs.Min();

        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    /// <summary>Stage 4: make sure every activity lacking detail/streams has a job, then work the queue.</summary>
    private async Task<int> ProcessQueueAsync(string token, AppSettingsModel opts, CancellationToken ct)
    {
        await EnsureJobsAsync(opts, ct);
        var processed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<SyncJob> batch;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var now = Now;
                batch = await db.SyncJobs.AsNoTracking()
                    .Where(j => j.Status == SyncJobStatus.Pending && j.NextAttemptUtc <= now)
                    .Where(j => (j.JobType == SyncJobType.Detail && opts.FetchDetail) || (j.JobType == SyncJobType.Streams && opts.FetchStreams))
                    .OrderByDescending(j => j.ActivityStravaId) // newest activities first
                    .Take(25)
                    .ToListAsync(ct);
            }
            if (batch.Count == 0) break;

            foreach (var job in batch)
            {
                await RunJobAsync(token, job, ct);
                processed++;
                if (processed % 5 == 0) Report("Detail", $"{processed} detail jobs done");
            }
        }
        return processed;
    }

    internal async Task EnsureJobsAsync(AppSettingsModel opts, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = Now;
        if (opts.FetchDetail)
        {
            var missing = await db.Activities
                .Where(a => a.DetailFetchedAtUtc == null && !a.IsManual)
                .Where(a => !db.SyncJobs.Any(j => j.ActivityStravaId == a.StravaId && j.JobType == SyncJobType.Detail))
                .Select(a => a.StravaId).ToListAsync(ct);
            db.SyncJobs.AddRange(missing.Select(id => NewJob(id, SyncJobType.Detail, now)));
        }
        if (opts.FetchStreams)
        {
            var missing = await db.Activities
                .Where(a => a.StreamsFetchedAtUtc == null && !a.IsManual)
                .Where(a => !db.SyncJobs.Any(j => j.ActivityStravaId == a.StravaId && j.JobType == SyncJobType.Streams))
                .Select(a => a.StravaId).ToListAsync(ct);
            db.SyncJobs.AddRange(missing.Select(id => NewJob(id, SyncJobType.Streams, now)));
        }
        await db.SaveChangesAsync(ct);
    }

    private static SyncJob NewJob(long id, SyncJobType type, DateTime now) =>
        new() { ActivityStravaId = id, JobType = type, Status = SyncJobStatus.Pending, NextAttemptUtc = now, CreatedUtc = now };

    private async Task RunJobAsync(string token, SyncJob job, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SyncJobs.SingleAsync(j => j.Id == job.Id, ct);
        try
        {
            var activity = await db.Activities
                .Include(a => a.Laps).Include(a => a.BestEfforts).Include(a => a.Streams)
                .AsSplitQuery()
                .FirstOrDefaultAsync(a => a.StravaId == job.ActivityStravaId, ct);

            if (activity is null)
            {
                row.Status = SyncJobStatus.Done;
            }
            else if (job.JobType == SyncJobType.Detail)
            {
                var detail = await client.GetActivityAsync(token, job.ActivityStravaId, ct);
                if (detail is null)
                {
                    // 404: deleted or no longer visible on Strava.
                    db.Activities.Remove(activity);
                    await log.InfoAsync("Sync", $"Activity {job.ActivityStravaId} no longer exists on Strava; removed.");
                }
                else
                {
                    ActivityMapper.ApplyDetail(activity, detail.Activity, detail.RawJson, Now);
                    await EnsureGearAsync(db, token, detail.Activity.GearId, ct);
                }
                row.Status = SyncJobStatus.Done;
            }
            else
            {
                var streams = await client.GetActivityStreamsAsync(token, job.ActivityStravaId, StreamKeys, ct);
                activity.Streams.Clear();
                foreach (var (type, (count, json)) in streams?.Streams ?? new Dictionary<string, (int, string)>())
                    activity.Streams.Add(new ActivityStream { StreamType = type, PointCount = count, Data = Compress(json) });
                activity.StreamsFetchedAtUtc = Now;
                row.Status = SyncJobStatus.Done;
            }
            row.Attempts++;
            row.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not RateLimitExceededException and not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            db.SyncJobs.Attach(row);
            row.Attempts++;
            row.LastError = ex.Message;
            row.Status = row.Attempts >= MaxJobAttempts ? SyncJobStatus.Failed : SyncJobStatus.Pending;
            row.NextAttemptUtc = Now.AddMinutes(Math.Pow(2, row.Attempts));
            await db.SaveChangesAsync(CancellationToken.None);
            await log.WarnAsync("Sync", $"{job.JobType} job for activity {job.ActivityStravaId} failed (attempt {row.Attempts}): {ex.Message}");
            if (ex is StravaApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) throw;
        }
    }

    public static byte[] Compress(string json)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize))
            gz.Write(Encoding.UTF8.GetBytes(json));
        return ms.ToArray();
    }

    public static string Decompress(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task<SyncState> LoadStateAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.SyncStates.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (state is not null) return state;
        state = new SyncState { Id = 1 };
        db.SyncStates.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    private async Task UpdateStateAsync(Action<SyncState> change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.SyncStates.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (state is null) db.SyncStates.Add(state = new SyncState { Id = 1 });
        change(state);
        await db.SaveChangesAsync(ct);
    }
}
