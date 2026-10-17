using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;
using StravaReports.Core.Logging;
using StravaReports.Core.Reports;

namespace StravaReports.Core.Archive;

public record ImportProgress(string Stage, int Done, int Total);

public record ImportResult(bool Success, ImportBatch Batch);

public record ArchiveYear(int Year, int Count, double DistanceM, int MovingTimeS, double ElevationGainM, int Runs, int Rides);

public record ArchiveSummary(int Activities, DateTime? First, DateTime? Last, double DistanceM, int MovingTimeS,
    int AlsoSynced, int OnlyInArchive, int SyncedNotInArchive, IReadOnlyList<ArchiveYear> Years);

public record ArchiveRow(long ActivityId, string Name, string ActivityType, string SportType, DateTime? StartDateUtc,
    double? DistanceM, int? MovingTimeS, double? ElevationGainM, double? AvgHr, string? GearName, DateTime UpdatedUtc, int FirstBatchId, int LastBatchId);

/// <summary>
/// Imports Strava's bulk-export activities.csv (or the whole export .zip) into the archive database.
/// Activities are keyed on Strava's activity ID, so loading the same or a newer export again inserts
/// only new activities, updates changed ones and never creates duplicates.
/// </summary>
public class ArchiveService(IDbContextFactory<ArchiveDbContext> archiveFactory, IDbContextFactory<StravaDbContext> syncedFactory, AppLog log, TimeProvider time)
{
    private readonly SemaphoreSlim _importLock = new(1, 1);
    private const int SaveEvery = 500;

    public bool IsImporting => _importLock.CurrentCount == 0;

    public event Action? Changed;

