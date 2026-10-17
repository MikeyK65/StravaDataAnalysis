using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Data;
using StravaReports.Core.Logging;
using StravaReports.Core.Settings;

namespace StravaReports.Core.Strava;

public static class SecretKeys
{
    public const string ClientSecret = "strava.clientSecret";
    public const string Tokens = "strava.tokens";
}

public record StoredTokens(string AccessToken, string RefreshToken, long ExpiresAt, string? Scope)
{
    public DateTime ExpiresAtUtc => DateTimeOffset.FromUnixTimeSeconds(ExpiresAt).UtcDateTime;
}

public record ConnectionInfo(bool HasClientId, bool HasClientSecret, bool Connected, string? AthleteName, DateTime? TokenExpiresUtc, string? Scope);

public class NotConnectedException(string message) : Exception(message);

/// <summary>OAuth: authorize URL, code exchange, token refresh and deauthorization.</summary>
public class StravaAuthService(
    HttpClient http,
    ISecretStore secrets,
    AppSettingsService settings,
    IDbContextFactory<StravaDbContext> dbFactory,
    AppLog log,
    TimeProvider? timeProvider = null)
{
    /// <summary>Refresh when the access token expires within this window.</summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _pendingStates = new();

    public event Action? ConnectionChanged;

    public string CreateState()
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (k, exp) in _pendingStates) if (exp < now) _pendingStates.TryRemove(k, out _);
        _pendingStates[state] = now.AddMinutes(15);
        return state;
    }

    public bool ValidateState(string? state) =>
        state is not null && _pendingStates.TryRemove(state, out var exp) && exp >= _time.GetUtcNow().UtcDateTime;

    public async Task<string> BuildAuthorizeUrlAsync(string redirectUri, string state, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(s.ClientId)) throw new NotConnectedException("Enter your Strava Client ID in Settings first.");
        return $"{StravaDefaults.OAuthBaseUrl}/authorize?client_id={Uri.EscapeDataString(s.ClientId)}" +
               $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&approval_prompt=auto&scope={Uri.EscapeDataString(string.Join(',', s.Scopes))}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task ExchangeCodeAsync(string code, string? grantedScope, CancellationToken ct = default)
    {
        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
        }, ct);

        await SaveTokensAsync(new StoredTokens(token.AccessToken, token.RefreshToken, token.ExpiresAt, grantedScope));
        if (token.Athlete is not null) await UpsertAthleteAsync(token.Athlete, null, ct);
        await log.InfoAsync("Auth", $"Connected to Strava as athlete {token.Athlete?.Id}. Granted scope: {grantedScope}");
        ConnectionChanged?.Invoke();
    }

    /// <summary>Returns a valid access token, refreshing it first if it expires within 10 minutes.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var tokens = await LoadTokensAsync() ?? throw new NotConnectedException("Not connected to Strava.");
        if (tokens.ExpiresAtUtc - _time.GetUtcNow().UtcDateTime > RefreshWindow) return tokens.AccessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            tokens = await LoadTokensAsync() ?? throw new NotConnectedException("Not connected to Strava.");
            if (tokens.ExpiresAtUtc - _time.GetUtcNow().UtcDateTime > RefreshWindow) return tokens.AccessToken;

            var token = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
            }, ct);

            // Persist the new refresh token immediately: the old one may stop working.
            await SaveTokensAsync(tokens with { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken, ExpiresAt = token.ExpiresAt });
            await log.InfoAsync("Auth", $"Access token refreshed; expires {DateTimeOffset.FromUnixTimeSeconds(token.ExpiresAt):u}.");
            return token.AccessToken;
        }
        finally { _refreshLock.Release(); }
    }

    /// <summary>Revokes the authorisation at Strava (best effort) and wipes stored tokens.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var tokens = await LoadTokensAsync();
        if (tokens is not null)
        {
            try
            {
                using var resp = await http.PostAsync($"{StravaDefaults.OAuthBaseUrl}/deauthorize",
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = tokens.AccessToken }), ct);
                await log.InfoAsync("Auth", $"Deauthorize returned {(int)resp.StatusCode}.");
            }
            catch (Exception ex)
            {
                await log.WarnAsync("Auth", $"Deauthorize failed: {ex.Message}");
            }
        }
        await secrets.RemoveAsync(SecretKeys.Tokens);
        await log.InfoAsync("Auth", "Disconnected; tokens wiped.");
        ConnectionChanged?.Invoke();
    }

    public async Task<ConnectionInfo> GetConnectionAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var hasSecret = !string.IsNullOrEmpty(await secrets.GetAsync(SecretKeys.ClientSecret));
        var tokens = await LoadTokensAsync();
        string? name = null;
        if (tokens is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var athlete = await db.Athletes.AsNoTracking().OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            name = athlete?.DisplayName;
        }
        return new ConnectionInfo(!string.IsNullOrWhiteSpace(s.ClientId), hasSecret, tokens is not null, name, tokens?.ExpiresAtUtc, tokens?.Scope);
    }

    public async Task SetClientSecretAsync(string secret)
    {
        await secrets.SetAsync(SecretKeys.ClientSecret, secret.Trim());
        ConnectionChanged?.Invoke();
    }

    public async Task UpsertAthleteAsync(StravaAthlete a, string? rawJson, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Athletes.FirstOrDefaultAsync(x => x.StravaId == a.Id, ct);
        if (row is null)
        {
            // Single-athlete app: replace any previous athlete.
            db.Athletes.RemoveRange(db.Athletes);
            row = new Athlete { StravaId = a.Id };
            db.Athletes.Add(row);
        }
        row.FirstName = a.Firstname ?? row.FirstName;
        row.LastName = a.Lastname ?? row.LastName;
        row.ProfileImageUrl = a.Profile ?? row.ProfileImageUrl;
        row.MeasurementPreference = a.MeasurementPreference ?? row.MeasurementPreference;
        if (rawJson is not null) row.RawJson = rawJson;
        row.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private async Task<StravaTokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var secret = await secrets.GetAsync(SecretKeys.ClientSecret);
        if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(secret))
            throw new NotConnectedException("Client ID and Client Secret must be set in Settings.");

        form["client_id"] = s.ClientId;
        form["client_secret"] = secret;

        using var resp = await http.PostAsync($"{StravaDefaults.OAuthBaseUrl}/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Do not log the request (it contains the secret). The error body is safe.
            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized && form["grant_type"] == "refresh_token")
            {
                await secrets.RemoveAsync(SecretKeys.Tokens);
                ConnectionChanged?.Invoke();
            }
            throw new StravaApiException(resp.StatusCode, $"Token request failed ({(int)resp.StatusCode}): {(body.Length > 300 ? body[..300] : body)}");
        }
        return JsonSerializer.Deserialize<StravaTokenResponse>(body, StravaJson.Options)
               ?? throw new StravaApiException(resp.StatusCode, "Empty token response.");
    }

    private async Task<StoredTokens?> LoadTokensAsync()
    {
        var json = await secrets.GetAsync(SecretKeys.Tokens);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<StoredTokens>(json); }
        catch (JsonException) { return null; }
    }

    private Task SaveTokensAsync(StoredTokens tokens) => secrets.SetAsync(SecretKeys.Tokens, JsonSerializer.Serialize(tokens));
}
