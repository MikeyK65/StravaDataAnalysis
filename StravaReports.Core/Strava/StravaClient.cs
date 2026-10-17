using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StravaReports.Core.Settings;

namespace StravaReports.Core.Strava;

public class StravaApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = status;
}

/// <summary>Read-only Strava API client. Every call goes through the rate-limit tracker.</summary>
public interface IStravaClient
{
    Task<(StravaAthlete Athlete, string RawJson)> GetAthleteAsync(string accessToken, CancellationToken ct = default);
    Task<IReadOnlyList<StravaActivityWithJson>> ListActivitiesAsync(string accessToken, long? after, long? before, int page, int perPage, CancellationToken ct = default);
    Task<StravaActivityWithJson?> GetActivityAsync(string accessToken, long id, CancellationToken ct = default);
    Task<StravaStreamSet?> GetActivityStreamsAsync(string accessToken, long id, IEnumerable<string> keys, CancellationToken ct = default);
    Task<StravaGearSummary?> GetGearAsync(string accessToken, string gearId, CancellationToken ct = default);
}

public class StravaClient(HttpClient http, RateLimitTracker rateLimits, AppSettingsService settings) : IStravaClient
{
    public const int MaxPageSize = 200;

    public async Task<(StravaAthlete Athlete, string RawJson)> GetAthleteAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await GetStringAsync(accessToken, "athlete", ct) ?? throw new StravaApiException(HttpStatusCode.NotFound, "Athlete not found.");
        return (JsonSerializer.Deserialize<StravaAthlete>(json, StravaJson.Options)!, json);
    }

    public async Task<IReadOnlyList<StravaActivityWithJson>> ListActivitiesAsync(string accessToken, long? after, long? before, int page, int perPage, CancellationToken ct = default)
    {
        var query = $"athlete/activities?page={page}&per_page={Math.Clamp(perPage, 1, MaxPageSize)}";
        if (after is not null) query += $"&after={after}";
        if (before is not null) query += $"&before={before}";

        var json = await GetStringAsync(accessToken, query, ct);
        if (json is null) return [];

        using var doc = JsonDocument.Parse(json);
        var list = new List<StravaActivityWithJson>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var raw = el.GetRawText();
            list.Add(new StravaActivityWithJson(el.Deserialize<StravaActivity>(StravaJson.Options)!, raw));
        }
        return list;
    }

    public async Task<StravaActivityWithJson?> GetActivityAsync(string accessToken, long id, CancellationToken ct = default)
    {
        var json = await GetStringAsync(accessToken, $"activities/{id}?include_all_efforts=false", ct);
        return json is null ? null : new StravaActivityWithJson(JsonSerializer.Deserialize<StravaActivity>(json, StravaJson.Options)!, json);
    }

    public async Task<StravaStreamSet?> GetActivityStreamsAsync(string accessToken, long id, IEnumerable<string> keys, CancellationToken ct = default)
    {
        var json = await GetStringAsync(accessToken, $"activities/{id}/streams?keys={string.Join(',', keys)}&key_by_type=true", ct);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, (int, string)>();
        // key_by_type=true returns an object keyed by stream type; be tolerant of the array form too.
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
                AddStream(prop.Name, prop.Value);
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.TryGetProperty("type", out var t)) AddStream(t.GetString()!, el);
        }
        return new StravaStreamSet(result);

        void AddStream(string type, JsonElement el)
        {
            if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
            result[type] = (data.GetArrayLength(), data.GetRawText());
        }
    }

    public async Task<StravaGearSummary?> GetGearAsync(string accessToken, string gearId, CancellationToken ct = default)
    {
        var json = await GetStringAsync(accessToken, $"gear/{Uri.EscapeDataString(gearId)}", ct);
        return json is null ? null : JsonSerializer.Deserialize<StravaGearSummary>(json, StravaJson.Options);
    }

    /// <summary>GET a relative API path. Returns null on 404.</summary>
    private async Task<string?> GetStringAsync(string accessToken, string relative, CancellationToken ct)
    {
        rateLimits.EnsureBudget();

        var baseUrl = (await settings.GetAsync(ct)).ApiBaseUrl.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{relative}");
        req.Headers.Authorization = new("Bearer", accessToken);

        using var resp = await http.SendAsync(req, ct);
        rateLimits.Update(resp.Headers);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests) throw rateLimits.OnTooManyRequests();
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            // Strava error bodies contain no secrets, but keep them short in logs.
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new StravaApiException(resp.StatusCode, $"Strava API {(int)resp.StatusCode} on {StripQuery(relative)}: {Truncate(body, 300)}");
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string StripQuery(string path) => path.Split('?')[0];
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
