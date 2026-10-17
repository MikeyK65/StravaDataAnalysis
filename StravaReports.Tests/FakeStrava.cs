using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using StravaReports.Core;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Data;
using StravaReports.Core.Strava;

namespace StravaReports.Tests;

public record FakeActivity(long Id, DateTime StartUtc, string Sport = "Run", double Distance = 10_000, int MovingTime = 3000, string? GearId = null, double? Watts = null, string? Polyline = null);

/// <summary>A tiny in-memory imitation of the Strava API endpoints the sync engine calls.</summary>
public class FakeStravaHandler : HttpMessageHandler
{
    public List<FakeActivity> Activities { get; } = [];
    public HashSet<long> DeletedDetail { get; } = [];
    public ConcurrentQueue<string> Requests { get; } = new();
    public int ShortLimit { get; set; } = 100;
    public int DailyLimit { get; set; } = 1000;
    public int ShortUsage { get; set; }
    public int DailyUsage { get; set; }
    /// <summary>Elapsed seconds of each activity's 5k best effort.</summary>
    public Func<FakeActivity, int> FiveKTime { get; set; } = _ => 1450;
    /// <summary>Optional per-activity streams payload (key_by_type form); null uses a tiny HR stream.</summary>
    public Func<long, object>? StreamsFor { get; set; }
    /// <summary>When true the athlete endpoint answers 401, as after the athlete revokes access.</summary>
    public bool Revoked { get; set; }
    public List<(long Id, string CallbackUrl)> Subscriptions { get; } = [];
    public Dictionary<string, string> LastSubscriptionForm { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue(path + request.RequestUri.Query);
        ShortUsage++;
        DailyUsage++;
        var q = HttpUtility.ParseQueryString(request.RequestUri.Query);

        HttpResponseMessage resp;
        if (path.EndsWith("/push_subscriptions") && request.Method == HttpMethod.Post)
        {
            var form = HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync(ct).Result);
            LastSubscriptionForm.Clear();
            foreach (var k in form.AllKeys) LastSubscriptionForm[k!] = form[k]!;
            if (Subscriptions.Count > 0)
                resp = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"Bad Request\",\"errors\":[{\"code\":\"already exists\"}]}") };
            else
            {
                Subscriptions.Add((777, form["callback_url"]!));
                resp = Json(new { id = 777 });
            }
        }
        else if (path.EndsWith("/push_subscriptions"))
        {
            resp = Json(Subscriptions.Select(x => new { id = x.Id, callback_url = x.CallbackUrl }).ToList());
        }
        else if (path.Contains("/push_subscriptions/") && request.Method == HttpMethod.Delete)
        {
            Subscriptions.RemoveAll(x => path.EndsWith("/" + x.Id));
            resp = new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        else if (path.EndsWith("/athlete") && Revoked)
        {
            resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        else if (path.EndsWith("/athlete"))
        {
            resp = Json(new { id = 42, firstname = "Test", lastname = "Athlete", measurement_preference = "meters",
                bikes = new[] { new { id = "b1", name = "Road bike", primary = true, distance = 12345.0 } },
                shoes = new[] { new { id = "g1", name = "Trainers", primary = true, distance = 500_000.0 } } });
        }
        else if (path.EndsWith("/athlete/activities"))
        {
            long? after = long.TryParse(q["after"], out var a) ? a : null;
            long? before = long.TryParse(q["before"], out var b) ? b : null;
            var page = int.Parse(q["page"] ?? "1");
            var per = int.Parse(q["per_page"] ?? "30");
            IEnumerable<FakeActivity> list = Activities;
            if (after is not null) list = list.Where(x => Epoch(x.StartUtc) > after).OrderBy(x => x.StartUtc);
            else list = list.OrderByDescending(x => x.StartUtc);
            if (before is not null) list = list.Where(x => Epoch(x.StartUtc) < before);
            resp = Json(list.Skip((page - 1) * per).Take(per).Select(Summary).ToList());
        }
        else if (path.Contains("/activities/") && path.EndsWith("/streams"))
        {
            var sid = long.Parse(path.Split('/')[^2]);
            resp = Json(StreamsFor?.Invoke(sid) ?? new { time = new { data = new[] { 0, 1, 2 } }, heartrate = new { data = new[] { 120, 121, 122 } } });
        }
        else if (path.Contains("/activities/"))
        {
            var id = long.Parse(path.Split('/').Last());
            var act = Activities.FirstOrDefault(x => x.Id == id);
            if (act is null || DeletedDetail.Contains(id)) resp = new HttpResponseMessage(HttpStatusCode.NotFound);
            else
            {
                var detail = Summary(act);
                detail["laps"] = new[]
                {
                    new { lap_index = 1, distance = act.Distance / 2, moving_time = act.MovingTime / 2, elapsed_time = act.MovingTime / 2, average_speed = 3.3 },
                    new { lap_index = 2, distance = act.Distance / 2, moving_time = act.MovingTime / 2, elapsed_time = act.MovingTime / 2, average_speed = 3.4 },
                };
                detail["best_efforts"] = new[] { new { name = "5k", distance = 5000.0, moving_time = FiveKTime(act), elapsed_time = FiveKTime(act), start_date_local = act.StartUtc, pr_rank = (int?)1 } };
                resp = Json(detail);
            }
        }
        else if (path.Contains("/gear/"))
        {
            resp = Json(new { id = path.Split('/').Last(), name = "Old shoes", retired = true, distance = 900_000.0 });
        }
        else resp = new HttpResponseMessage(HttpStatusCode.NotFound);

        resp.Headers.Add("X-ReadRateLimit-Limit", $"{ShortLimit},{DailyLimit}");
        resp.Headers.Add("X-ReadRateLimit-Usage", $"{ShortUsage},{DailyUsage}");
        return Task.FromResult(resp);
    }

    public static long Epoch(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private static Dictionary<string, object?> Summary(FakeActivity a) => new()
    {
        ["id"] = a.Id,
        ["name"] = $"Activity {a.Id}",
        ["sport_type"] = a.Sport,
        ["type"] = a.Sport,
        ["start_date"] = a.StartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["start_date_local"] = a.StartUtc.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["distance"] = a.Distance,
        ["moving_time"] = a.MovingTime,
        ["elapsed_time"] = a.MovingTime + 60,
        ["total_elevation_gain"] = 50.0,
        ["average_speed"] = a.MovingTime > 0 ? a.Distance / a.MovingTime : 0,
        ["max_speed"] = 5.0,
        ["has_heartrate"] = true,
        ["average_heartrate"] = 150.0,
        ["gear_id"] = a.GearId,
        ["average_watts"] = a.Watts,
        ["weighted_average_watts"] = a.Watts,
        ["device_watts"] = a.Watts is not null,
        ["trainer"] = false,
        ["commute"] = false,
        ["manual"] = false,
        ["map"] = new { summary_polyline = a.Polyline },
    };

    private static HttpResponseMessage Json(object o) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
}

public class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _map = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(_map.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value) { _map[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key) { _map.TryRemove(key, out _); return Task.CompletedTask; }
}

/// <summary>Real core services over a temp SQLite file, a fake clock and the fake API.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public FakeStravaHandler Strava { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    public ServiceProvider Services { get; }
    /// <summary>Each host gets its own folder, so restore safety copies (written next to the database) are cleaned up too.</summary>
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"stravareports-test-{Guid.NewGuid():N}");
    private string _dbPath => Path.Combine(Dir, "strava.db");
    private string _archivePath => Path.Combine(Dir, "strava-archive.db");

    private TestHost()
    {
        Directory.CreateDirectory(Dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStravaReportsCore(_dbPath);
        services.AddStravaArchive(_archivePath);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
        services.AddSingleton<ISecretStore, MemorySecretStore>();
        services.AddHttpClient<IStravaClient, StravaClient>().ConfigurePrimaryHttpMessageHandler(() => Strava);
        services.AddHttpClient(nameof(StravaWebhookService)).ConfigurePrimaryHttpMessageHandler(() => Strava);
        services.AddHttpClient(nameof(StravaAuthService)).ConfigurePrimaryHttpMessageHandler(() => Strava);
        Services = services.BuildServiceProvider();
    }

    public static async Task<TestHost> CreateAsync(bool connected = true)
    {
        var host = new TestHost();
        await host.Services.InitialiseDatabaseAsync();
        if (connected)
        {
            var tokens = new StoredTokens("access", "refresh", host.Time.GetUtcNow().AddHours(6).ToUnixTimeSeconds(), "read,activity:read_all");
            await host.Services.GetRequiredService<ISecretStore>().SetAsync(SecretKeys.Tokens, JsonSerializer.Serialize(tokens));
        }
        return host;
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<StravaDbContext> DbAsync() => await Get<IDbContextFactory<StravaDbContext>>().CreateDbContextAsync();

    public void AddActivities(int count, DateTime newest, TimeSpan spacing, long firstId = 1000)
    {
        for (var i = 0; i < count; i++)
            Strava.Activities.Add(new FakeActivity(firstId + i, newest - spacing * i));
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
