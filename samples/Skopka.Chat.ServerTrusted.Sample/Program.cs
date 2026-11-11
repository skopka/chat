// SAMPLE ONLY. Server-trusted mode: the server stores and reads every message (no end-to-end encryption).
// The synthetic sign-in below exists only so the sample can switch between demo users; it is not an
// authentication example. Real hosts authenticate users with their identity provider.
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Skopka.Chat.Attachments;
using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.PostgreSql;
using Skopka.Chat.ServerTrusted.Sample;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/");
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IServerTrustedChatAccessPolicy, SampleAccessPolicy>();
builder.Services.AddSingleton<IServerTrustedChatRateLimiter, UnlimitedServerTrustedChatRateLimiter>();
builder.Services.AddSingleton<IAttachmentStore, SampleMemoryBlobStore>();
builder.Services.AddSingleton<IServerTrustedAttachmentValidator, SampleImageValidator>();

var connectionString = builder.Configuration.GetConnectionString("ServerTrusted");
if (string.IsNullOrWhiteSpace(connectionString))
{
    // In-memory mode: one process, no durability. Never a production configuration.
    var chatOptions = new ServerTrustedChatOptions { MaxTextLength = 5000, MaxAttachmentBytes = 5 * 1024 * 1024 };
    builder.Services.AddSingleton(chatOptions);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<InMemoryServerTrustedChatStore>();
    builder.Services.AddSingleton<IServerTrustedChatStore>(provider => provider.GetRequiredService<InMemoryServerTrustedChatStore>());
    builder.Services.AddSingleton<InProcessServerTrustedChatSignalHub>();
    builder.Services.AddSingleton<IServerTrustedChatSignalSink>(provider => provider.GetRequiredService<InProcessServerTrustedChatSignalHub>());
    builder.Services.AddSingleton<IServerTrustedChatLiveSignals>(provider => provider.GetRequiredService<InProcessServerTrustedChatSignalHub>());
    builder.Services.AddScoped(provider => new ServerTrustedChatService(
        provider.GetRequiredService<IServerTrustedChatStore>(),
        provider.GetRequiredService<IServerTrustedChatAccessPolicy>(),
        provider.GetRequiredService<IServerTrustedChatRateLimiter>(),
        chatOptions,
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<IServerTrustedChatSignalSink>(),
        provider.GetRequiredService<IAttachmentStore>(),
        provider.GetRequiredService<IServerTrustedAttachmentValidator>()));
}
else
{
    builder.Services.AddSkopkaChatServerTrustedPostgreSql(options =>
    {
        options.ConnectionString = connectionString;
        options.Schema = "skopka_chat_trusted_sample";
        options.Chat.MaxTextLength = 5000;
        options.Chat.MaxAttachmentBytes = 5 * 1024 * 1024;
    });
}

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(connectionString))
{
    await using var context = new ServerTrustedChatDbContext(
        ServerTrustedChatDbContext.CreateOptions(connectionString, "skopka_chat_trusted_sample"));
    await context.Database.MigrateAsync();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var chat = scope.ServiceProvider.GetRequiredService<ServerTrustedChatService>();
    await SampleData.EnsureMentorChatAsync(chat);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/signin", async (HttpContext http) =>
{
    var form = await http.Request.ReadFormAsync();
    if (!SampleUsers.TryGet(form["user"].ToString(), out var user))
    {
        return Results.BadRequest();
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")), new Claim(ClaimTypes.Name, user.DisplayName)],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect(user.Key == "manager" ? "/Moderation" : "/Chat");
}).DisableAntiforgery();

app.MapPost("/signout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
}).DisableAntiforgery();

// Host-owned download endpoint: the engine authorizes, the host sets safe headers.
app.MapGet("/chat/attachments/{id:guid}", async (Guid id, HttpContext http, ServerTrustedChatService chat) =>
{
    if (!SampleUsers.TryGetCurrent(http.User, out var user))
    {
        return Results.Unauthorized();
    }

    var download = await chat.OpenAttachmentAsync(user.Id, new TrustedAttachmentId(id), http.RequestAborted);
    if (!download.IsSuccess)
    {
        return Results.NotFound();
    }

    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.CacheControl = "private, no-store";
    http.Response.Headers.ContentSecurityPolicy = "sandbox";
    http.Response.Headers.ContentDisposition = "inline";
    http.Response.ContentType = download.Value!.Info.ContentType;
    await download.Value.CopyToAsync(http.Response.Body, http.RequestAborted);
    return Results.Empty;
}).RequireAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.Run();

namespace Skopka.Chat.ServerTrusted.Sample
{
    internal sealed record SampleUser(string Key, Guid Id, string DisplayName, string Label);

