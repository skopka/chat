using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Http.Tests;

public sealed class BackupHttpClientTests
{
    [Theory]
    [InlineData("oversize")]
    [InlineData("foreign")]
    [InlineData("media-type")]
    [InlineData("trailing")]
    [InlineData("error")]
    public async Task Backup_responses_are_bounded_scoped_and_never_reflect_untrusted_content(string fault)
    {
        var account = UserId.New();
        using var handler = new Handler(account, fault); using var http = new HttpClient(handler) { BaseAddress = new Uri("https://backup.example.test/") };
        var client = new SkopkaChatHttpClient(http, new Authorizer(), Options.Create(new SkopkaChatHttpClientOptions
        { AuthenticatedUserId = account.Value, AuthenticatedDeviceId = Guid.NewGuid(), MaxTransientRetries = 0 }), TimeProvider.System);
        var error = await Record.ExceptionAsync(() => client.GetArchiveAsync().AsTask());
        Assert.True(error is ChatBackupException or ChatBackupFormatException); Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-private-remote", error.ToString(), StringComparison.Ordinal);
    }
    private sealed class Authorizer : IChatHttpRequestAuthorizer
    { public ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask; }
    private sealed class Handler(UserId account, string fault) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var data = ChatBackupEncoding.EncodeArchive(new(new("test", fault == "foreign" ? UserId.New() : account), Guid.NewGuid(), Guid.NewGuid()));
            if (fault == "oversize") { data = new byte[ChatBackupLimits.MaxControlBytes + 1]; }
            if (fault == "trailing") { data = [.. data, .. Encoding.UTF8.GetBytes("synthetic-private-remote")]; }
            var result = new HttpResponseMessage(fault == "error" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(fault == "error" ? Encoding.UTF8.GetBytes("synthetic-private-remote") : data) };
            result.Content.Headers.ContentType = new(fault == "media-type" ? "application/json" : ChatBackupHttpRoutes.ContentType);
            return Task.FromResult(result);
        }
    }
}
