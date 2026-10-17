using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Data;
using StravaReports.Core.Logging;
using StravaReports.Core.Settings;

namespace StravaReports.Core.Strava;

/// <summary>The body Strava POSTs to the callback for every event.</summary>
public class StravaWebhookPayload
{
    public string? ObjectType { get; set; }
    public long ObjectId { get; set; }
    public string? AspectType { get; set; }
    public long OwnerId { get; set; }
    public long SubscriptionId { get; set; }
    public long EventTime { get; set; }
    public Dictionary<string, JsonElement>? Updates { get; set; }
}

public record WebhookStatus(bool Subscribed, long? SubscriptionId, string? CallbackUrl, DateTime? LastEventUtc, int PendingEvents, int EventsLast7Days);

public record RemoteSubscription(long Id, string CallbackUrl);

/// <summary>
/// Strava webhooks (web app only): manages the app's single push subscription and records incoming
/// events. Strava does not sign events, so they are filtered on subscription and athlete ID and then
/// treated purely as hints; the sync engine confirms each one against the API before acting.
/// </summary>
public class StravaWebhookService(
    HttpClient http,
    ISecretStore secrets,
    AppSettingsService settings,
    IDbContextFactory<StravaDbContext> dbFactory,
    AppLog log,
    TimeProvider time)
{
    public const string VerifyTokenKey = "strava.webhookVerifyToken";
    private const string SubscriptionIdKey = "webhook.subscriptionId";
    private const string CallbackUrlKey = "webhook.callbackUrl";
    /// <summary>Events beyond this many unprocessed ones are dropped; the next poll catches up anyway.</summary>
    public const int MaxPendingEvents = 500;

    private long? _subscriptionId;
    private long? _athleteId;

    /// <summary>Raised after an event is stored, so the host can schedule a prompt sync.</summary>
    public event Action? EventReceived;

    /// <summary>Answers Strava's subscription challenge. Returns the challenge to echo, or null to refuse.</summary>
    public async Task<string?> ValidateChallengeAsync(string? mode, string? verifyToken, string? challenge)
    {
        if (mode != "subscribe" || string.IsNullOrEmpty(challenge) || string.IsNullOrEmpty(verifyToken)) return null;
        var expected = await secrets.GetAsync(VerifyTokenKey);
        if (expected is null) return null;
        var ok = CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(verifyToken));
        return ok ? challenge : null;
    }

    /// <summary>Stores an event if it is for our subscription and athlete. Returns false when ignored.</summary>
    public async Task<bool> AcceptAsync(StravaWebhookPayload p, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        if (_subscriptionId is not { } sub || p.SubscriptionId != sub) return false;
        if (_athleteId is not { } athlete || p.OwnerId != athlete) return false;
        if (p.ObjectType is not ("activity" or "athlete") || p.AspectType is not ("create" or "update" or "delete")) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.WebhookEvents.CountAsync(e => e.Status == WebhookEventStatus.Pending, ct) >= MaxPendingEvents) return false;

        var updates = p.Updates is null ? null : JsonSerializer.Serialize(p.Updates);
        db.WebhookEvents.Add(new WebhookEvent
        {
            ObjectType = p.ObjectType,
            ObjectId = p.ObjectId,
            AspectType = p.AspectType,
            OwnerId = p.OwnerId,
            SubscriptionId = p.SubscriptionId,
            EventTime = p.EventTime,
            UpdatesJson = updates is { Length: <= 2048 } ? updates : null,
            ReceivedUtc = time.GetUtcNow().UtcDateTime,
            Status = WebhookEventStatus.Pending,
        });
        await db.SaveChangesAsync(ct);
        EventReceived?.Invoke();
        return true;
    }

    public async Task<WebhookStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var map = await db.AppSettings.AsNoTracking().Where(s => s.Key == SubscriptionIdKey || s.Key == CallbackUrlKey).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        long? id = long.TryParse(map.GetValueOrDefault(SubscriptionIdKey), out var v) ? v : null;
        var weekAgo = time.GetUtcNow().UtcDateTime.AddDays(-7);
        var last = await db.WebhookEvents.MaxAsync(e => (DateTime?)e.ReceivedUtc, ct);
        var pending = await db.WebhookEvents.CountAsync(e => e.Status == WebhookEventStatus.Pending, ct);
        var recent = await db.WebhookEvents.CountAsync(e => e.ReceivedUtc >= weekAgo, ct);
        return new WebhookStatus(id is not null, id, map.GetValueOrDefault(CallbackUrlKey), last, pending, recent);
    }

    /// <summary>
    /// Creates the subscription. Strava calls <paramref name="callbackUrl"/> with a challenge during this
    /// request, so the endpoint must already be reachable over HTTPS from the internet.
    /// </summary>
    public async Task<long> SubscribeAsync(string callbackUrl, CancellationToken ct = default)
    {
        var (clientId, secret, baseUrl) = await CredentialsAsync(ct);
        // Fresh verify token per subscription; saved before the request because Strava checks it mid-request.
        await secrets.SetAsync(VerifyTokenKey, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var verify = (await secrets.GetAsync(VerifyTokenKey))!;

        using var resp = await http.PostAsync($"{baseUrl}/push_subscriptions", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["callback_url"] = callbackUrl,
            ["verify_token"] = verify,
        }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Strava allows one subscription per app; adopt it if it already points here.
            if (resp.StatusCode == HttpStatusCode.BadRequest && body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                var existing = (await ListRemoteAsync(ct)).FirstOrDefault();
                if (existing is not null && string.Equals(existing.CallbackUrl, callbackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    await SaveStateAsync(existing.Id, callbackUrl, ct);
                    return existing.Id;
                }
                throw new InvalidOperationException($"This Strava app already has a webhook subscription for {existing?.CallbackUrl ?? "another URL"}. Remove it first.");
            }
            throw new StravaApiException(resp.StatusCode, $"Strava refused the subscription ({(int)resp.StatusCode}): {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        await SaveStateAsync(id, callbackUrl, ct);
        await log.InfoAsync("Webhook", $"Subscribed to Strava webhooks (subscription {id}).");
        return id;
    }

    public async Task UnsubscribeAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        if (status.SubscriptionId is { } id)
        {
            var (clientId, secret, baseUrl) = await CredentialsAsync(ct);
            using var resp = await http.DeleteAsync($"{baseUrl}/push_subscriptions/{id}?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}", ct);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
                throw new StravaApiException(resp.StatusCode, $"Strava refused to delete the subscription ({(int)resp.StatusCode}).");
        }
        await SaveStateAsync(null, null, ct);
        await secrets.RemoveAsync(VerifyTokenKey);
        await log.InfoAsync("Webhook", "Webhook subscription removed.");
    }

    public async Task<List<RemoteSubscription>> ListRemoteAsync(CancellationToken ct = default)
    {
        var (clientId, secret, baseUrl) = await CredentialsAsync(ct);
        using var resp = await http.GetAsync($"{baseUrl}/push_subscriptions?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}", ct);
        if (!resp.IsSuccessStatusCode) throw new StravaApiException(resp.StatusCode, $"Could not list webhook subscriptions ({(int)resp.StatusCode}).");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray()
            .Select(e => new RemoteSubscription(e.GetProperty("id").GetInt64(), e.TryGetProperty("callback_url", out var u) ? u.GetString() ?? "" : ""))
            .ToList();
    }

    /// <summary>IDs are read fresh for every event, so this only exists for symmetry with other caches.</summary>
    public void Invalidate() { _subscriptionId = null; _athleteId = null; }

    // Read on every event (a few per day): reconnecting as another athlete or restoring takes effect at once.
    private async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var raw = await db.AppSettings.AsNoTracking().Where(s => s.Key == SubscriptionIdKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        _subscriptionId = long.TryParse(raw, out var id) ? id : null;
        _athleteId = await db.Athletes.AsNoTracking().OrderBy(a => a.Id).Select(a => (long?)a.StravaId).FirstOrDefaultAsync(ct);
    }

    private async Task SaveStateAsync(long? id, string? callbackUrl, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var (key, value) in new[] { (SubscriptionIdKey, id?.ToString()), (CallbackUrlKey, callbackUrl) })
        {
            var row = await db.AppSettings.FindAsync([key], ct);
            if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            else row.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<(string ClientId, string Secret, string BaseUrl)> CredentialsAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var secret = await secrets.GetAsync(SecretKeys.ClientSecret);
        if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrEmpty(secret))
            throw new NotConnectedException("Enter your Strava Client ID and Client Secret in Settings first.");
        return (s.ClientId, secret, s.ApiBaseUrl.TrimEnd('/'));
    }

    // Strava's error bodies never echo the secret, but cap their length for the UI.
    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
