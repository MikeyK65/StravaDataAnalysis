using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StravaReports.Core.Analysis;
using StravaReports.Core.Archive;
using StravaReports.Core.Backup;
using StravaReports.Core.Data;
using StravaReports.Core.Export;
using StravaReports.Core.Logging;
using StravaReports.Core.Reports;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;
using StravaReports.Core.Sync;

namespace StravaReports.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared core. Hosts must also register <see cref="Abstractions.ISecretStore"/>,
    /// <see cref="Abstractions.IAuthLauncher"/> and <see cref="Abstractions.IHostInfo"/>.
    /// </summary>
    public static IServiceCollection AddStravaReportsCore(this IServiceCollection services, string databasePath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        services.AddDbContextFactory<StravaDbContext>(o => o.UseSqlite(cs));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<AppLog>();
        services.AddSingleton<RateLimitTracker>();

        services.AddHttpClient<IStravaClient, StravaClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient(nameof(StravaAuthService), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<StravaAuthService>(sp,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(StravaAuthService))));

        // The engine is a singleton so there is one run lock and one progress feed per process.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SyncEngine>(sp, sp.GetRequiredService<IStravaClient>()));
        services.AddSingleton<SyncStatusService>();
        services.AddSingleton<DataManagementService>();

        services.AddSingleton<ActivityQueries>();
        services.AddReport<DashboardReport>();
        services.AddReport<VolumeTrendsReport>();
        services.AddReport<ConsistencyReport>();
        services.AddReport<RunningReport>();
        services.AddReport<CyclingReport>();
        services.AddReport<GearReport>();

        services.AddSingleton<StreamAnalysisService>();
        services.AddSingleton<ActivityAnalysis>();
        services.AddReport<RecordsReport>();
        services.AddReport<HrZonesReport>();
        services.AddReport<TrainingLoadReport>();
        services.AddReport<PowerReport>();
        services.AddReport<RunningEfficiencyReport>();
        services.AddSingleton<CsvExportService>();

        services.AddReport<RoutesReport>();
        services.AddReport<AllDataReport>();
        services.AddSingleton<ActivityRoutes>();
        services.AddHttpClient(nameof(StravaWebhookService), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<StravaWebhookService>(sp,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(StravaWebhookService))));
        services.AddSingleton<BackupService>();
        services.AddSingleton<TransferCodeService>();
        // No automatic redirects: a redirect means the wrong address or a login page, never a backup.
        services.AddHttpClient<TransferClient>(c => c.Timeout = TimeSpan.FromMinutes(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }

    /// <summary>
    /// Registers the separate archive database that holds Strava bulk-export CSV imports.
    /// </summary>
    public static IServiceCollection AddStravaArchive(this IServiceCollection services, string archiveDatabasePath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = archiveDatabasePath, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        services.AddDbContextFactory<ArchiveDbContext>(o => o.UseSqlite(cs));
        services.AddSingleton<ArchiveService>();
        services.AddSingleton<ArchiveRowSource>();
        return services;
    }

    public static IServiceCollection AddReport<T>(this IServiceCollection services) where T : class, IReport
    {
        services.AddSingleton(sp =>
        {
            var report = ActivatorUtilities.CreateInstance<T>(sp);
            if (report is ReportBase rb) rb.Archive = sp.GetService<ArchiveRowSource>();
            return report;
        });
        services.AddSingleton<IReport>(sp => sp.GetRequiredService<T>());
        return services;
    }

    /// <summary>Applies migrations (both databases) and switches SQLite to WAL so a killed app cannot corrupt the database.</summary>
    public static async Task InitialiseDatabaseAsync(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<StravaDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        if (!await db.SyncStates.AnyAsync())
        {
            db.SyncStates.Add(new SyncState { Id = 1 });
            await db.SaveChangesAsync();
        }

        if (services.GetService<IDbContextFactory<ArchiveDbContext>>() is { } archiveFactory)
        {
            await using var archive = await archiveFactory.CreateDbContextAsync();
            await archive.Database.MigrateAsync();
            await archive.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
    }
}
