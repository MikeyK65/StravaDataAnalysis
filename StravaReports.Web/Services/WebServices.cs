using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;
using StravaReports.Core.Sync;

namespace StravaReports.Web.Services;

public record WebDataPaths(string Root)
{
    public string Database => Path.Combine(Root, "strava.db");
    /// <summary>Separate database for CSV imports of Strava's bulk export.</summary>
    public string ArchiveDatabase => Path.Combine(Root, "strava-archive.db");
    public string Secrets => Path.Combine(Root, "secrets.json");
    public string Keys => Path.Combine(Root, "keys");
    public string SetupCode => Path.Combine(Root, "setup-code.txt");
}

/// <summary>
/// Secrets encrypted with ASP.NET Core Data Protection and stored in a JSON file next to the
/// database. Kept outside the database so "Delete all data" never touches the login or tokens.
/// </summary>
public class DataProtectionSecretStore(IDataProtectionProvider provider, WebDataPaths paths) : ISecretStore
{
    private readonly IDataProtector _protector = provider.CreateProtector("StravaReports.Secrets.v1");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string?> GetAsync(string key)
    {
        var map = await ReadAsync();
        if (!map.TryGetValue(key, out var protectedValue)) return null;
        try { return _protector.Unprotect(protectedValue); }
        catch (CryptographicException) { return null; } // key ring lost: treat as unset
    }

    public async Task SetAsync(string key, string value) => await UpdateAsync(m => m[key] = _protector.Protect(value));

    public async Task RemoveAsync(string key) => await UpdateAsync(m => m.Remove(key));

    private async Task<Dictionary<string, string>> ReadAsync()
    {
        if (!File.Exists(paths.Secrets)) return [];
        await using var fs = File.OpenRead(paths.Secrets);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs) ?? [];
    }

    private async Task UpdateAsync(Action<Dictionary<string, string>> change)
    {
        await _lock.WaitAsync();
        try
        {
            var map = await ReadAsync();
            change(map);
            var tmp = paths.Secrets + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(map));
            File.Move(tmp, paths.Secrets, overwrite: true);
        }
        finally { _lock.Release(); }
    }
}

/// <summary>Starts OAuth with a full-page redirect to the server endpoint, which redirects to Strava.</summary>
public class WebAuthLauncher(NavigationManager nav) : IAuthLauncher
{
    public string RedirectHint
    {
        get
        {
            var host = new Uri(nav.BaseUri).Host;
            return $"Set “Authorization Callback Domain” on Strava to {host}. The callback is {nav.BaseUri}auth/strava/callback.";
        }
    }

    public Task<ConnectResult> ConnectAsync(CancellationToken ct = default)
    {
        nav.NavigateTo("auth/strava/login", forceLoad: true);
        return Task.FromResult(ConnectResult.Redirecting);
    }
}

public class WebHostInfo : IHostInfo
{
    public string PlatformName => "Web";
    public bool HasScheduledSync => true;
    public string? SignOutPath => "account/logout";
    public bool SupportsWebhooks => true;
    public bool CanImportFromWebApp => false;
}

/// <summary>Single-user login: one password, hashed with ASP.NET Core Identity's PasswordHasher.</summary>
public class WebAccountService(ISecretStore secrets, WebDataPaths paths, ILogger<WebAccountService> logger)
{
    private const string PasswordKey = "web.passwordHash";
    private static readonly PasswordHasher<string> Hasher = new();
    private readonly ConcurrentQueue<DateTime> _failures = new();

    public const string UserName = "owner";

    public async Task<bool> HasPasswordAsync() => !string.IsNullOrEmpty(await secrets.GetAsync(PasswordKey));

    /// <summary>
    /// On first run a one-time setup code is written to the data folder (not the log), so only someone
    /// with access to the server's files can claim the site.
    /// </summary>
    public async Task EnsureSetupCodeAsync()
    {
        if (await HasPasswordAsync())
        {
            if (File.Exists(paths.SetupCode)) File.Delete(paths.SetupCode);
            return;
        }
        if (!File.Exists(paths.SetupCode))
            await File.WriteAllTextAsync(paths.SetupCode, Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
        logger.LogWarning("No password set. Open /account/setup and enter the code from {File}.", paths.SetupCode);
    }

    public async Task<bool> VerifySetupCodeAsync(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || !File.Exists(paths.SetupCode)) return false;
        var expected = (await File.ReadAllTextAsync(paths.SetupCode)).Trim();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected.ToUpperInvariant()),
            System.Text.Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()));
    }

    public async Task SetPasswordAsync(string password)
    {
        await secrets.SetAsync(PasswordKey, Hasher.HashPassword(UserName, password));
        if (File.Exists(paths.SetupCode)) File.Delete(paths.SetupCode);
    }

    public bool IsLockedOut()
    {
        while (_failures.TryPeek(out var t) && t < DateTime.UtcNow.AddMinutes(-15)) _failures.TryDequeue(out _);
        return _failures.Count >= 5;
    }

    public async Task<bool> VerifyPasswordAsync(string password)
    {
        if (IsLockedOut()) return false;
        var hash = await secrets.GetAsync(PasswordKey);
        var ok = hash is not null && Hasher.VerifyHashedPassword(UserName, hash, password) != PasswordVerificationResult.Failed;
        if (!ok)
        {
            _failures.Enqueue(DateTime.UtcNow);
            await Task.Delay(750); // slow down guessing
        }
        return ok;
    }
}

