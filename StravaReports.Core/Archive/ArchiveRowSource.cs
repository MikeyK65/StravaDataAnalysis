using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Reports;

namespace StravaReports.Core.Archive;

/// <summary>
/// Supplies archive activities to the reports when the filter asks for them. Only activities that are
/// not in the synced database are added, so nothing is counted twice. The export has no local time,
/// gear ID or trainer flag, so archive rows use the UTC start and count virtual rides and runs as trainer
/// activities. The export names its gear ("Activity Gear"); a name that matches a synced gear item takes
/// that item's ID, so one filter covers both. Any other name becomes the synthetic ID "name:{name}".
/// </summary>
public class ArchiveRowSource
{
    private static readonly string[] Virtual = ["VirtualRide", "VirtualRun"];
    public const string NamePrefix = "name:";
    private readonly IDbContextFactory<ArchiveDbContext> archiveFactory;
    private readonly IDbContextFactory<StravaDbContext> mainFactory;
    private readonly TimeProvider time;
    private (int Count, DateTime At)? _count;

    public ArchiveRowSource(IDbContextFactory<ArchiveDbContext> archiveFactory, IDbContextFactory<StravaDbContext> mainFactory, TimeProvider time, ArchiveService archive)
    {
        this.archiveFactory = archiveFactory;
        this.mainFactory = mainFactory;
        this.time = time;
        archive.Changed += Invalidate;
    }

    /// <summary>Cached for a minute; used to decide whether to show the filter option at all.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_count is { } c && now - c.At < TimeSpan.FromMinutes(1)) return c.Count;
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        var n = await db.Activities.CountAsync(ct);
        _count = (n, now);
        return n;
    }

    public void Invalidate() => _count = null;

    /// <summary>Gear names that appear only in the archive (older bikes and shoes the API never returned), for the filter list.</summary>
    public async Task<List<string>> GearNamesAsync(CancellationToken ct = default)
    {
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        var names = await db.Activities.Where(a => a.GearName != null && a.GearName != "").Select(a => a.GearName!).Distinct().ToListAsync(ct);
        if (names.Count == 0) return names;
        await using var main = await mainFactory.CreateDbContextAsync(ct);
        var known = (await main.Gear.Select(g => g.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. names.Where(n => !known.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<List<ActivityRow>> RowsAsync(ReportFilter f, CancellationToken ct = default)
    {
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        var q = db.Activities.AsNoTracking().Where(a => a.StartDateUtc != null);
        if (f.From is { } from) { var s = from.ToDateTime(TimeOnly.MinValue); q = q.Where(a => a.StartDateUtc >= s); }
        if (f.To is { } to) { var e = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(a => a.StartDateUtc < e); }
        string[] runAndRide = [.. SportGroups.Run, .. SportGroups.Ride];
        q = f.Group switch
        {
            SportGroup.Run => q.Where(a => SportGroups.Run.Contains(a.SportType)),
            SportGroup.Ride => q.Where(a => SportGroups.Ride.Contains(a.SportType)),
            SportGroup.Other => q.Where(a => !runAndRide.Contains(a.SportType)),
            _ => q,
        };
        if (!string.IsNullOrEmpty(f.SportType)) q = q.Where(a => a.SportType == f.SportType);
        if (!f.IncludeCommute) q = q.Where(a => a.IsCommute != true);
        if (!f.IncludeTrainer) q = q.Where(a => !Virtual.Contains(a.SportType));

        var rows = await q.Select(a => new
        {
            a.ActivityId, a.Name, a.SportType, a.StartDateUtc, a.DistanceM, a.MovingTimeS, a.ElapsedTimeS, a.ElevationGainM,
            a.AvgSpeed, a.AvgHr, a.MaxHr, a.AvgWatts, a.WeightedAvgWatts, a.AvgCadence, a.RelativeEffort, a.IsCommute, a.GearName,
        }).ToListAsync(ct);
        if (rows.Count == 0) return [];

        await using var main = await mainFactory.CreateDbContextAsync(ct);
        var synced = (await main.Activities.Select(a => a.StravaId).ToListAsync(ct)).ToHashSet();
        var gearByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in await main.Gear.AsNoTracking().OrderBy(g => g.Id).ToListAsync(ct)) gearByName.TryAdd(g.Name, g.StravaId);
        string? GearIdOf(string? name) => string.IsNullOrWhiteSpace(name) ? null : gearByName.GetValueOrDefault(name.Trim()) ?? NamePrefix + name.Trim();

        return rows.Where(a => !synced.Contains(a.ActivityId) && (string.IsNullOrEmpty(f.GearId) || GearIdOf(a.GearName) == f.GearId)).Select(a =>
        {
            var moving = a.MovingTimeS ?? a.ElapsedTimeS ?? 0;
            var distance = a.DistanceM ?? 0;
            return new ActivityRow
            {
                StravaId = a.ActivityId,
                Name = a.Name,
                SportType = a.SportType,
                StartDateLocal = DateTime.SpecifyKind(a.StartDateUtc!.Value, DateTimeKind.Unspecified),
                DistanceM = distance,
                MovingTimeS = moving,
                ElapsedTimeS = a.ElapsedTimeS ?? moving,
                ElevationGainM = a.ElevationGainM ?? 0,
                AvgSpeed = a.AvgSpeed ?? (moving > 0 ? distance / moving : 0),
                AvgHr = a.AvgHr is > 0 ? a.AvgHr : null,
                MaxHr = a.MaxHr is > 0 ? a.MaxHr : null,
                AvgWatts = a.AvgWatts is > 0 ? a.AvgWatts : null,
                WeightedAvgWatts = a.WeightedAvgWatts is > 0 ? a.WeightedAvgWatts : null,
                AvgCadence = a.AvgCadence is > 0 ? a.AvgCadence : null,
                SufferScore = a.RelativeEffort,
                IsCommute = a.IsCommute == true,
                GearId = GearIdOf(a.GearName),
                IsTrainer = Virtual.Contains(a.SportType),
                FromArchive = true,
            };
        }).ToList();
    }
}
