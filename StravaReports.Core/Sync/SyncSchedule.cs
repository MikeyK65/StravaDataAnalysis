namespace StravaReports.Core.Sync;

/// <summary>Everything the scheduler looks at when deciding whether to start a run.</summary>
public record ScheduleInput(
    DateTime NowUtc,
    bool AutoSync,
    bool Connected,
    bool Running,
    DateTime? LastFinishedUtc,
    DateTime? ResumeAfterUtc,
    bool BackfillComplete,
    int DueJobs,
    int IntervalMinutes,
    bool WebhooksActive,
    int PendingWebhookEvents,
    DateTime? LastWebhookUtc,
    int ConsecutiveFailures);

public record ScheduleDecision(bool Due, string Reason);

/// <summary>
/// When the web app's background service should sync. Pure so the rules are easy to test:
/// rate-limit pauses win, then failure backoff, then webhook events (debounced), then the backlog,
/// then the regular interval (stretched to a safety-net poll while webhooks deliver changes).
/// </summary>
public static class SyncSchedule
{
    public static readonly TimeSpan BacklogInterval = TimeSpan.FromMinutes(5);
    /// <summary>Strava often sends create then update within seconds; wait so one run handles both.</summary>
    public static readonly TimeSpan WebhookDebounce = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan WebhookSafetyInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromHours(6);

    public static ScheduleDecision Evaluate(ScheduleInput i)
    {
        if (!i.AutoSync) return new(false, "Automatic sync is off.");
        if (!i.Connected) return new(false, "Not connected to Strava.");
        if (i.Running) return new(false, "A sync is running.");
        if (i.ResumeAfterUtc is { } resume)
            return i.NowUtc >= resume ? new(true, "Rate-limit pause is over.") : new(false, $"Paused for rate limits until {resume:HH:mm} UTC.");

        var since = i.LastFinishedUtc is { } last ? i.NowUtc - last : TimeSpan.MaxValue;
        if (i.ConsecutiveFailures > 0)
        {
            var backoff = FailureBackoff(i.ConsecutiveFailures);
            if (since < backoff) return new(false, $"Backing off after {i.ConsecutiveFailures} failed run(s); next try in {(backoff - since).TotalMinutes:0} min.");
        }

        if (i.PendingWebhookEvents > 0 && (i.LastWebhookUtc is not { } hook || i.NowUtc - hook >= WebhookDebounce))
            return new(true, "Strava sent new events.");

        if (!i.BackfillComplete || i.DueJobs > 0)
            return since >= BacklogInterval ? new(true, "Working through the backlog.") : new(false, "Backlog: waiting between runs.");

        var interval = TimeSpan.FromMinutes(Math.Max(1, i.IntervalMinutes));
        if (i.WebhooksActive && interval < WebhookSafetyInterval) interval = WebhookSafetyInterval;
        return since >= interval
            ? new(true, i.WebhooksActive ? "Safety-net poll (webhooks active)." : "Scheduled interval reached.")
            : new(false, $"Next scheduled run in {(interval - since).TotalMinutes:0} min.");
    }

    /// <summary>5, 10, 20, 40... minutes, capped at 6 hours.</summary>
    public static TimeSpan FailureBackoff(int failures) =>
        TimeSpan.FromMinutes(Math.Min(MaxFailureBackoff.TotalMinutes, 5 * Math.Pow(2, Math.Max(0, failures - 1))));
}
