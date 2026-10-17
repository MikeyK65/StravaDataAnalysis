using ApexCharts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using StravaReports.Core.Reports;
using StravaReports.Core.Settings;
using StravaReports.Shared.Reports;

namespace StravaReports.Shared.Services;

/// <summary>Links a report (query + metadata) to the Razor component that renders it.</summary>
public record ReportViewRegistration(Type ReportType, Type ViewType);

public record ReportEntry(IReport Report, Type ViewType);

/// <summary>The catalogue the navigation reads. Reports appear in nav by registering a view.</summary>
public class ReportCatalogue(IEnumerable<IReport> reports, IEnumerable<ReportViewRegistration> views)
{
    private readonly List<ReportEntry> _entries = views
        .Select(v => new ReportEntry(reports.Single(r => r.GetType() == v.ReportType), v.ViewType))
        .OrderBy(e => e.Report.Order)
        .ToList();

    public IReadOnlyList<ReportEntry> Entries => _entries;

    public ReportEntry? Find(string id) => _entries.FirstOrDefault(e => string.Equals(e.Report.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The shared report filter, kept for the lifetime of the UI session so it follows you between reports.</summary>
public class FilterState
{
    private ReportFilter? _filter;

    public string Preset { get; private set; } = "12m";

    /// <summary>The Archive box is switched on once per session when an archive exists; after that it is the user's choice.</summary>
    public bool ArchiveDefaultApplied { get; set; }

    public event Action? Changed;

    public ReportFilter Get(AppSettingsModel s)
    {
        _filter ??= new ReportFilter
        {
            From = PresetRange("12m", s.WeekStart).From,
            To = null,
            IncludeTrainer = s.IncludeTrainerByDefault,
            IncludeCommute = s.IncludeCommuteByDefault,
        };
        return _filter;
    }

    public void Set(ReportFilter filter, string? preset = null)
    {
        _filter = filter;
        if (preset is not null) Preset = preset;
        Changed?.Invoke();
    }

    public static readonly (string Key, string Label)[] Presets =
    [
        ("week", "This week"), ("4w", "Last 4 weeks"), ("3m", "Last 3 months"), ("ytd", "This year"),
        ("lastyear", "Last year"), ("12m", "Last 12 months"), ("all", "All time"), ("custom", "Custom"),
    ];

    public static (DateOnly? From, DateOnly? To) PresetRange(string key, DayOfWeek weekStart)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return key switch
        {
            "week" => (Calc.StartOfWeek(today, weekStart), null),
            "4w" => (Calc.StartOfWeek(today, weekStart).AddDays(-21), null),
            "3m" => (today.AddMonths(-3).AddDays(1), null),
            "ytd" => (new DateOnly(today.Year, 1, 1), null),
            "lastyear" => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            "12m" => (today.AddYears(-1).AddDays(1), null),
            _ => (null, null),
        };
    }
}

/// <summary>Light/dark detection and chart defaults so every chart reads as one system.</summary>
public class UiTheme(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private bool? _dark;

    public const string Accent = "#FC4C02";
    public static readonly List<string> Palette = ["#FC4C02", "#2D7FF9", "#14A38B", "#A35BEB", "#E8A33D", "#7A8699"];

    public async Task<bool> IsDarkAsync()
    {
        if (_dark is not null) return _dark.Value;
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/StravaReports.Shared/interop.js");
            _dark = await _module.InvokeAsync<bool>("prefersDark");
        }
        catch (Exception) { _dark = false; } // prerender or JS unavailable
        return _dark.Value;
    }

    public static ApexChartOptions<T> ChartOptions<T>(bool dark, string? yTitle = null, bool stacked = false) where T : class => new()
    {
        Chart = new Chart
        {
            Background = "transparent",
            Toolbar = new Toolbar { Show = false },
            FontFamily = "inherit",
            Stacked = stacked,
            Zoom = new Zoom { Enabled = false },
        },
        Theme = new Theme { Mode = dark ? Mode.Dark : Mode.Light },
        Colors = [.. Palette],
        DataLabels = new DataLabels { Enabled = false },
        Grid = new Grid { BorderColor = dark ? "#2a2f36" : "#e8eaee", StrokeDashArray = 3 },
        Stroke = new Stroke { Width = 2, Curve = Curve.Smooth },
        Legend = new Legend { Position = LegendPosition.Top, HorizontalAlign = Align.Left },
        Yaxis = yTitle is null ? null : [new YAxis { Title = new AxisTitle { Text = yTitle }, DecimalsInFloat = 1 }],
    };

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { }
        }
    }
}

public static class UiServiceCollectionExtensions
{
    public static IServiceCollection AddStravaReportsUi(this IServiceCollection services)
    {
        services.AddScoped<FilterState>();
        services.AddScoped<UiTheme>();
        services.AddSingleton<ReportCatalogue>();

        services.AddReportView<VolumeTrendsReport, VolumeTrendsView>();
        services.AddReportView<ConsistencyReport, ConsistencyView>();
        services.AddReportView<RunningReport, RunningView>();
        services.AddReportView<CyclingReport, CyclingView>();
        services.AddReportView<GearReport, GearView>();
        services.AddReportView<RecordsReport, RecordsView>();
        services.AddReportView<HrZonesReport, HrZonesView>();
        services.AddReportView<TrainingLoadReport, TrainingLoadView>();
        services.AddReportView<PowerReport, PowerView>();
        services.AddReportView<RunningEfficiencyReport, RunningEfficiencyView>();
        services.AddReportView<RoutesReport, RoutesView>();
        services.AddReportView<AllDataReport, AllDataView>();
        return services;
    }

    public static IServiceCollection AddReportView<TReport, TView>(this IServiceCollection services)
        where TReport : IReport where TView : Microsoft.AspNetCore.Components.IComponent
    {
        services.AddSingleton(new ReportViewRegistration(typeof(TReport), typeof(TView)));
        return services;
    }
}
