using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;

namespace StravaReports.Core.Sync;

public record SyncStatus(
    bool IsRunning,
    string Stage,
    string StageMessage,
    DateTime? LastRunStartedUtc,
    DateTime? LastRunFinishedUtc,
    string? LastStatus,
    string? LastError,
    DateTime? ResumeAfterUtc,
    bool BackfillComplete,
    DateTime? BackfillReachedUtc,
    int ActivityCount,
    int PendingJobs,
    /// <summary>Pending jobs that are enabled in settings and ready to run now.</summary>
    int DueJobs,
    int FailedJobs,
    RateLimitSnapshot RateLimits);

public class SyncStatusService(IDbContextFactory<StravaDbContext> dbFactory, SyncEngine engine, RateLimitTracker tracker, AppSettingsService settings)
{
    public async Task<SyncStatus> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.SyncStates.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new SyncState();
        var count = await db.Activities.CountAsync(ct);
        var pending = await db.SyncJobs.CountAsync(j => j.Status == SyncJobStatus.Pending, ct);
        var failed = await db.SyncJobs.CountAsync(j => j.Status == SyncJobStatus.Failed, ct);
        var opts = await settings.GetAsync(ct);
        var now = DateTime.UtcNow;
        var due = await db.SyncJobs.CountAsync(j => j.Status == SyncJobStatus.Pending && j.NextAttemptUtc <= now &&
            ((j.JobType == SyncJobType.Detail && opts.FetchDetail) || (j.JobType == SyncJobType.Streams && opts.FetchStreams)), ct);

        tracker.Restore(new RateLimitSnapshot(state.ShortLimit, state.ShortUsage, state.DailyLimit, state.DailyUsage, state.RateLimitObservedUtc));
        var progress = engine.Progress;

        return new SyncStatus(
            progress.IsRunning, progress.Stage, progress.Message,
            state.LastRunStartedUtc, state.LastRunFinishedUtc, state.LastStatus, state.LastError, state.ResumeAfterUtc,
            state.BackfillComplete,
            state.BackfillCursorEpoch is { } c ? DateTimeOffset.FromUnixTimeSeconds(c).UtcDateTime : null,
            count, pending, due, failed, tracker.Current);
    }

    public async Task<int> RetryFailedJobsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SyncJobs.Where(j => j.Status == SyncJobStatus.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, SyncJobStatus.Pending)
                                      .SetProperty(j => j.Attempts, 0)
                                      .SetProperty(j => j.NextAttemptUtc, DateTime.UtcNow), ct);
    }

    /// <summary>Force a reconcile pass on the next run.</summary>
    public async Task RequestReconcileAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SyncStates.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReconcileUtc, (DateTime?)null), ct);
    }
}

public class DataManagementService(IDbContextFactory<StravaDbContext> dbFactory, AppSettingsService settings)
{
    /// <summary>Empties all Strava data and sync state. Settings and the Strava connection are kept.</summary>
    public async Task DeleteAllDataAsync(bool includeSettings, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ActivityStreams.ExecuteDeleteAsync(ct);
        await db.ActivityLaps.ExecuteDeleteAsync(ct);
        await db.ActivityBestEfforts.ExecuteDeleteAsync(ct);
        await db.Activities.ExecuteDeleteAsync(ct);
        await db.Gear.ExecuteDeleteAsync(ct);
        await db.SyncJobs.ExecuteDeleteAsync(ct);
        await db.SyncStates.ExecuteDeleteAsync(ct);
        await db.Athletes.ExecuteDeleteAsync(ct);
        await db.LogEntries.ExecuteDeleteAsync(ct);
        await db.WebhookEvents.ExecuteDeleteAsync(ct);
        if (includeSettings) await db.AppSettings.ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
        settings.Invalidate();
    }
}