    internal static class SampleUsers
    {
        public static readonly SampleUser Learner = new("learner", Guid.Parse("a0000000-0000-0000-0000-000000000001"), "Ученик (синтетический)", "learner");
        public static readonly SampleUser Teacher = new("teacher", Guid.Parse("a0000000-0000-0000-0000-000000000002"), "Преподаватель (синтетический)", "teacher");
        public static readonly SampleUser Parent = new("parent", Guid.Parse("a0000000-0000-0000-0000-000000000003"), "Родитель (синтетический)", "parent");
        public static readonly SampleUser Manager = new("manager", Guid.Parse("a0000000-0000-0000-0000-000000000004"), "Менеджер (синтетический)", "manager");

        public static IReadOnlyList<SampleUser> All { get; } = [Learner, Teacher, Parent, Manager];

        public static bool TryGet(string key, out SampleUser user)
        {
            user = All.FirstOrDefault(item => item.Key == key)!;
            return user is not null;
        }

        public static bool TryGetCurrent(ClaimsPrincipal principal, out SampleUser user)
        {
            var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            user = All.FirstOrDefault(item => item.Id.ToString("D") == id)!;
            return user is not null;
        }

        public static string NameOf(Guid id) => All.FirstOrDefault(item => item.Id == id)?.DisplayName ?? "—";
    }

    internal static class SampleData
    {
        public const string HostKey = "sample:mentor:learner:teacher";

        public static async Task<TrustedConversation> EnsureMentorChatAsync(ServerTrustedChatService chat)
        {
            var result = await chat.GetOrCreateConversationAsync(HostKey, TrustedConversationKind.Direct,
            [
                new TrustedParticipantSpec(SampleUsers.Learner.Id, TrustedParticipantRole.Writer, SampleUsers.Learner.Label),
                new TrustedParticipantSpec(SampleUsers.Teacher.Id, TrustedParticipantRole.Writer, SampleUsers.Teacher.Label),
                new TrustedParticipantSpec(SampleUsers.Parent.Id, TrustedParticipantRole.Observer, SampleUsers.Parent.Label),
            ]);
            var conversation = result.Value!;
            if (conversation.LastSequence == 0)
            {
                await chat.SendAsync(SampleUsers.Teacher.Id, conversation.Id, new TrustedSendRequest
                {
                    ClientMessageId = Guid.Parse("b0000000-0000-0000-0000-000000000001"),
                    Text = "Здравствуйте! Это синтетическое сообщение sample-хоста.",
                });
            }

            return conversation;
        }
    }

    /// <summary>Sample policy: participants act on their own conversation; only the manager reads report windows and hides.</summary>
    internal sealed class SampleAccessPolicy : IServerTrustedChatAccessPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ServerTrustedAccessContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.Operation switch
            {
                ServerTrustedChatOperation.ReadReportWindow or ServerTrustedChatOperation.Hide => context.ActorUserId == SampleUsers.Manager.Id,
                ServerTrustedChatOperation.AttachHostReference => context.Reference?.Type is "lesson" or "task",
                _ => context.Participant is not null,
            });
    }

    /// <summary>Accepts PNG, JPEG and WebP by content, never by the declared type.</summary>
    internal sealed class SampleImageValidator : IServerTrustedAttachmentValidator
    {
        public ValueTask<ServerTrustedAttachmentVerdict> ValidateAsync(ServerTrustedAttachmentCandidate candidate, CancellationToken cancellationToken = default)
        {
            var bytes = candidate.Content.Span;
            var type = bytes switch
            {
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
                [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
                [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
                _ => null,
            };
            return ValueTask.FromResult(type is null ? ServerTrustedAttachmentVerdict.Rejected : ServerTrustedAttachmentVerdict.Accept(type));
        }
    }

    /// <summary>In-memory plaintext blob store for the sample; use the PostgreSQL or S3 attachment stores in real hosts.</summary>
    internal sealed class SampleMemoryBlobStore : IAttachmentStore
    {
        private readonly ConcurrentDictionary<AttachmentId, (StoredAttachment Metadata, byte[] Content)> _blobs = new();

        public ValueTask<StoredAttachment?> GetMetadataAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_blobs.TryGetValue(attachmentId, out var item) ? item.Metadata : null);

        public async ValueTask<AttachmentStoreResult> TryPutAsync(StoredAttachment attachment, Stream ciphertext, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await ciphertext.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            if (bytes.LongLength != attachment.CiphertextLength || !SHA256.HashData(bytes).AsSpan().SequenceEqual(attachment.CiphertextSha256.Span))
            {
                throw new AttachmentServiceException("The attachment does not match its declared length or hash.");
            }

            return _blobs.TryAdd(attachment.AttachmentId, (attachment, bytes)) ? AttachmentStoreResult.Stored : AttachmentStoreResult.Conflict;
        }

        public async ValueTask CopyToAsync(AttachmentId attachmentId, Stream destination, CancellationToken cancellationToken = default) =>
            await destination.WriteAsync(_blobs[attachmentId].Content, cancellationToken);

        public ValueTask<bool> DeleteAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_blobs.TryRemove(attachmentId, out _));

        public ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }
}
