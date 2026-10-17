using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Data;

namespace StravaReports.Core.Settings;

public enum UnitSystem { Metric, Imperial }

/// <summary>Non-secret settings, stored in the AppSetting table.</summary>
public record AppSettingsModel
{
    public string? ClientId { get; init; }
    public string ApiBaseUrl { get; init; } = StravaDefaults.ApiBaseUrl;
    /// <summary>MAUI only: base URL of the web app that relays the OAuth callback into the app's URL scheme.</summary>
    public string? MobileRelayBaseUrl { get; init; }
    public bool ScopeActivityReadAll { get; init; } = true;
    public bool ScopeProfileReadAll { get; init; }
    public bool AutoSync { get; init; } = true;
    public int SyncIntervalMinutes { get; init; } = 60;
    public bool FetchDetail { get; init; } = true;
    public bool FetchStreams { get; init; }
    public UnitSystem Units { get; init; } = UnitSystem.Metric;
    public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;
    public int? MaxHr { get; init; }
    public int? RestingHr { get; init; }
    public int? Ftp { get; init; }
    /// <summary>Upper bounds (bpm) of HR zones 1-4; zone 5 is everything above.</summary>
    public string? HrZones { get; init; }
    public double ShoeRetirementKm { get; init; } = 800;
    public bool IncludeTrainerByDefault { get; init; } = true;
    public bool IncludeCommuteByDefault { get; init; } = true;
    /// <summary>Draw map tiles under routes. Off by default: tiles are fetched from a third-party server.</summary>
    public bool MapTiles { get; init; }
    public string MapTileUrl { get; init; } = StravaDefaults.MapTileUrl;

    public IReadOnlyList<string> Scopes
    {
        get
        {
            var scopes = new List<string> { "read", ScopeActivityReadAll ? "activity:read_all" : "activity:read" };
            if (ScopeProfileReadAll) scopes.Add("profile:read_all");
            return scopes;
        }
    }
}

public static class StravaDefaults
{
    public const string ApiBaseUrl = "https://www.strava.com/api/v3";
    public const string OAuthBaseUrl = "https://www.strava.com/oauth";
    public const string AppUrlScheme = "stravareports";
    public const string MapTileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
}

public class AppSettingsService(IDbContextFactory<StravaDbContext> dbFactory)
{
    private AppSettingsModel? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<AppSettingsModel>? Changed;

    public async Task<AppSettingsModel> GetAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var map = await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
            _cache = FromMap(map);
            return _cache;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(AppSettingsModel model, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.AppSettings.ToDictionaryAsync(x => x.Key, ct);
            foreach (var (key, value) in ToMap(model))
            {
                if (existing.TryGetValue(key, out var row)) row.Value = value;
                else db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            }
            await db.SaveChangesAsync(ct);
            _cache = model;
        }
        finally { _lock.Release(); }
        Changed?.Invoke(model);
    }

    /// <summary>Drop the cache, e.g. after "Delete all data".</summary>
    public void Invalidate() => _cache = null;

    private static Dictionary<string, string?> ToMap(AppSettingsModel m) => new()
    {
        ["strava.clientId"] = m.ClientId,
        ["strava.apiBaseUrl"] = m.ApiBaseUrl,
        ["strava.mobileRelayBaseUrl"] = m.MobileRelayBaseUrl,
        ["strava.scope.activityReadAll"] = B(m.ScopeActivityReadAll),
        ["strava.scope.profileReadAll"] = B(m.ScopeProfileReadAll),
        ["sync.auto"] = B(m.AutoSync),
        ["sync.intervalMinutes"] = I(m.SyncIntervalMinutes),
        ["sync.fetchDetail"] = B(m.FetchDetail),
        ["sync.fetchStreams"] = B(m.FetchStreams),
        ["display.units"] = m.Units.ToString(),
        ["display.weekStart"] = m.WeekStart.ToString(),
        ["athlete.maxHr"] = I(m.MaxHr),
        ["athlete.restingHr"] = I(m.RestingHr),
        ["athlete.ftp"] = I(m.Ftp),
        ["athlete.hrZones"] = m.HrZones,
        ["gear.shoeRetirementKm"] = m.ShoeRetirementKm.ToString(CultureInfo.InvariantCulture),
        ["reports.includeTrainer"] = B(m.IncludeTrainerByDefault),
        ["reports.includeCommute"] = B(m.IncludeCommuteByDefault),
        ["maps.tiles"] = B(m.MapTiles),
        ["maps.tileUrl"] = m.MapTileUrl,
    };

    private static AppSettingsModel FromMap(IReadOnlyDictionary<string, string?> map)
    {
        var d = new AppSettingsModel();
        string? S(string k) => map.TryGetValue(k, out var v) ? v : null;
        bool PB(string k, bool def) => bool.TryParse(S(k), out var v) ? v : def;
        int? PI(string k, int? def) => int.TryParse(S(k), CultureInfo.InvariantCulture, out var v) ? v : def;

        return new AppSettingsModel
        {
            ClientId = S("strava.clientId"),
            ApiBaseUrl = string.IsNullOrWhiteSpace(S("strava.apiBaseUrl")) ? d.ApiBaseUrl : S("strava.apiBaseUrl")!,
            MobileRelayBaseUrl = S("strava.mobileRelayBaseUrl"),
            ScopeActivityReadAll = PB("strava.scope.activityReadAll", d.ScopeActivityReadAll),
            ScopeProfileReadAll = PB("strava.scope.profileReadAll", d.ScopeProfileReadAll),
            AutoSync = PB("sync.auto", d.AutoSync),
            SyncIntervalMinutes = PI("sync.intervalMinutes", d.SyncIntervalMinutes) ?? d.SyncIntervalMinutes,
            FetchDetail = PB("sync.fetchDetail", d.FetchDetail),
            FetchStreams = PB("sync.fetchStreams", d.FetchStreams),
            Units = Enum.TryParse<UnitSystem>(S("display.units"), out var u) ? u : d.Units,
            WeekStart = Enum.TryParse<DayOfWeek>(S("display.weekStart"), out var w) ? w : d.WeekStart,
            MaxHr = PI("athlete.maxHr", null),
            RestingHr = PI("athlete.restingHr", null),
            Ftp = PI("athlete.ftp", null),
            HrZones = S("athlete.hrZones"),
            ShoeRetirementKm = double.TryParse(S("gear.shoeRetirementKm"), CultureInfo.InvariantCulture, out var km) ? km : d.ShoeRetirementKm,
            IncludeTrainerByDefault = PB("reports.includeTrainer", d.IncludeTrainerByDefault),
            IncludeCommuteByDefault = PB("reports.includeCommute", d.IncludeCommuteByDefault),
            MapTiles = PB("maps.tiles", d.MapTiles),
            MapTileUrl = string.IsNullOrWhiteSpace(S("maps.tileUrl")) ? d.MapTileUrl : S("maps.tileUrl")!,
        };
    }

    private static string B(bool v) => v ? "true" : "false";
    private static string? I(int? v) => v?.ToString(CultureInfo.InvariantCulture);
}
