namespace StravaReports.Core.Abstractions;

/// <summary>
/// Secure storage for secrets (client secret, tokens). MAUI uses SecureStorage (Keychain on iOS);
/// the web app uses ASP.NET Core Data Protection, encrypted at rest.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

public record ConnectResult(bool Completed, string? Error = null)
{
    /// <summary>The host is navigating away (web redirect); the flow completes on another request.</summary>
    public static ConnectResult Redirecting => new(false);
}

/// <summary>Starts the Strava OAuth flow in a platform-appropriate way.</summary>
public interface IAuthLauncher
{
    /// <summary>Shown in Settings so the user knows what to enter as the callback domain on Strava.</summary>
    string RedirectHint { get; }

    Task<ConnectResult> ConnectAsync(CancellationToken ct = default);
}

/// <summary>Describes what the hosting platform does about sync scheduling.</summary>
public interface IHostInfo
{
    string PlatformName { get; }
    /// <summary>True when a background service syncs on a schedule (web); false when sync is on launch / manual (MAUI).</summary>
    bool HasScheduledSync { get; }
    /// <summary>Relative URL of a sign-out page, or null when the host has no login (MAUI).</summary>
    string? SignOutPath { get; }
    /// <summary>True when the host is reachable from the internet and can receive Strava webhooks (web).</summary>
    bool SupportsWebhooks { get; }
    /// <summary>True when the app can pull a backup from the web app with a transfer code (MAUI).</summary>
    bool CanImportFromWebApp { get; }
}

/// <summary>Hands a generated file (CSV export) to the user: a browser download on the web, the share sheet on MAUI.</summary>
public interface IFileSaver
{
    /// <summary>The stream is read to the end; large files (backups) are never held in memory.</summary>
    /// <returns>A short message describing where the file went.</returns>
    Task<string> SaveAsync(string fileName, string contentType, Stream content, CancellationToken ct = default);
}

public static class FileSaverExtensions
{
    public static Task<string> SaveAsync(this IFileSaver saver, string fileName, string contentType, byte[] content, CancellationToken ct = default) =>
        saver.SaveAsync(fileName, contentType, new MemoryStream(content), ct);
}
