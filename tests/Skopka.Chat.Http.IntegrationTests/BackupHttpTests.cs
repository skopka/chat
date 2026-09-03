using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Testing;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Http.IntegrationTests;

public sealed class BackupHttpTests
{
    [Fact]
    public async Task Required_PostgreSql_binary_http_survives_restart_has_atomic_versions_and_account_isolation()
    {
        var connectionString = await PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync();
        var storage = new PostgreSqlBackupStorage(connectionString); await storage.MigrateAsync();
        var account = UserId.New(); var service = new ChatBackupService(storage, TimeProvider.System);
        await using var app = await Host(service); using var http = app.GetTestClient();
        var api = Client(http, account); var scope = new ChatBackupScope("backup-http-test", account);
        var archive = new ChatBackupArchive(scope, Guid.NewGuid(), Guid.NewGuid()); using var key = ChatBackupRecoveryKey.Create(); var crypto = new ChatBackupCryptography();
        Assert.Null(await api.GetArchiveAsync()); Assert.True(await api.TryCreateArchiveAsync(archive)); Assert.False(await api.TryCreateArchiveAsync(archive));
        var source = new ReceivedChatContent(MessageId.New(), ConversationId.New(), UserId.New(), DeviceId.New(), DateTimeOffset.UtcNow,
            new ChatTextContent(ChatContentId.New(), "synthetic PG backup private marker 237fd61"));
        var id = Guid.NewGuid(); var part = crypto.Encrypt(key, archive, id, 0, new byte[32], ChatBackupEventEncoding.Encode(source)); var encoded = ChatBackupEncoding.EncodePart(part);
        var seal = crypto.Seal(key, archive, id, null, 1, encoded.Length, SHA256.HashData(encoded), DateTimeOffset.UtcNow);
        await api.BeginUploadAsync(archive.ArchiveId, id);
        Assert.Equal(ChatBackupFailure.Incomplete, (await Assert.ThrowsAsync<ChatBackupException>(() => api.CommitAsync(seal).AsTask())).Failure);
        await api.PutPartAsync(archive.ArchiveId, part); await api.PutPartAsync(archive.ArchiveId, part);
        var different = crypto.Encrypt(key, archive, id, 0, new byte[32], ChatBackupEventEncoding.Encode(source));
        Assert.Equal(ChatBackupFailure.Conflict, (await Assert.ThrowsAsync<ChatBackupException>(() => api.PutPartAsync(archive.ArchiveId, different).AsTask())).Failure);
        Assert.Null(await api.GetHeadAsync(archive.ArchiveId)); Assert.Equal(ChatBackupCommitResult.Committed, await api.CommitAsync(seal));
        Assert.Equal(ChatBackupCommitResult.Duplicate, await api.CommitAsync(seal));
        var leftId = Guid.NewGuid(); var rightId = Guid.NewGuid();
        await api.BeginUploadAsync(archive.ArchiveId, leftId); await api.BeginUploadAsync(archive.ArchiveId, rightId);
        var left = crypto.Seal(key, archive, leftId, seal, 0, 0, new byte[32], DateTimeOffset.UtcNow);
        var right = crypto.Seal(key, archive, rightId, seal, 0, 0, new byte[32], DateTimeOffset.UtcNow);
        var raced = await Task.WhenAll(api.CommitAsync(left).AsTask(), api.CommitAsync(right).AsTask());
        Assert.Single(raced, result => result == ChatBackupCommitResult.Committed); Assert.Single(raced, result => result == ChatBackupCommitResult.Conflict);
        var parent = (await api.GetHeadAsync(archive.ArchiveId))!;
        var loser = raced[0] == ChatBackupCommitResult.Conflict ? leftId : rightId;
        Assert.Equal(ChatBackupCommitResult.Committed, await api.CommitAsync(crypto.Seal(key, archive, loser, parent, 0, 0, new byte[32], DateTimeOffset.UtcNow)));
        // A new store/host does not use any envelope, local key store or old device binding to restore history.
        await using var restarted = await Host(new ChatBackupService(new PostgreSqlBackupStorage(connectionString), TimeProvider.System));
        using var newHttp = restarted.GetTestClient(); var fresh = Client(newHttp, account);
        var head = (await fresh.GetHeadAsync(archive.ArchiveId))!; crypto.Verify(key, archive, head);
        var received = (await fresh.GetPartAsync(archive.ArchiveId, seal.VersionId, 0))!;
        Assert.Equal(ChatBackupEventEncoding.Encode(source), crypto.Decrypt(key, archive, received));
        using var foreignHttp = restarted.GetTestClient(); var foreign = Client(foreignHttp, UserId.New());
        Assert.Null(await foreign.GetArchiveAsync()); Assert.Equal(ChatBackupFailure.NotFound, (await Assert.ThrowsAsync<ChatBackupException>(() => foreign.GetHeadAsync(archive.ArchiveId).AsTask())).Failure);
        Assert.Equal(ChatBackupFailure.Scope, (await Assert.ThrowsAsync<ChatBackupException>(() => service.TryCreateArchiveAsync(new("other", account), archive).AsTask())).Failure);
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT data FROM chat_backup_records WHERE service_id=@service AND user_id=@user";
        command.Parameters.AddWithValue("service", scope.ServiceId); command.Parameters.AddWithValue("user", account.Value);
        await using var reader = await command.ExecuteReaderAsync(); var raw = key.ExportBytes();
        try
        {
            while (await reader.ReadAsync())
            {
                var data = reader.GetFieldValue<byte[]>(0); Assert.True(data.AsSpan().IndexOf(raw) < 0);
                Assert.True(data.AsSpan().IndexOf(Encoding.UTF8.GetBytes("synthetic PG backup private marker")) < 0);
            }
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
        using var unauthorized = await http.GetAsync("/skopka-chat/v1/backups"); Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using (var forgedRequest = new HttpRequestMessage(HttpMethod.Put, "/skopka-chat/v1/backups")
        { Content = new ByteArrayContent(ChatBackupEncoding.EncodeArchive(new(new(scope.ServiceId, UserId.New()), archive.ArchiveId, archive.KeyGeneration))) })
        {
            forgedRequest.Headers.Add("X-Test-Account", account.Value.ToString("D")); forgedRequest.Content.Headers.ContentType = new(ChatBackupHttpRoutes.ContentType);
            using var rejected = await http.SendAsync(forgedRequest); Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }
        using (var duplicated = new HttpRequestMessage(HttpMethod.Get, "/skopka-chat/v1/backups"))
        {
            duplicated.Headers.Add("X-Test-Account", new[] { account.Value.ToString("D"), account.Value.ToString("D") });
            using var rejected = await http.SendAsync(duplicated); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        foreach (var invalidContext in new[] { (Service: "wrong-service", Expired: false), (Service: scope.ServiceId, Expired: true) })
        {
            await using var invalidApp = await Host(service, invalidContext.Service, invalidContext.Expired); using var invalidHttp = invalidApp.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/skopka-chat/v1/backups"); request.Headers.Add("X-Test-Account", account.Value.ToString("D"));
            using var rejected = await invalidHttp.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }
        foreach (var body in new[] { new byte[1], new byte[ChatBackupLimits.MaxControlBytes + 1] })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/skopka-chat/v1/backups") { Content = new ByteArrayContent(body) };
            request.Headers.Add("X-Test-Account", account.Value.ToString("D")); request.Content.Headers.ContentType = new(ChatBackupHttpRoutes.ContentType);
            using var response = await http.SendAsync(request); Assert.Equal(body.Length == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }
    private static SkopkaChatHttpClient Client(HttpClient http, UserId account) => new(http, new Authorizer(account), Options.Create(new SkopkaChatHttpClientOptions
    { AuthenticatedUserId = account.Value, AuthenticatedDeviceId = Guid.NewGuid(), RequireHttps = false, MaxTransientRetries = 0 }), TimeProvider.System);
    private static async Task<WebApplication> Host(ChatBackupService service, string contextService = "backup-http-test", bool expired = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(service); builder.Services.AddSingleton<IChatAuthorizationContextProvider>(new Context(contextService, expired));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("account", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("backup", policy => { policy.PermitLimit = 8; policy.QueueLimit = 0; }));
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapSkopkaChatBackups("backup-http-test", "account", "backup");
        await app.StartAsync(); return app;
    }
    private sealed class Authorizer(UserId account) : IChatHttpRequestAuthorizer
    { public ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) { request.Headers.Add("X-Test-Account", account.Value.ToString("D")); return ValueTask.CompletedTask; } }
    private sealed class Context(string serviceId, bool expired) : IChatAuthorizationContextProvider
    {
        public ValueTask<DeviceAuthorizationContext?> GetContextAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DeviceAuthorizationContext?>(new(serviceId, new(Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!)), "synthetic-session", DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1)));
    }
    // Only this in-process test host trusts this synthetic header. Never register it in a product host.
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var fields = Request.Headers["X-Test-Account"];
            if (fields.Count != 1 || !Guid.TryParseExact(fields[0], "D", out var user) || user == Guid.Empty) { return Task.FromResult(AuthenticateResult.NoResult()); }
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString("D"))], "test")), "test")));
        }
    }
}
