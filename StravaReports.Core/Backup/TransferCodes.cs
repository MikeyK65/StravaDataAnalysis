using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using StravaReports.Core.Logging;

namespace StravaReports.Core.Backup;

public record TransferTicket(string Code, DateTime ExpiresUtc, long Bytes, BackupManifest Manifest);

public enum RedeemStatus { Ok, NotFound, Expired, LockedOut }

public record RedeemResult(RedeemStatus Status, string? Path = null, string? FileName = null);

/// <summary>
/// Web app side of "import the web database into the app": makes a backup and a short, single-use code.
/// The app downloads the backup once with that code. Codes are random (about 59 bits), expire after
/// 10 minutes and work once; repeated wrong guesses lock the endpoint for 15 minutes.
/// </summary>
public class TransferCodeService(BackupService backups, AppLog log, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    public const int MaxFailures = 10;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I/L
    private const int CodeLength = 12;

    private readonly ConcurrentDictionary<string, (string Path, string FileName, DateTime Expires)> _codes = new();
    private readonly ConcurrentQueue<DateTime> _failures = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<TransferTicket> CreateAsync(BackupOptions options, CancellationToken ct = default)
    {
        Sweep();
        var file = await backups.CreateAsync(options, ct);
        var code = NewCode();
        var expires = Now + Lifetime;
        _codes[code] = (file.Path, file.FileName, expires);
        await log.InfoAsync("Backup", $"Transfer code issued; expires {expires:HH:mm} UTC.");
        return new TransferTicket(Format(code), expires, file.Bytes, file.Manifest);
    }

    /// <summary>Single use: a successful redeem removes the code. The caller deletes the file after sending it.</summary>
    public RedeemResult Redeem(string? code)
    {
        Sweep();
        if (_failures.Count >= MaxFailures) return new RedeemResult(RedeemStatus.LockedOut);
        var key = Normalise(code);
        if (key.Length == CodeLength && _codes.TryRemove(key, out var entry))
        {
            if (entry.Expires >= Now) return new RedeemResult(RedeemStatus.Ok, entry.Path, entry.FileName);
            TryDelete(entry.Path);
            return new RedeemResult(RedeemStatus.Expired);
        }
        _failures.Enqueue(Now);
        _ = log.WarnAsync("Backup", "A transfer download was attempted with an unknown code.");
        return new RedeemResult(RedeemStatus.NotFound);
    }

    /// <summary>Backups behind unused codes are personal data; remove them on shutdown.</summary>
    public void Dispose()
    {
        foreach (var code in _codes.Keys)
            if (_codes.TryRemove(code, out var entry)) TryDelete(entry.Path);
        GC.SuppressFinalize(this);
    }

    public void Revoke(string code)
    {
        if (_codes.TryRemove(Normalise(code), out var entry)) TryDelete(entry.Path);
    }

    public static string Normalise(string? code) =>
        new((code ?? "").ToUpperInvariant().Where(c => Alphabet.Contains(c)).ToArray());

    public static string Format(string code) => string.Join('-', code.Chunk(4).Select(c => new string(c)));

    private static string NewCode() =>
        new(Enumerable.Range(0, CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    private void Sweep()
    {
        var now = Now;
        foreach (var (code, entry) in _codes)
            if (entry.Expires < now && _codes.TryRemove(code, out _)) TryDelete(entry.Path);
        while (_failures.TryPeek(out var t) && now - t > LockoutWindow) _failures.TryDequeue(out _);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

/// <summary>App side: downloads a backup from the web app with a transfer code.</summary>
public class TransferClient(HttpClient http)
{
    public async Task DownloadAsync(string webAppUrl, string code, string destinationPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(webAppUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("Enter the web app's address, e.g. https://reports.example.com.");
        var local = baseUri.IsLoopback || baseUri.Host == "10.0.2.2";
        if (baseUri.Scheme != Uri.UriSchemeHttps && !local)
            throw new InvalidOperationException("The web app must use HTTPS so the backup is encrypted in transit.");

        var key = TransferCodeService.Normalise(code);
        using var resp = await http.GetAsync(new Uri(baseUri, $"transfer/{key}"), HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)resp.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("The web app redirected instead of sending the backup. Check the address (it should be the site's root URL).");
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(resp.StatusCode switch
            {
                HttpStatusCode.NotFound => "That code was not found. Codes work once and expire after 10 minutes; create a new one on the web app.",
                HttpStatusCode.Gone => "That code has expired. Create a new one on the web app.",
                HttpStatusCode.TooManyRequests => "Too many wrong codes were tried. Wait 15 minutes and try again.",
                _ => $"The web app returned {(int)resp.StatusCode}.",
            });
        }
        if (resp.Content.Headers.ContentType?.MediaType != "application/zip")
            throw new InvalidOperationException("The web app did not send a backup file. Check the address.");
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destinationPath);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            progress?.Report(total);
        }
    }
}
