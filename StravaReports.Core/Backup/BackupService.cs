using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StravaReports.Core.Analysis;
using StravaReports.Core.Archive;
using StravaReports.Core.Data;
using StravaReports.Core.Logging;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;
using StravaReports.Core.Sync;

namespace StravaReports.Core.Backup;

public record BackupOptions(bool IncludeArchive = true, bool IncludeStreams = true);

public record BackupManifest(
    string Format, int Version, DateTime CreatedUtc, string Platform,
    string? MainMigration, string? ArchiveMigration,
    int Activities, int ArchivedActivities, bool IncludesStreams,
    long? AthleteId, string? AthleteName);

/// <summary>A backup written to a temporary file. The caller hands it to the user and then deletes it.</summary>
public record BackupFile(string Path, string FileName, long Bytes, BackupManifest Manifest);

public record BackupInspection(BackupManifest? Manifest, bool HasArchive, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Manifest is not null && Errors.Count == 0;
}

public record BackupRestoreOptions(bool KeepDeviceSettings = true, bool RestoreArchive = true);

public record RestoreResult(int Activities, int ArchivedActivities, bool ArchiveRestored, string SafetyCopyPath);

/// <summary>
/// Backup and restore of the local databases (FR-16). A backup is a zip holding a manifest and
/// consistent SQLite snapshots taken with SQLite's online backup API. Secrets never live in the
/// databases, so a backup holds no tokens or passwords. Restoring a web backup in the MAUI app is
/// how the web database is imported into the app.
/// </summary>
public class BackupService(
    IDbContextFactory<StravaDbContext> dbFactory,
    IServiceProvider services,
    SyncEngine engine,
    AppSettingsService settings,
    StreamAnalysisService streams,
    AppLog log,
    TimeProvider time)
{
    public const string FormatName = "strava-reports-backup";
    public const int FormatVersion = 1;
    public const string ManifestEntry = "manifest.json";
    public const string MainEntry = "strava.db";
    public const string ArchiveEntry = "strava-archive.db";
    public const int SafetyCopiesKept = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Raised after a restore so open views can reload.</summary>
    public event Action? Restored;

    private IDbContextFactory<ArchiveDbContext>? ArchiveFactory => services.GetService<IDbContextFactory<ArchiveDbContext>>();

    public async Task<BackupFile> CreateAsync(BackupOptions options, CancellationToken ct = default)
    {
        SweepStaleFiles();
        var work = NewWorkDir();
        try
        {
            var mainCopy = Path.Combine(work, MainEntry);
            await SnapshotAsync(await MainConnectionStringAsync(ct), mainCopy, ct);
            await using (var conn = Open(mainCopy))
            {
                // Host-specific and transient rows stay behind; activities, gear, settings and sync state go.
                await ExecAsync(conn, "DELETE FROM WebhookEvents; DELETE FROM AppSettings WHERE Key LIKE 'webhook.%';", ct);
                if (!options.IncludeStreams)
                    await ExecAsync(conn, $"DELETE FROM ActivityStreams; UPDATE Activities SET StreamsFetchedAtUtc = NULL; DELETE FROM SyncJobs WHERE JobType = {(int)SyncJobType.Streams};", ct);
                await ExecAsync(conn, "PRAGMA journal_mode=DELETE; VACUUM;", ct);
            }

            string? archiveCopy = null;
            if (options.IncludeArchive && ArchiveFactory is { } af)
            {
                archiveCopy = Path.Combine(work, ArchiveEntry);
                await using var adb = await af.CreateDbContextAsync(ct);
                await SnapshotAsync(adb.Database.GetConnectionString()!, archiveCopy, ct);
                await using var conn = Open(archiveCopy);
                await ExecAsync(conn, "PRAGMA journal_mode=DELETE;", ct);
            }

            var manifest = await DescribeAsync(mainCopy, archiveCopy, options.IncludeStreams, ct);
            var fileName = $"strava-reports-backup-{time.GetLocalNow():yyyyMMdd-HHmm}.zip";
            var zipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");
            SqliteConnection.ClearAllPools();
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                await using (var m = zip.CreateEntry(ManifestEntry).Open()) await JsonSerializer.SerializeAsync(m, manifest, Json, ct);
                zip.CreateEntryFromFile(mainCopy, MainEntry, CompressionLevel.Optimal);
                if (archiveCopy is not null) zip.CreateEntryFromFile(archiveCopy, ArchiveEntry, CompressionLevel.Optimal);
            }
            var bytes = new FileInfo(zipPath).Length;
            await log.InfoAsync("Backup", $"Backup created: {manifest.Activities} activities, {manifest.ArchivedActivities} archived, {bytes / 1024.0 / 1024:0.0} MB.");
            return new BackupFile(zipPath, fileName, bytes, manifest);
        }
        finally { DeleteDir(work); }
    }

    /// <summary>Checks a backup without changing anything: format, integrity, schema version and athlete.</summary>
    public async Task<BackupInspection> InspectAsync(string zipPath, CancellationToken ct = default)
    {
        var work = NewWorkDir();
        try { return await InspectIntoAsync(zipPath, work, ct); }
        finally { DeleteDir(work); }
    }

    public async Task<RestoreResult> RestoreAsync(string zipPath, BackupRestoreOptions options, CancellationToken ct = default)
    {
        var work = NewWorkDir();
        try
        {
            var inspection = await InspectIntoAsync(zipPath, work, ct);
            if (!inspection.IsValid) throw new InvalidDataException(string.Join(" ", inspection.Errors));

            await using var exclusive = await engine.AcquireExclusiveAsync(ct);
            var mainCs = await MainConnectionStringAsync(ct);

            // A safety copy of what is about to be replaced, kept next to the database.
            var safety = await SafetyCopyAsync(mainCs, ct);

            List<AppSetting> keep = [];
            if (options.KeepDeviceSettings)
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                    keep = await db.AppSettings.AsNoTracking().ToListAsync(ct);

            await CopyIntoAsync(Path.Combine(work, MainEntry), mainCs, ct);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                await db.Database.MigrateAsync(ct);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
                if (options.KeepDeviceSettings)
                {
                    await db.AppSettings.ExecuteDeleteAsync(ct);
                    db.AppSettings.AddRange(keep);
                }
                else await db.AppSettings.Where(s => s.Key.StartsWith("webhook.")).ExecuteDeleteAsync(ct);
                await db.SaveChangesAsync(ct);
                if (!await db.SyncStates.AnyAsync(ct)) { db.SyncStates.Add(new SyncState { Id = 1 }); await db.SaveChangesAsync(ct); }
            }

            var archiveRestored = false;
            if (options.RestoreArchive && inspection.HasArchive && ArchiveFactory is { } af)
            {
                await using var adb = await af.CreateDbContextAsync(ct);
                await CopyIntoAsync(Path.Combine(work, ArchiveEntry), adb.Database.GetConnectionString()!, ct);
                await using var fresh = await af.CreateDbContextAsync(ct);
                await fresh.Database.MigrateAsync(ct);
                await fresh.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
                archiveRestored = true;
            }

            settings.Invalidate();
            streams.Clear();
            services.GetService<StravaWebhookService>()?.Invalidate();

            int activities, archived = 0;
            await using (var db = await dbFactory.CreateDbContextAsync(ct)) activities = await db.Activities.CountAsync(ct);
            if (ArchiveFactory is { } af2) { await using var adb = await af2.CreateDbContextAsync(ct); archived = await adb.Activities.CountAsync(ct); }

            await log.InfoAsync("Backup", $"Restored backup from {inspection.Manifest!.CreatedUtc:u} ({inspection.Manifest.Platform}): {activities} activities" +
                (archiveRestored ? $", {archived} archived" : "") + $". Previous data saved to {Path.GetFileName(safety)}.");
            Restored?.Invoke();
            return new RestoreResult(activities, archived, archiveRestored, safety);
        }
        finally { DeleteDir(work); }
    }

    // ------------------------------------------------------------------ internals

    private async Task<BackupInspection> InspectIntoAsync(string zipPath, string work, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        BackupManifest? manifest = null;
        var hasArchive = false;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var m = zip.GetEntry(ManifestEntry);
            var main = zip.GetEntry(MainEntry);
            if (m is null || main is null)
                return new BackupInspection(null, false, ["This is not a Strava Reports backup (manifest or database missing)."], []);
            await using (var s = m.Open()) manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(s, Json, ct);
            if (manifest?.Format != FormatName) errors.Add("This is not a Strava Reports backup.");
            else if (manifest.Version > FormatVersion) errors.Add("This backup was made by a newer version of the app. Update the app first.");

            main.ExtractToFile(Path.Combine(work, MainEntry), overwrite: true);
            if (zip.GetEntry(ArchiveEntry) is { } a)
            {
                a.ExtractToFile(Path.Combine(work, ArchiveEntry), overwrite: true);
                hasArchive = true;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return new BackupInspection(null, false, [$"The file could not be read as a backup: {ex.Message}"], []);
        }
        if (errors.Count > 0) return new BackupInspection(manifest, hasArchive, errors, warnings);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await CheckDatabaseAsync(Path.Combine(work, MainEntry), db.Database.GetMigrations().ToHashSet(), "Activities", "main", errors, ct);
        if (hasArchive && ArchiveFactory is { } af)
        {
            await using var adb = await af.CreateDbContextAsync(ct);
            await CheckDatabaseAsync(Path.Combine(work, ArchiveEntry), adb.Database.GetMigrations().ToHashSet(), "ArchivedActivities", "archive", errors, ct);
        }
        else if (hasArchive) warnings.Add("The backup includes an export archive, which this app does not use; it will be skipped.");

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var current = await db.Athletes.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.StravaId, Name = a.FirstName + " " + a.LastName }).FirstOrDefaultAsync(ct);
            if (current is not null && manifest!.AthleteId is { } other && other != current.StravaId)
                warnings.Add($"The backup belongs to athlete {manifest.AthleteName ?? other.ToString()}, but this device is connected as {current.Name.Trim()}. Restoring mixes another athlete's data with this connection.");
            var count = await db.Activities.CountAsync(ct);
            if (count > manifest!.Activities)
                warnings.Add($"This device has {count:N0} activities and the backup has {manifest.Activities:N0}. Activities missing from the backup are removed until the next sync fetches them again.");
        }
        if (!manifest!.IncludesStreams) warnings.Add("The backup has no streams. Charts that need streams fill in again as sync re-downloads them, if streams are turned on.");
        return new BackupInspection(manifest, hasArchive, errors, warnings);
    }

    private static async Task CheckDatabaseAsync(string path, HashSet<string> known, string requiredTable, string label, List<string> errors, CancellationToken ct)
    {
        try
        {
            await using var conn = Open(path, readOnly: true);
            if (await ScalarAsync(conn, "PRAGMA quick_check;", ct) is not "ok") { errors.Add($"The {label} database in the backup is damaged."); return; }
            if (await ScalarAsync(conn, $"SELECT name FROM sqlite_master WHERE type='table' AND name='{requiredTable}';", ct) is null)
            { errors.Add($"The {label} database in the backup is not a Strava Reports database."); return; }
            var applied = new List<string>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory;";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) applied.Add(r.GetString(0));
            }
            if (applied.Any(m => !known.Contains(m)))
                errors.Add($"The {label} database was made by a newer version of the app. Update the app first.");
        }
        catch (SqliteException ex) { errors.Add($"The {label} database in the backup could not be opened: {ex.Message}"); }
    }

    private async Task<BackupManifest> DescribeAsync(string mainCopy, string? archiveCopy, bool streamsIncluded, CancellationToken ct)
    {
        await using var conn = Open(mainCopy, readOnly: true);
        var activities = Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM Activities;", ct));
        var migration = await ScalarAsync(conn, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;", ct) as string;
        long? athleteId = await ScalarAsync(conn, "SELECT StravaId FROM Athletes ORDER BY Id LIMIT 1;", ct) is long id ? id : null;
        var athleteName = (await ScalarAsync(conn, "SELECT FirstName || ' ' || LastName FROM Athletes ORDER BY Id LIMIT 1;", ct) as string)?.Trim();
        int archived = 0;
        string? archiveMigration = null;
        if (archiveCopy is not null)
        {
            await using var a = Open(archiveCopy, readOnly: true);
            archived = Convert.ToInt32(await ScalarAsync(a, "SELECT COUNT(*) FROM ArchivedActivities;", ct));
            archiveMigration = await ScalarAsync(a, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;", ct) as string;
        }
        return new BackupManifest(FormatName, FormatVersion, time.GetUtcNow().UtcDateTime, services.GetService<Abstractions.IHostInfo>()?.PlatformName ?? "", migration, archiveMigration,
            activities, archived, streamsIncluded, athleteId, string.IsNullOrEmpty(athleteName) ? null : athleteName);
    }

    private async Task<string> MainConnectionStringAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return db.Database.GetConnectionString()!;
    }

    private static async Task SnapshotAsync(string sourceCs, string destPath, CancellationToken ct)
    {
        await using var src = new SqliteConnection(sourceCs);
        await using var dst = Open(destPath);
        await src.OpenAsync(ct);
        await RetryBusyAsync(() => src.BackupDatabase(dst), ct);
    }

    /// <summary>Replaces the live database's contents page by page; other connections see the new data.</summary>
    private static async Task CopyIntoAsync(string sourcePath, string destCs, CancellationToken ct)
    {
        SqliteConnection.ClearAllPools();
        await using (var src = Open(sourcePath, readOnly: true))
        await using (var dst = new SqliteConnection(destCs))
        {
            await dst.OpenAsync(ct);
            await RetryBusyAsync(() => src.BackupDatabase(dst), ct);
        }
        SqliteConnection.ClearAllPools();
    }

    private async Task<string> SafetyCopyAsync(string mainCs, CancellationToken ct)
    {
        var dataSource = new SqliteConnectionStringBuilder(mainCs).DataSource;
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataSource))!, "backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"pre-restore-{time.GetLocalNow():yyyyMMdd-HHmmss}.db");
        await SnapshotAsync(mainCs, path, ct);
        await using (var conn = Open(path)) await ExecAsync(conn, "PRAGMA journal_mode=DELETE;", ct);
        SqliteConnection.ClearAllPools();
        foreach (var old in new DirectoryInfo(dir).GetFiles("pre-restore-*.db").OrderByDescending(f => f.Name).Skip(SafetyCopiesKept))
            try { old.Delete(); } catch (IOException) { }
        return path;
    }

    private static async Task RetryBusyAsync(Action action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 10) // BUSY, LOCKED
            {
                await Task.Delay(200 * attempt, ct);
            }
        }
    }

    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    /// <summary>
    /// Backups are personal data. If the process was killed before a backup was handed over (or a transfer
    /// code was used), the temp file would linger, so anything older than a day is removed.
    /// </summary>
    private static void SweepStaleFiles()
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (var f in new DirectoryInfo(Path.GetTempPath()).EnumerateFiles("*-strava-reports-backup-*.zip"))
                if (f.LastWriteTimeUtc < cutoff) try { f.Delete(); } catch (IOException) { }
            var work = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "stravareports-backup"));
            if (work.Exists)
                foreach (var d in work.EnumerateDirectories())
                    if (d.LastWriteTimeUtc < cutoff) try { d.Delete(recursive: true); } catch (IOException) { }
        }
        catch (UnauthorizedAccessException) { }
    }

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stravareports-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
