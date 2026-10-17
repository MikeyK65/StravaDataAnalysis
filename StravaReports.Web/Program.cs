using System.Security.Claims;
using ApexCharts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using StravaReports.Core;
using StravaReports.Core.Abstractions;
using StravaReports.Core.Backup;
using StravaReports.Core.Logging;
using StravaReports.Core.Settings;
using StravaReports.Core.Strava;
using StravaReports.Core.Sync;
using StravaReports.Shared.Services;
using StravaReports.Web.Components;
using StravaReports.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Data folder: database, encrypted secrets and Data Protection keys. On Azure App Service point
// DataDirectory at persistent storage, e.g. /home/data.
var dataRoot = builder.Configuration["DataDirectory"] is { Length: > 0 } configured
    ? configured
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataRoot);
var paths = new WebDataPaths(dataRoot);
builder.Services.AddSingleton(paths);

builder.Services.AddDataProtection()
    .SetApplicationName("StravaReports")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));

builder.Services.AddStravaReportsCore(paths.Database);
builder.Services.AddStravaArchive(paths.ArchiveDatabase);
builder.Services.AddStravaReportsUi();
builder.Services.AddApexCharts();

builder.Services.AddSingleton<ISecretStore, DataProtectionSecretStore>();
builder.Services.AddSingleton<IHostInfo, WebHostInfo>();
builder.Services.AddScoped<IAuthLauncher, WebAuthLauncher>();
builder.Services.AddSingleton<DownloadStore>();
builder.Services.AddScoped<IFileSaver, WebFileSaver>();
builder.Services.AddSingleton<WebAccountService>();
builder.Services.AddHostedService<SyncBackgroundService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/account/login";
        o.LogoutPath = "/account/logout";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Cookie.Name = "stravareports.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Azure App Service terminates TLS in front of the app.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

await app.Services.InitialiseDatabaseAsync();
await app.Services.GetRequiredService<WebAccountService>().EnsureSetupCodeAsync();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// ---- Strava OAuth endpoints -------------------------------------------------------------

var auth = app.MapGroup("/auth/strava");

// Web flow: logged-in user -> Strava authorize page -> /callback.
auth.MapGet("/login", async (HttpContext ctx, StravaAuthService strava) =>
{
    var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}/auth/strava/callback";
    try
    {
        var url = await strava.BuildAuthorizeUrlAsync(redirect, strava.CreateState());
        return Results.Redirect(url);
    }
    catch (NotConnectedException ex)
    {
        return Results.Redirect($"/settings?error={Uri.EscapeDataString(ex.Message)}");
    }
}).RequireAuthorization();

auth.MapGet("/callback", async (string? code, string? scope, string? state, string? error,
    StravaAuthService strava, SyncEngine sync, AppLog log) =>
{
    if (!string.IsNullOrEmpty(error)) return Results.Redirect($"/settings?error={Uri.EscapeDataString(error)}");
    if (!strava.ValidateState(state)) return Results.Redirect("/settings?error=Invalid%20or%20expired%20state;%20please%20try%20again.");
    if (string.IsNullOrEmpty(code)) return Results.Redirect("/settings?error=No%20authorization%20code%20returned.");
    try
    {
        await strava.ExchangeCodeAsync(code, scope);
        sync.StartInBackground(SyncTrigger.Manual);
        return Results.Redirect("/settings?connected=1");
    }
    catch (Exception ex)
    {
        await log.ErrorAsync("Auth", $"Code exchange failed: {ex.Message}");
        return Results.Redirect($"/settings?error={Uri.EscapeDataString(ex.Message)}");
    }
}).RequireAuthorization();

