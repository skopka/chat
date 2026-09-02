using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Server.NSec;

// Local demonstration only: two synthetic accounts, no OAuth/passwords, no private device keys or plaintext chat API.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5200");
builder.Logging.ClearProviders();
if (builder.Environment.WebRootPath is null || !Directory.Exists(builder.Environment.WebRootPath))
{ throw new InvalidOperationException("Supply --webroot with the published browser sample's wwwroot directory."); }
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "Skopka.Chat.LocalDemo";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // HTTP loopback demo only; production requires Always + HTTPS.
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-Chat-CSRF"; options.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddSingleton<ConcurrentDictionary<string, DeviceAuthorizationContext>>();
builder.Services.AddScoped<IChatAuthorizationContextProvider, DemoContextProvider>();
builder.Services.AddSingleton<IDeviceProofVerifier, NSecDeviceProofVerifier>();
var store = new InMemoryServerStore();
builder.Services.AddSingleton<IDeviceRepository>(store);
builder.Services.AddSingleton<IConversationRepository>(store);
builder.Services.AddSingleton<IEnvelopeRepository>(store);
builder.Services.AddSingleton<IDeviceBindingRepository>(store);
builder.Services.AddScoped<ChatServerEngine>();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("skopka-chat-challenges", limit => { limit.PermitLimit = 120; limit.Window = TimeSpan.FromMinutes(1); });
    options.AddFixedWindowLimiter("skopka-chat-proofs", limit => { limit.PermitLimit = 120; limit.Window = TimeSpan.FromMinutes(1); });
});
builder.Services.AddSkopkaChatDeviceBinding(options => options.ServiceId = "skopka.chat.browser.demo");
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) || context.Request.Host.Value != "127.0.0.1:5200")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    try { await next(context); }
    catch (Exception) when (!context.Response.HasStarted) { context.Response.Clear(); context.Response.StatusCode = 500; }
});
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { context.Response.StatusCode = 403; return; }
    }
    await next(context);
});
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/demo/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Json(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
app.MapPost("/demo/login/{account}", async (string account, HttpContext context,
    ConcurrentDictionary<string, DeviceAuthorizationContext> sessions, TimeProvider clock) =>
{
    var id = account switch
    {
        "alice" => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "bob" => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        _ => Guid.Empty
    };
    if (id == Guid.Empty) { return Results.BadRequest(); }
    var session = Guid.NewGuid().ToString("N");
    var deadline = clock.GetUtcNow().AddHours(2);
    var trusted = new DeviceAuthorizationContext("skopka.chat.browser.demo", new UserId(id), session, deadline);
    sessions[session] = trusted;
    var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", id.ToString("D")), new Claim("sid", session)], CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { ExpiresUtc = deadline });
    return Results.NoContent();
});
app.MapPost("/demo/logout", async (HttpContext context, ConcurrentDictionary<string, DeviceAuthorizationContext> sessions) =>
{
    if (context.User.FindFirst("sid") is { } session) { sessions.TryRemove(session.Value, out _); }
    await context.SignOutAsync();
    return Results.NoContent();
});
app.MapGet("/demo/account", async (HttpContext context, IChatAuthorizationContextProvider provider) =>
{
    var trusted = await provider.GetContextAsync(context);
    return trusted is null ? Results.Unauthorized() : Results.Json(new { serviceId = trusted.ServiceId, userId = trusted.UserId.Value, sessionReference = trusted.SessionReference, expiresAt = trusted.ExpiresAt });
}).RequireAuthorization();
app.MapSkopkaChatApi();
app.UseDefaultFiles();
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".dat"] = "application/octet-stream";
contentTypes.Mappings[".mjs"] = "text/javascript";
contentTypes.Mappings[".wasm"] = "application/wasm";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
await app.RunAsync();

internal sealed class DemoContextProvider(ConcurrentDictionary<string, DeviceAuthorizationContext> sessions, TimeProvider clock) : IChatAuthorizationContextProvider
{
    public ValueTask<DeviceAuthorizationContext?> GetContextAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claims = context.User.Identities.Where(identity => identity.IsAuthenticated).SelectMany(identity => identity.Claims).ToArray();
        var users = claims.Where(claim => claim.Type == "sub").ToArray();
        var ids = claims.Where(claim => claim.Type == "sid").ToArray();
        return ValueTask.FromResult(users.Length == 1 && ids.Length == 1 && sessions.TryGetValue(ids[0].Value, out var session) &&
            session.ExpiresAt > clock.GetUtcNow() && session.UserId.Value.ToString("D") == users[0].Value ? session : null);
    }
}
