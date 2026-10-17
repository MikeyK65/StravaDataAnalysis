using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Sync;

namespace StravaReports.Core.Analysis;

/// <summary>
/// Settings-independent facts derived from one activity's streams. Zones and FTP are applied later,
/// so changing them in Settings never needs a recalculation.
/// </summary>
public sealed record StreamSummary(
    IReadOnlyDictionary<int, double> HrHistogram,
    IReadOnlyDictionary<int, double> PowerPeaks,
    double? NormalizedPower,
    double? MaxHr);

/// <summary>Decodes stored streams and caches per-activity summaries so reports stay fast.</summary>
public class StreamAnalysisService(IDbContextFactory<StravaDbContext> dbFactory)
{
    private static readonly string[] SummaryKeys = ["time", "heartrate", "watts", "moving"];
    private readonly ConcurrentDictionary<int, (DateTime Stamp, StreamSummary Summary)> _cache = new();

    /// <summary>Drops cached summaries, e.g. after a restore replaced the database.</summary>
    public void Clear() => _cache.Clear();

    public async Task<StreamData?> LoadAsync(long stravaId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var streams = await db.ActivityStreams.AsNoTracking()
            .Where(s => db.Activities.Any(a => a.Id == s.ActivityId && a.StravaId == stravaId))
            .Select(s => new { s.StreamType, s.Data }).ToListAsync(ct);
        if (streams.Count == 0) return null;
        return StreamData.FromJson(streams.ToDictionary(s => s.StreamType, s => SyncEngine.Decompress(s.Data)));
    }

    /// <summary>Summaries for the given activities that have streams, keyed by Strava ID.</summary>
    public async Task<Dictionary<long, StreamSummary>> SummariesAsync(IReadOnlyCollection<long> stravaIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, StreamSummary>();
        if (stravaIds.Count == 0) return result;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var withStreams = await db.Activities.AsNoTracking()
            .Where(a => a.StreamsFetchedAtUtc != null && stravaIds.Contains(a.StravaId))
            .Select(a => new { a.Id, a.StravaId, Stamp = a.StreamsFetchedAtUtc!.Value })
            .ToListAsync(ct);

        var missing = new List<(int Id, long StravaId, DateTime Stamp)>();
        foreach (var a in withStreams)
        {
            if (_cache.TryGetValue(a.Id, out var hit) && hit.Stamp == a.Stamp) result[a.StravaId] = hit.Summary;
            else missing.Add((a.Id, a.StravaId, a.Stamp));
        }

        // Decode in chunks so a first load of years of streams never holds them all in memory.
        foreach (var chunk in missing.Chunk(50))
        {
            var ids = chunk.Select(c => c.Id).ToList();
            var rows = await db.ActivityStreams.AsNoTracking()
                .Where(s => ids.Contains(s.ActivityId) && SummaryKeys.Contains(s.StreamType))
                .Select(s => new { s.ActivityId, s.StreamType, s.Data }).ToListAsync(ct);
            var byActivity = rows.GroupBy(r => r.ActivityId).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.StreamType, r => SyncEngine.Decompress(r.Data)));
            foreach (var (id, stravaId, stamp) in chunk)
            {
                var data = byActivity.TryGetValue(id, out var json) ? StreamData.FromJson(json) : null;
                var summary = data is null ? Empty : Summarise(data);
                _cache[id] = (stamp, summary);
                result[stravaId] = summary;
            }
        }
        return result;
    }

    private static readonly StreamSummary Empty = new(new Dictionary<int, double>(), new Dictionary<int, double>(), null, null);

    public static StreamSummary Summarise(StreamData d)
    {
        var hist = d.HeartRate is { } hr ? StreamMath.HeartRateHistogram(d.Time, hr, d.Moving) : new Dictionary<int, double>();
        Dictionary<int, double> peaks = [];
        double? np = null;
        if (d.Watts is { } w && w.Any(x => x > 0))
        {
            var perSecond = StreamMath.Resample1Hz(d.Time, w);
            peaks = StreamMath.BestMeans(perSecond, StreamMath.PeakDurations);
            np = StreamMath.NormalizedPower(perSecond);
        }
        double? maxHr = d.HeartRate?.Where(x => x is > 0).Select(x => x!.Value).DefaultIfEmpty().Max();
        return new StreamSummary(hist, peaks, np, maxHr is > 0 ? maxHr : null);
    }
}
