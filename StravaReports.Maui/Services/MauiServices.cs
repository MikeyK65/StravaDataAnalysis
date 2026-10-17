using System.Net;
using System.Net.Sockets;
using System.Text;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;

namespace StravaReports.Services;

/// <summary>Secrets in the platform's secure store (Keychain on iOS/macOS, Keystore on Android, DPAPI on Windows).</summary>
public class MauiSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}

public class MauiHostInfo : IHostInfo
{
    public string PlatformName => DeviceInfo.Platform.ToString();
    public bool HasScheduledSync => false;
    public string? SignOutPath => null;
    public bool SupportsWebhooks => false;
    public bool CanImportFromWebApp => true;
}

/// <summary>
/// OAuth on device. iOS/Android use WebAuthenticator: Strava redirects to the web app's relay
/// endpoint, which forwards the code to stravareports://oauth. Desktop (Windows, Mac Catalyst)
/// uses a localhost loopback listener instead, since Strava always allows localhost callbacks.
/// </summary>
public class MauiAuthLauncher(StravaAuthService auth, AppSettingsService settings) : IAuthLauncher
{
    private static bool IsDesktop => DeviceInfo.Platform == DevicePlatform.WinUI || DeviceInfo.Platform == DevicePlatform.MacCatalyst;

    public string RedirectHint => IsDesktop
        ? "On desktop the app listens on http://localhost for the callback. Strava always allows localhost, so the callback domain can stay set to your web app's domain."
        : "Set “Authorization Callback Domain” on Strava to your web app's domain, and enter that web app's URL below as the OAuth relay.";

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        try
        {
            if (IsDesktop && string.IsNullOrWhiteSpace(s.MobileRelayBaseUrl))
                return await LoopbackAsync(ct);
            return await WebAuthenticatorAsync(s, ct);
        }
        catch (TaskCanceledException)
        {
            return new ConnectResult(false, "Sign-in was cancelled.");
        }
    }

    private async Task<ConnectResult> WebAuthenticatorAsync(AppSettingsModel s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.MobileRelayBaseUrl))
            return new ConnectResult(false, "Enter your web app's URL as the OAuth relay first: Strava's single callback domain must point at it.");

        var state = auth.CreateState();
        var redirect = $"{s.MobileRelayBaseUrl!.TrimEnd('/')}/auth/strava/app-callback";
        var url = await auth.BuildAuthorizeUrlAsync(redirect, state, ct);

        var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
        {
            Url = new Uri(url),
            CallbackUrl = new Uri($"{StravaDefaults.AppUrlScheme}://oauth"),
            PrefersEphemeralWebBrowserSession = false,
        });

        return await CompleteAsync(result.Properties, ct);
    }

    private async Task<ConnectResult> LoopbackAsync(CancellationToken ct)
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var state = auth.CreateState();
        var url = await auth.BuildAuthorizeUrlAsync($"{prefix}callback", state, ct);
        await Launcher.Default.OpenAsync(new Uri(url));

        var contextTask = listener.GetContextAsync();
        var finished = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5), ct));
        if (finished != contextTask) return new ConnectResult(false, "Timed out waiting for Strava.");

        var context = await contextTask;
        var query = context.Request.QueryString;
        var props = query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k] ?? "");

        var ok = props.ContainsKey("code") && !props.ContainsKey("error");
        var html = ok
            ? "<h2>Connected to Strava</h2><p>You can close this tab and return to Strava Reports.</p>"
            : "<h2>Strava sign-in failed</h2><p>Return to Strava Reports and try again.</p>";
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><body style=\"font-family:sans-serif;padding:2rem\">{html}</body>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct);
        context.Response.Close();

        return await CompleteAsync(props, ct);
    }

    private async Task<ConnectResult> CompleteAsync(IDictionary<string, string> props, CancellationToken ct)
    {
        if (props.TryGetValue("error", out var error)) return new ConnectResult(false, $"Strava returned: {error}");
        if (!props.TryGetValue("state", out var state) || !auth.ValidateState(state)) return new ConnectResult(false, "Invalid or expired state; please try again.");
        if (!props.TryGetValue("code", out var code) || string.IsNullOrEmpty(code)) return new ConnectResult(false, "No authorization code returned.");

        props.TryGetValue("scope", out var scope);
        await auth.ExchangeCodeAsync(code, scope, ct);
        return new ConnectResult(true);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>Writes the file to the app's cache and opens the share sheet (Files, AirDrop, Mail...).</summary>
public class MauiFileSaver : IFileSaver
{
    public async Task<string> SaveAsync(string fileName, string contentType, Stream content, CancellationToken ct = default)
    {
        var dir = Path.Combine(FileSystem.CacheDirectory, "exports");
        Directory.CreateDirectory(dir);
        // Old exports are personal data; keep only the newest few.
        foreach (var old in new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.CreationTimeUtc).Skip(5))
            try { old.Delete(); } catch (IOException) { }
        var path = Path.Combine(dir, fileName);
        await using (var file = File.Create(path)) await content.CopyToAsync(file, ct);
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File = new ShareFile(path, contentType),
        }));
        return $"Saved {fileName}.";
    }
}
