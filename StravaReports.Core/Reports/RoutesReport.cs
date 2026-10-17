using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Analysis;
using StravaReports.Core.Data;
using StravaReports.Core.Sync;

namespace StravaReports.Core.Reports;

public record RouteTrack(long StravaId, string Name, string SportType, DateTime StartDateLocal, double DistanceM, IReadOnlyList<LatLng> Points);

public record RouteRegion(string Key, LatLng Center, int Count, double DistanceM);

public record RoutesResult(IReadOnlyList<RouteRegion> Regions, RouteRegion? Selected, IReadOnlyList<RouteTrack> Tracks, int WithoutRoute, int Truncated);

/// <summary>
/// Maps of routes from Strava's summary polylines (FR-18). Activities are grouped into areas by start
/// point so a holiday abroad does not shrink the home map to a dot. Works offline: no geocoding,
/// and tiles are optional.
/// </summary>
public class RoutesReport(IDbContextFactory<StravaDbContext> dbFactory) : ReportBase(dbFactory)
{
    public override string Id => "routes";
    public override string Title => "Route maps";
    public override string Description => "Every route in the range drawn on one map, grouped by area.";
    public override string Section => "All sports";
    public override int Order => 55;

    /// <summary>Starts within this distance of an area's first start belong to that area.</summary>
    public const double RegionRadiusM = 60_000;
    public const int MaxTracks = 600;

    public async Task<RoutesResult> RunAsync(ReportFilter filter, string? regionKey = null, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var rows = await db.Activities.AsNoTracking().ApplyFilter(filter)
            .OrderByDescending(a => a.StartDateLocal)
            .Select(a => new { a.StravaId, a.Name, a.SportType, a.StartDateLocal, a.DistanceM, a.SummaryPolyline })
            .ToListAsync(ct);

        var tracks = new List<RouteTrack>();
        var without = 0;
        foreach (var r in rows)
        {
            var points = Polyline.Decode(r.SummaryPolyline);
            if (points.Count < 2) { without++; continue; }
            tracks.Add(new RouteTrack(r.StravaId, r.Name, r.SportType, r.StartDateLocal, r.DistanceM, points));
        }

        // Greedy clustering on start points; newest first, so areas are seeded by recent activity.
        var regions = new List<(LatLng Center, List<RouteTrack> Tracks)>();
        foreach (var t in tracks)
        {
            var region = regions.FirstOrDefault(g => MapLayout.DistanceM(g.Center, t.Points[0]) <= RegionRadiusM);
            if (region.Tracks is null) regions.Add((t.Points[0], [t]));
            else region.Tracks.Add(t);
        }
        var summaries = regions.OrderByDescending(g => g.Tracks.Count)
            .Select(g => new RouteRegion(KeyOf(g.Center), g.Center, g.Tracks.Count, g.Tracks.Sum(t => t.DistanceM)))
            .ToList();
        var selected = summaries.FirstOrDefault(r => r.Key == regionKey) ?? summaries.FirstOrDefault();
        var inRegion = selected is null ? [] : regions.First(g => KeyOf(g.Center) == selected.Key).Tracks;
        return new RoutesResult(summaries, selected, inRegion.Take(MaxTracks).ToList(), without, Math.Max(0, inRegion.Count - MaxTracks));
    }

    public static string KeyOf(LatLng p) => $"{p.Lat:0.000},{p.Lng:0.000}";

    public static string Label(LatLng p) =>
        $"{Math.Abs(p.Lat):0.00}°{(p.Lat >= 0 ? "N" : "S")} {Math.Abs(p.Lng):0.00}°{(p.Lng >= 0 ? "E" : "W")}";
}

/// <summary>The most detailed route available for one activity: latlng stream, then detail polyline, then summary.</summary>
public class ActivityRoutes(IDbContextFactory<StravaDbContext> dbFactory)
{
    public async Task<IReadOnlyList<LatLng>> RouteAsync(Activity a, CancellationToken ct = default)
    {
        if (a.StreamsFetchedAtUtc is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var blob = await db.ActivityStreams.AsNoTracking()
                .Where(s => s.ActivityId == a.Id && s.StreamType == "latlng").Select(s => s.Data).FirstOrDefaultAsync(ct);
            if (blob is not null)
            {
                var points = Polyline.FromLatLngJson(SyncEngine.Decompress(blob));
                if (points.Count >= 2) return points;
            }
        }
        if (a.DetailFetchedAtUtc is not null && !string.IsNullOrEmpty(a.RawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(a.RawJson);
                if (doc.RootElement.TryGetProperty("map", out var map) && map.TryGetProperty("polyline", out var pl) && pl.GetString() is { Length: > 0 } enc)
                    return Polyline.Decode(enc);
            }
            catch (JsonException) { }
        }
        return Polyline.Decode(a.SummaryPolyline);
    }
}
