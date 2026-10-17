using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StravaReports.Core.Data;

namespace StravaReports.Core.Logging;

/// <summary>
/// Persists sync and API events to the LogEntry table so they can be viewed on the Logs page,
/// and mirrors them to the host's ILogger. Never pass secrets or tokens to this class.
/// </summary>
public class AppLog(IDbContextFactory<StravaDbContext> dbFactory, ILogger<AppLog> logger)
{
    private const int MaxRows = 2000;

    public Task InfoAsync(string category, string message) => WriteAsync(LogLevel.Information, category, message);
    public Task WarnAsync(string category, string message) => WriteAsync(LogLevel.Warning, category, message);
    public Task ErrorAsync(string category, string message) => WriteAsync(LogLevel.Error, category, message);

    public async Task WriteAsync(LogLevel level, string category, string message)
    {
        logger.Log(level, "[{Category}] {Message}", category, message);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.LogEntries.Add(new LogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Level = level.ToString(),
                Category = category,
                Message = message.Length > 2000 ? message[..2000] : message,
            });
            await db.SaveChangesAsync();

            // Keep the table bounded.
            var count = await db.LogEntries.CountAsync();
            if (count > MaxRows + 200)
            {
                var cutoff = await db.LogEntries.OrderByDescending(x => x.Id).Skip(MaxRows).Select(x => x.Id).FirstAsync();
                await db.LogEntries.Where(x => x.Id <= cutoff).ExecuteDeleteAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist log entry");
        }
    }

    public async Task<List<LogEntry>> RecentAsync(int take = 300, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LogEntries.AsNoTracking().OrderByDescending(x => x.Id).Take(take).ToListAsync(ct);
    }
}
