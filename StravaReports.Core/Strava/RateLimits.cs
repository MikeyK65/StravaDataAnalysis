using System.Globalization;
using System.Net.Http.Headers;

namespace StravaReports.Core.Strava;

public record RateLimitSnapshot(int? ShortLimit, int? ShortUsage, int? DailyLimit, int? DailyUsage, DateTime? ObservedUtc)
{
    public int? ShortRemaining => ShortLimit - ShortUsage;
    public int? DailyRemaining => DailyLimit - DailyUsage;

    /// <summary>Usage carried forward to <paramref name="nowUtc"/>: counters reset at each 15-minute mark and at midnight UTC.</summary>
    public RateLimitSnapshot AsOf(DateTime nowUtc)
    {
        if (ObservedUtc is not { } observed) return this;
        var shortUsage = RateLimitWindows.ShortWindowStart(observed) == RateLimitWindows.ShortWindowStart(nowUtc) ? ShortUsage : 0;
        var dailyUsage = observed.Date == nowUtc.Date ? DailyUsage : 0;
        return this with { ShortUsage = shortUsage, DailyUsage = dailyUsage };
    }
}

public static class RateLimitWindows
{
    public static DateTime ShortWindowStart(DateTime utc) =>
        new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, DateTimeKind.Utc);

    public static DateTime NextShortWindow(DateTime utc) => ShortWindowStart(utc).AddMinutes(15);

    public static DateTime NextDailyWindow(DateTime utc) =>
        DateTime.SpecifyKind(utc.Date.AddDays(1), DateTimeKind.Utc);
}

public class RateLimitExceededException(DateTime resumeAfterUtc, bool daily, string message) : Exception(message)
{
    public DateTime ResumeAfterUtc { get; } = resumeAfterUtc;
    public bool Daily { get; } = daily;
}

/// <summary>
/// Tracks Strava's rate-limit headers across requests and refuses to send a request when the
/// 15-minute or daily budget is nearly spent. Prefers the read-specific limits when present.
/// </summary>
public class RateLimitTracker
{
    /// <summary>Requests kept in reserve in each 15-minute window (for the UI's own calls).</summary>
    public int ShortReserve { get; set; } = 3;
    public int DailyReserve { get; set; } = 10;

    private readonly Lock _lock = new();
    private RateLimitSnapshot _snapshot = new(null, null, null, null, null);
    private readonly TimeProvider _time;

    public RateLimitTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public RateLimitSnapshot Current
    {
        get { lock (_lock) return _snapshot.AsOf(_time.GetUtcNow().UtcDateTime); }
    }

    /// <summary>Seed from persisted state so budget is respected across app restarts.</summary>
    public void Restore(RateLimitSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_snapshot.ObservedUtc is null || snapshot.ObservedUtc > _snapshot.ObservedUtc)
                _snapshot = snapshot;
        }
    }

    public void Update(HttpResponseHeaders headers)
    {
        var limit = Read(headers, "X-ReadRateLimit-Limit") ?? Read(headers, "X-RateLimit-Limit");
        var usage = Read(headers, "X-ReadRateLimit-Usage") ?? Read(headers, "X-RateLimit-Usage");
        if (limit is null || usage is null) return;

        lock (_lock)
        {
            _snapshot = new RateLimitSnapshot(limit.Value.Short, usage.Value.Short, limit.Value.Daily, usage.Value.Daily,
                _time.GetUtcNow().UtcDateTime);
        }
    }

    /// <summary>Throws <see cref="RateLimitExceededException"/> when the next request would eat into the reserve.</summary>
    public void EnsureBudget()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var s = Current;
        if (s.DailyRemaining is { } d && d <= DailyReserve)
            throw new RateLimitExceededException(RateLimitWindows.NextDailyWindow(now), true,
                $"Daily rate limit nearly reached ({s.DailyUsage}/{s.DailyLimit}).");
        if (s.ShortRemaining is { } r && r <= ShortReserve)
            throw new RateLimitExceededException(RateLimitWindows.NextShortWindow(now), false,
                $"15-minute rate limit nearly reached ({s.ShortUsage}/{s.ShortLimit}).");
    }

    /// <summary>Called on HTTP 429: work out which window was exhausted and when to resume.</summary>
    public RateLimitExceededException OnTooManyRequests()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var s = Current;
        var daily = s.DailyRemaining is { } d && d <= 0;
        return daily
            ? new RateLimitExceededException(RateLimitWindows.NextDailyWindow(now), true, "Strava returned 429: daily limit exhausted.")
            : new RateLimitExceededException(RateLimitWindows.NextShortWindow(now), false, "Strava returned 429: 15-minute limit exhausted.");
    }

    internal static (int Short, int Daily)? Read(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        var parts = string.Join(",", values).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) ||
            !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var b)) return null;
        return (a, b);
    }
}
