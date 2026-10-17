using ApexCharts;
using Microsoft.Extensions.Logging;
using StravaReports.Core;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;
using StravaReports.Core.Sync;
using StravaReports.Services;
using StravaReports.Shared.Services;

namespace StravaReports;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // On-device SQLite database; each platform keeps its own copy in v1.
        builder.Services.AddStravaReportsCore(Path.Combine(FileSystem.AppDataDirectory, "strava.db"));
        builder.Services.AddStravaArchive(Path.Combine(FileSystem.AppDataDirectory, "strava-archive.db"));
        builder.Services.AddStravaReportsUi();
        builder.Services.AddApexChartsMaui();

        builder.Services.AddSingleton<ISecretStore, MauiSecretStore>();
        builder.Services.AddSingleton<IHostInfo, MauiHostInfo>();
        builder.Services.AddSingleton<IAuthLauncher, MauiAuthLauncher>();
        builder.Services.AddSingleton<IFileSaver, MauiFileSaver>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Migrations are quick on device; run them before the first page queries the database.
        Task.Run(() => app.Services.InitialiseDatabaseAsync()).GetAwaiter().GetResult();
        _ = SyncOnLaunchAsync(app.Services);

        return app;
    }

    /// <summary>iOS limits background work, so the app syncs on launch (and on manual refresh).</summary>
    private static async Task SyncOnLaunchAsync(IServiceProvider services)
    {
        try
        {
            var settings = await services.GetRequiredService<AppSettingsService>().GetAsync();
            if (!settings.AutoSync) return;
            if (!(await services.GetRequiredService<StravaAuthService>().GetConnectionAsync()).Connected) return;
            services.GetRequiredService<SyncEngine>().StartInBackground(SyncTrigger.Launch);
        }
        catch (Exception ex)
        {
            services.GetService<ILogger<App>>()?.LogWarning(ex, "Sync on launch failed");
        }
    }
}