/// <summary>Runs sync on the interval from Settings, honouring rate-limit pauses.</summary>
/// <summary>
/// Scheduled sync. Checks every 15 seconds against <see cref="SyncSchedule"/>: rate-limit pauses,
/// failure backoff, debounced webhook events, the backlog, then the interval (a 6-hour safety-net
/// poll while webhooks are active).
/// </summary>
public class SyncBackgroundService(
    SyncEngine engine,
    AppSettingsService settings,
    SyncStatusService status,
    StravaAuthService auth,
    StravaWebhookService webhooks,
    TimeProvider time,
    ILogger<SyncBackgroundService> logger) : BackgroundService
{
    private int _failures;
    private string? _lastReason;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                var decision = await EvaluateAsync(stoppingToken);
                if (!decision.Due) continue;
                if (decision.Reason != _lastReason) logger.LogInformation("Starting sync: {Reason}", decision.Reason);
                _lastReason = decision.Reason;
                var trigger = decision.Reason.StartsWith("Strava sent") ? SyncTrigger.Webhook : SyncTrigger.Scheduled;
                var result = await engine.RunAsync(trigger, stoppingToken);
                _failures = result.Outcome == SyncOutcome.Failed ? _failures + 1 : result.Outcome == SyncOutcome.AlreadyRunning ? _failures : 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _failures++;
                logger.LogError(ex, "Scheduled sync failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<ScheduleDecision> EvaluateAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (!s.AutoSync) return new ScheduleDecision(false, "Automatic sync is off.");
        var connected = (await auth.GetConnectionAsync(ct)).Connected;
        var st = await status.GetAsync(ct);
        var hooks = await webhooks.GetStatusAsync(ct);
        return SyncSchedule.Evaluate(new ScheduleInput(
            time.GetUtcNow().UtcDateTime, s.AutoSync, connected, engine.IsRunning,
            st.LastRunFinishedUtc, st.ResumeAfterUtc, st.BackfillComplete, st.DueJobs, s.SyncIntervalMinutes,
            hooks.Subscribed, hooks.PendingEvents, hooks.LastEventUtc, _failures));
    }
}

/// <summary>
/// Files waiting to be downloaded by the signed-in browser: a temp file behind a random, single-use,
/// 10-minute token. Lets large backups download over plain HTTP instead of through the SignalR circuit.
/// </summary>
public class DownloadStore(TimeProvider time) : IDisposable
{
    private readonly ConcurrentDictionary<string, (string Path, string FileName, string ContentType, DateTime Expires)> _items = new();
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static string Dir => Path.Combine(Path.GetTempPath(), "stravareports-downloads");

    public async Task<string> AddAsync(Stream content, string fileName, string contentType, CancellationToken ct)
    {
        Sweep();
        Directory.CreateDirectory(Dir);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var path = Path.Combine(Dir, token);
        await using (var file = File.Create(path)) await content.CopyToAsync(file, ct);
        _items[token] = (path, fileName, contentType, time.GetUtcNow().UtcDateTime + Lifetime);
        return token;
    }

    /// <summary>Single use: the caller streams the file, which is deleted when the stream closes.</summary>
    public (string Path, string FileName, string ContentType)? Take(string token)
    {
        Sweep();
        return _items.TryRemove(token, out var e) ? (e.Path, e.FileName, e.ContentType) : null;
    }

    /// <summary>Files nobody fetched are removed on shutdown.</summary>
    public void Dispose()
    {
        foreach (var token in _items.Keys)
            if (_items.TryRemove(token, out var e))
                try { File.Delete(e.Path); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Sweep()
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (token, e) in _items)
            if (e.Expires < now && _items.TryRemove(token, out _))
                try { File.Delete(e.Path); } catch (IOException) { }
    }
}

/// <summary>Web <see cref="IFileSaver"/>: stores the file and starts a normal browser download of it.</summary>
public class WebFileSaver(DownloadStore store, IJSRuntime js) : IFileSaver, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<string> SaveAsync(string fileName, string contentType, Stream content, CancellationToken ct = default)
    {
        var token = await store.AddAsync(content, fileName, contentType, ct);
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ct, "./_content/StravaReports.Shared/interop.js");
        await _module.InvokeVoidAsync("downloadUrl", ct, $"download/{token}");
        return $"Downloading {fileName}.";
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { }
        }
    }
}
