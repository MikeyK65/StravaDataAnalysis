using Microsoft.AspNetCore.Components;
using StravaReports.Core.Reports;
using StravaReports.Core.Settings;
using StravaReports.Core.Sync;
using StravaReports.Shared.Services;

namespace StravaReports.Shared.Components;

/// <summary>
/// Base for report views: loads settings and the shared filter, and re-runs the report when the
/// filter changes or a sync completes.
/// </summary>
public abstract class ReportViewBase : ComponentBase, IDisposable
{
    [Inject] protected AppSettingsService SettingsService { get; set; } = default!;
    [Inject] protected FilterState FilterState { get; set; } = default!;
    [Inject] protected UiTheme Theme { get; set; } = default!;
    [Inject] protected SyncEngine Sync { get; set; } = default!;

    protected AppSettingsModel Settings { get; private set; } = new();
    protected ReportFilter Filter { get; private set; } = new();
    protected UnitSystem U => Settings.Units;
    protected bool Dark { get; private set; }
    protected bool Loading { get; private set; } = true;
    protected string? Error { get; private set; }
    /// <summary>Changes on every load; use as a @key on charts so they rebuild with new data.</summary>
    protected int Version { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        FilterState.Changed += OnFilterChanged;
        Sync.RunCompleted += OnSyncCompleted;
        Settings = await SettingsService.GetAsync();
        await ReloadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        var dark = await Theme.IsDarkAsync();
        if (dark != Dark) { Dark = dark; BuildCharts(); Version++; StateHasChanged(); }
    }

    protected abstract Task LoadAsync(CancellationToken ct);

    /// <summary>Create chart options; called after each load and when the colour scheme is detected.</summary>
    protected virtual void BuildCharts() { }

    protected async Task ReloadAsync()
    {
        Loading = true;
        Error = null;
        try
        {
            Settings = await SettingsService.GetAsync();
            Filter = FilterState.Get(Settings);
            await LoadAsync(CancellationToken.None);
            BuildCharts();
            Version++;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Loading = false; }
    }

    private void OnFilterChanged() => InvokeAsync(async () => { await ReloadAsync(); StateHasChanged(); });

    private void OnSyncCompleted(SyncRunResult r)
    {
        if (r.ActivitiesUpserted > 0 || r.JobsProcessed > 0) OnFilterChanged();
    }

    // Display helpers in the user's units.
    protected string Dist(double m, int decimals = 1) => Units.FormatDistance(m, U, decimals);
    protected string Elev(double m) => Units.FormatElevation(m, U);
    protected string Speed(double mps) => Units.FormatSpeed(mps, U);
    protected string Pace(double distanceM, int seconds) => Units.FormatPace(Calc.PaceSecondsPerUnit(distanceM, seconds, U), U);
    protected decimal? DistVal(double m) => (decimal)Math.Round(Units.DistanceValue(m, U), 2);
    protected decimal? ElevVal(double m) => (decimal)Math.Round(Units.ElevationValue(m, U), 0);
    protected string DU => Units.DistanceUnit(U);

    public virtual void Dispose()
    {
        FilterState.Changed -= OnFilterChanged;
        Sync.RunCompleted -= OnSyncCompleted;
    }
}
