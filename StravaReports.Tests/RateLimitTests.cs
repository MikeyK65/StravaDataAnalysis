using Microsoft.Extensions.Time.Testing;
using StravaReports.Core.Strava;

namespace StravaReports.Tests;

public class RateLimitTests
{
    private static HttpResponseMessage Response(string limit, string usage, bool read = false)
    {
        var r = new HttpResponseMessage();
        var prefix = read ? "X-ReadRateLimit" : "X-RateLimit";
        r.Headers.Add($"{prefix}-Limit", limit);
        r.Headers.Add($"{prefix}-Usage", usage);
        return r;
    }

    [Fact]
    public void Parses_headers_and_prefers_read_limits()
    {
        var tracker = new RateLimitTracker(new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 10, 2, 0, TimeSpan.Zero)));
        var resp = Response("200,2000", "10,500");
        resp.Headers.Add("X-ReadRateLimit-Limit", "100,1000");
        resp.Headers.Add("X-ReadRateLimit-Usage", "7,300");
        tracker.Update(resp.Headers);

        var s = tracker.Current;
        Assert.Equal((100, 7, 1000, 300), (s.ShortLimit, s.ShortUsage, s.DailyLimit, s.DailyUsage));
        Assert.Equal(93, s.ShortRemaining);
    }

    [Fact]
    public void Stops_near_the_15_minute_limit_and_resumes_next_window()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 10, 7, 30, TimeSpan.Zero));
        var tracker = new RateLimitTracker(time);
        tracker.Update(Response("100,1000", "97,300").Headers);

        var ex = Assert.Throws<RateLimitExceededException>(tracker.EnsureBudget);
        Assert.False(ex.Daily);
        Assert.Equal(new DateTime(2026, 10, 16, 10, 15, 0, DateTimeKind.Utc), ex.ResumeAfterUtc);

        // Usage resets at the next 15-minute mark.
        time.SetUtcNow(new DateTimeOffset(2026, 10, 16, 10, 15, 1, TimeSpan.Zero));
        tracker.EnsureBudget();
        Assert.Equal(0, tracker.Current.ShortUsage);
    }

    [Fact]
    public void Stops_near_the_daily_limit_until_midnight_utc()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 22, 0, 0, TimeSpan.Zero));
        var tracker = new RateLimitTracker(time);
        tracker.Update(Response("100,1000", "5,995").Headers);

        var ex = Assert.Throws<RateLimitExceededException>(tracker.EnsureBudget);
        Assert.True(ex.Daily);
        Assert.Equal(new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc), ex.ResumeAfterUtc);
    }

    [Fact]
    public void Missing_headers_leave_budget_unknown()
    {
        var tracker = new RateLimitTracker();
        tracker.Update(new HttpResponseMessage().Headers);
        Assert.Null(tracker.Current.ShortLimit);
        tracker.EnsureBudget(); // no throw
    }
}
