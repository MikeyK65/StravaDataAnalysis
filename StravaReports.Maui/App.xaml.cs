using Microsoft.Extensions.Logging;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;
using StravaReports.Core.Sync;

namespace StravaReports;

public partial class App : Application
{
    /// <summary>Returning to the app syncs again if the last run is older than this (or work is waiting).</summary>
    public static readonly TimeSpan ResumeSyncGap = TimeSpan.FromMinutes(15);

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "Strava Reports" };
        // iOS gives apps little background time, so sync when the user comes back instead.
        window.Resumed += (_, _) => _ = SyncOnResumeAsync();
        return window;
    }

    private static async Task SyncOnResumeAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;
        try
        {
            var engine = services.GetRequiredService<SyncEngine>();
            if (engine.IsRunning || !(await services.GetRequiredService<AppSettingsService>().GetAsync()).AutoSync) return;
            if (!(await services.GetRequiredService<StravaAuthService>().GetConnectionAsync()).Connected) return;

            var status = await services.GetRequiredService<SyncStatusService>().GetAsync();
            var now = DateTime.UtcNow;
            if (status.ResumeAfterUtc is { } resume && resume > now) return;
            var backlog = !status.BackfillComplete || status.DueJobs > 0;
            if (!backlog && status.LastRunFinishedUtc is { } last && now - last < ResumeSyncGap) return;
            engine.StartInBackground(SyncTrigger.Resume);
        }
        catch (Exception ex)
        {
            services.GetService<ILogger<App>>()?.LogWarning(ex, "Sync on resume failed");
        }
    }
}