    public async Task<ImportResult> ImportAsync(Stream input, string fileName, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        if (!await _importLock.WaitAsync(0, ct))
            return new ImportResult(false, new ImportBatch { FileName = fileName, Status = "Failed", Message = "Another import is already running." });

        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        // Only one import runs per process, so a batch still marked Running was cut off (app closed, page left).
        await db.Batches.Where(b => b.Status == "Running")
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, "Interrupted").SetProperty(b => b.Message, "The import did not finish; nothing from it was saved."), ct);
        var batch = new ImportBatch { FileName = Path.GetFileName(fileName), StartedUtc = Now, Status = "Running" };
        db.Batches.Add(batch);
        await db.SaveChangesAsync(ct);
        var tempFile = Path.Combine(Path.GetTempPath(), $"strava-import-{Guid.NewGuid():N}.tmp");
        try
        {
            // Spool to disk asynchronously first: browser upload streams (Blazor Server) cannot be read
            // synchronously, zips need seeking, and parsing then never holds the UI thread.
            progress?.Report(new ImportProgress("Uploading", 0, 0));
            await using (var fs = File.Create(tempFile)) await input.CopyToAsync(fs, ct);

            progress?.Report(new ImportProgress("Reading file", 0, 0));
            var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var parsed = await Task.Run(() => ParseFile(tempFile, isZip, ct), ct);
            var rows = parsed.Rows;
            (batch.RowsRead, batch.Skipped, batch.DuplicatesInFile) = (parsed.RowsRead, parsed.Skipped, parsed.Duplicates);

            var existing = await db.Activities.AsNoTracking()
                .Select(a => new { a.Id, a.ActivityId, a.RowHash }).ToDictionaryAsync(a => a.ActivityId, ct);
            var now = Now;
            var toUpdate = new List<ExportRow>();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var done = 0;
            foreach (var row in rows.Values)
            {
                if (existing.TryGetValue(row.ActivityId, out var e))
                {
                    if (e.RowHash == row.Hash) batch.Unchanged++;
                    else toUpdate.Add(row);
                    continue;
                }
                var a = row.Values;
                a.RawJson = row.RawJson;
                a.RowHash = row.Hash;
                a.FirstBatchId = a.LastBatchId = batch.Id;
                a.CreatedUtc = a.UpdatedUtc = now;
                db.Activities.Add(a);
                batch.Inserted++;
                if (++done % SaveEvery == 0)
                {
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    db.Attach(batch);
                    progress?.Report(new ImportProgress("Saving", done, rows.Count));
                }
            }

            foreach (var chunk in toUpdate.Chunk(SaveEvery))
            {
                var ids = chunk.Select(r => r.ActivityId).ToList();
                var tracked = await db.Activities.Where(a => ids.Contains(a.ActivityId)).ToDictionaryAsync(a => a.ActivityId, ct);
                foreach (var row in chunk)
                {
                    var target = tracked[row.ActivityId];
                    var v = row.Values;
                    (v.Id, v.FirstBatchId, v.CreatedUtc) = (target.Id, target.FirstBatchId, target.CreatedUtc);
                    (v.RawJson, v.RowHash, v.LastBatchId, v.UpdatedUtc) = (row.RawJson, row.Hash, batch.Id, now);
                    db.Entry(target).CurrentValues.SetValues(v);
                    batch.Updated++;
                }
                await db.SaveChangesAsync(ct);
                done += chunk.Length;
                progress?.Report(new ImportProgress("Updating", done, rows.Count));
            }

            batch.Status = "Completed";
            batch.FinishedUtc = Now;
            batch.Message = $"{batch.Inserted} new, {batch.Updated} updated, {batch.Unchanged} unchanged";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await log.InfoAsync("Archive", $"Imported {batch.FileName}: {batch.Message}, {batch.Skipped} skipped.");
            return new ImportResult(true, batch);
        }
        catch (Exception ex)
        {
            // The transaction rolled back, so the archive is exactly as it was; only the failed batch is recorded.
            await using var fail = await archiveFactory.CreateDbContextAsync(CancellationToken.None);
            var row = await fail.Batches.FindAsync(batch.Id);
            if (row is not null)
            {
                row.Status = ex is OperationCanceledException ? "Cancelled" : "Failed";
                row.Message = ex is OperationCanceledException ? "Cancelled." : ex.Message;
                row.FinishedUtc = Now;
                row.RowsRead = batch.RowsRead;
                (row.Inserted, row.Updated, row.Unchanged) = (0, 0, 0);
                await fail.SaveChangesAsync(CancellationToken.None);
                batch = row;
            }
            await log.WarnAsync("Archive", $"Import of {Path.GetFileName(fileName)} {batch.Status.ToLowerInvariant()}: {batch.Message}");
            if (ex is not (FormatException or InvalidDataException or IOException or DbUpdateException or OperationCanceledException))
                await log.ErrorAsync("Archive", $"Unexpected import error: {ex.GetType().Name}");
            return new ImportResult(false, batch);
        }
        finally
        {
            try { File.Delete(tempFile); } catch (IOException) { }
            _importLock.Release();
            Changed?.Invoke();
        }
    }

    private sealed record ParsedFile(Dictionary<long, ExportRow> Rows, int RowsRead, int Skipped, int Duplicates);

    private static ParsedFile ParseFile(string path, bool isZip, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        using var zip = isZip ? new ZipArchive(file, ZipArchiveMode.Read) : null;
        var csv = zip is null ? file : (zip.Entries
            .Where(e => e.Name.Equals("activities.csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/'))
            .FirstOrDefault()
            ?? throw new FormatException("The zip has no activities.csv. Use the archive from Strava's \"Download your data\".")).Open();

        var rows = new Dictionary<long, ExportRow>();
        int read = 0, skipped = 0, duplicates = 0;
        using var reader = new StreamReader(csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        StravaExportCsv? map = null;
        foreach (var record in CsvReader.Read(reader))
        {
            ct.ThrowIfCancellationRequested();
            if (map is null) { map = new StravaExportCsv(record); continue; }
            if (record.Count == 1 && string.IsNullOrWhiteSpace(record[0])) continue;
            read++;
            var row = map.Parse(record);
            if (row is null) { skipped++; continue; }
            if (rows.ContainsKey(row.ActivityId)) duplicates++;
            rows[row.ActivityId] = row;
        }
        if (map is null) throw new FormatException("The file is empty.");
        return new ParsedFile(rows, read, skipped, duplicates);
    }

    public async Task<List<ImportBatch>> BatchesAsync(CancellationToken ct = default)
    {
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        return await db.Batches.AsNoTracking().OrderByDescending(b => b.Id).Take(50).ToListAsync(ct);
    }

    public async Task<ArchiveSummary> SummaryAsync(CancellationToken ct = default)
    {
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        var slim = await db.Activities.AsNoTracking()
            .Select(a => new { a.ActivityId, a.SportType, a.StartDateUtc, a.DistanceM, a.MovingTimeS, a.ElevationGainM }).ToListAsync(ct);

        await using var synced = await syncedFactory.CreateDbContextAsync(ct);
        var syncedIds = (await synced.Activities.Select(a => a.StravaId).ToListAsync(ct)).ToHashSet();
        var archiveIds = slim.Select(a => a.ActivityId).ToHashSet();
        var both = archiveIds.Count(syncedIds.Contains);

        var years = slim.Where(a => a.StartDateUtc is not null).GroupBy(a => a.StartDateUtc!.Value.Year).OrderByDescending(g => g.Key)
            .Select(g => new ArchiveYear(g.Key, g.Count(), g.Sum(a => a.DistanceM ?? 0), g.Sum(a => a.MovingTimeS ?? 0), g.Sum(a => a.ElevationGainM ?? 0),
                g.Count(a => SportGroups.Classify(a.SportType) == SportGroup.Run), g.Count(a => SportGroups.Classify(a.SportType) == SportGroup.Ride)))
            .ToList();
        return new ArchiveSummary(slim.Count, slim.Min(a => a.StartDateUtc), slim.Max(a => a.StartDateUtc),
            slim.Sum(a => a.DistanceM ?? 0), slim.Sum(a => a.MovingTimeS ?? 0),
            both, archiveIds.Count - both, syncedIds.Count - both, years);
    }

    public async Task<(List<ArchiveRow> Rows, int Total)> ListAsync(string? search, string? sportType, int? year, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        var q = db.Activities.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = long.TryParse(term, out var id) ? q.Where(a => a.ActivityId == id) : q.Where(a => EF.Functions.Like(a.Name, $"%{term}%"));
        }
        if (!string.IsNullOrEmpty(sportType)) q = q.Where(a => a.SportType == sportType);
        if (year is { } y)
        {
            var start = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            q = q.Where(a => a.StartDateUtc >= start && a.StartDateUtc < start.AddYears(1));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.StartDateUtc).Skip(skip).Take(take)
            .Select(a => new ArchiveRow(a.ActivityId, a.Name, a.ActivityType, a.SportType, a.StartDateUtc, a.DistanceM, a.MovingTimeS, a.ElevationGainM,
                a.AvgHr, a.GearName, a.UpdatedUtc, a.FirstBatchId, a.LastBatchId))
            .ToListAsync(ct);
        return (rows, total);
    }

    /// <summary>All exported columns of one activity, in file order.</summary>
    public async Task<IReadOnlyList<KeyValuePair<string, string>>> RawAsync(long activityId, CancellationToken ct = default)
    {
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        var json = await db.Activities.Where(a => a.ActivityId == activityId).Select(a => a.RawJson).FirstOrDefaultAsync(ct);
        if (json is null) return [];
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        return map.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
    }

    public async Task<List<string>> SportTypesAsync(CancellationToken ct = default)
    {
        await using var db = await archiveFactory.CreateDbContextAsync(ct);
        return await db.Activities.Select(a => a.SportType).Distinct().OrderBy(x => x).ToListAsync(ct);
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await using (var db = await archiveFactory.CreateDbContextAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Activities.ExecuteDeleteAsync(ct);
            await db.Batches.ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
        }
        await log.InfoAsync("Archive", "Archive database emptied.");
        Changed?.Invoke();
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;
}