// Mobile relay: Strava only allows one callback domain, so the MAUI app sends Strava here and this
// bounces the code into the app's custom URL scheme. Holds no secrets; only whitelisted params pass.
auth.MapGet("/app-callback", (string? code, string? scope, string? state, string? error) =>
{
    var q = new List<string>();
    if (code is not null) q.Add($"code={Uri.EscapeDataString(code)}");
    if (scope is not null) q.Add($"scope={Uri.EscapeDataString(scope)}");
    if (state is not null) q.Add($"state={Uri.EscapeDataString(state)}");
    if (error is not null) q.Add($"error={Uri.EscapeDataString(error)}");
    return Results.Redirect($"{StravaDefaults.AppUrlScheme}://oauth?{string.Join('&', q)}");
}).AllowAnonymous();

// ---- Strava webhooks (FR-17) ---------------------------------------------------------------
// Anonymous because Strava calls them. GET answers the subscription challenge with the stored verify
// token; POST stores the event (filtered on subscription and athlete) and returns 200 within Strava's
// 2-second limit. Events are processed by the next sync run, which confirms each one with the API.

app.MapGet("/webhooks/strava", async (HttpContext ctx, StravaWebhookService hooks) =>
{
    var q = ctx.Request.Query;
    var challenge = await hooks.ValidateChallengeAsync(q["hub.mode"], q["hub.verify_token"], q["hub.challenge"]);
    return challenge is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Json(new Dictionary<string, string> { ["hub.challenge"] = challenge });
}).AllowAnonymous().WithoutStatusCodePages();

app.MapPost("/webhooks/strava", async (HttpContext ctx, StravaWebhookService hooks, ILogger<StravaWebhookService> logger) =>
{
    if (ctx.Request.ContentLength is > 16_384) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    StravaWebhookPayload? payload;
    try { payload = await ctx.Request.ReadFromJsonAsync<StravaWebhookPayload>(StravaJson.Options, ctx.RequestAborted); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
    if (payload is null) return Results.BadRequest();
    if (!await hooks.AcceptAsync(payload, ctx.RequestAborted))
        logger.LogInformation("Ignored webhook event for subscription {Subscription}", payload.SubscriptionId);
    // 200 either way: Strava retries anything else, and an ignored event needs no retry.
    return Results.Ok();
}).AllowAnonymous().WithoutStatusCodePages().DisableAntiforgery();

// ---- Downloads and transfer -------------------------------------------------------------

// One-time links for files the signed-in browser asked for (CSV exports, backups).
app.MapGet("/download/{token}", (string token, DownloadStore store, HttpContext ctx) =>
{
    if (store.Take(token) is not { } file) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "no-store";
    var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    return Results.File(stream, file.ContentType, file.FileName);
}).RequireAuthorization().WithoutStatusCodePages();

// The MAUI app pulls a backup with a transfer code created on the Backup page. Anonymous because the
// app has no web login; the single-use, expiring, rate-limited code is the credential.
app.MapGet("/transfer/{code}", (string code, TransferCodeService transfers, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    var result = transfers.Redeem(code);
    return result.Status switch
    {
        RedeemStatus.Ok => Results.File(
            new FileStream(result.Path!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose),
            "application/zip", result.FileName),
        RedeemStatus.Expired => Results.StatusCode(StatusCodes.Status410Gone),
        RedeemStatus.LockedOut => Results.StatusCode(StatusCodes.Status429TooManyRequests),
        _ => Results.NotFound(),
    };
}).AllowAnonymous().WithoutStatusCodePages();

// ---- Account (single user) ----------------------------------------------------------------

app.MapPost("/account/signout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/login");
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(StravaReports.Shared._Imports).Assembly)
    .RequireAuthorization();

app.Run();

public static class EndpointExtensions
{
    /// <summary>
    /// Machine endpoints (Strava, the app) must get their real status codes, not the HTML not-found
    /// page, which requires a login and so would turn every 4xx into a redirect to the login page.
    /// </summary>
    public static RouteHandlerBuilder WithoutStatusCodePages(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            if (ctx.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } f) f.Enabled = false;
            return await next(ctx);
        });
}

public static class AccountSignIn
{
    public static Task SignInOwnerAsync(this HttpContext ctx) =>
        ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, WebAccountService.UserName)], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });
}
