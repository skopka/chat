using Skopka.Chat.Attachments;
using Skopka.Chat.Attachments.PostgreSql;
using Skopka.Chat.Attachments.S3;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Media;
using Skopka.Chat.Media.FFmpeg;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.NSec;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Transport.Http;
using Skopka.Chat.UI;
using Skopka.Chat.UI.Blazor;

var typedContent = new ChatTextContent(ChatContentId.New(), "package consumer");
var keys = new InMemoryDeviceKeyStore();
var now = TimeProvider.System.GetUtcNow();
var device = await new DeviceIdentityService(keys).CreateAsync(UserId.New(), DeviceId.New(), now);
var account = new DeviceAuthorizationContext("consumer.example.test", device.UserId, "synthetic-session", now.AddHours(1));
var store = new InMemoryServerStore();
var bindingService = new DeviceBindingService(store, store, new NSecDeviceProofVerifier(), TimeProvider.System);
var challenge = await bindingService.IssueAsync(account, DeviceBindingOperation.Enrollment, device);
var proof = await new DeviceBindingProofService(keys, TimeProvider.System).CreateProofAsync(challenge, account, device, challenge.Operation);
_ = await bindingService.CompleteAsync(account, proof);
_ = ChatContentEncoding.Decode(ChatContentEncoding.Encode(typedContent));
_ = ChatContentEncoding.Decode(ChatContentEncoding.Encode(
    new ChatEditContent(ChatContentId.New(), typedContent.ContentId, ChatEditField.Text, "edited")));
_ = new ChatConversationProjection(ConversationId.New());
_ = new ChatViewModel(ConversationId.New(), UserId.New(), new PackageContentSender());
if (typeof(ChatSyncCoordinator).GetMethod(nameof(ChatSyncCoordinator.CommitLocalEchoAsync)) is null)
{
    throw new InvalidOperationException("The durable client synchronization API could not be loaded.");
}

Type[] packageSurfaces =
[
    typeof(ProtocolVersions),
    typeof(AttachmentStorageService),
    typeof(AttachmentDbContext),
    typeof(S3AttachmentStore),
    typeof(ChatCryptoService),
    typeof(ChatSyncCoordinator),
    typeof(SqliteChatEventStore),
    typeof(ChatMediaAttachmentService),
    typeof(FfmpegMediaPreparationService),
    typeof(ChatViewModel),
    typeof(SkopkaChat),
    typeof(SkopkaChatHttpRoutes),
    typeof(SkopkaChatHttpClient),
    typeof(ChatServerEngine),
    typeof(NSecDeviceProofVerifier),
    typeof(SkopkaChatEndpointRouteBuilderExtensions),
    typeof(ChatDbContext)
];

var assemblies = packageSurfaces
    .Select(type => type.Assembly.GetName())
    .Select(name => $"{name.Name} {name.Version}")
    .ToArray();

if (assemblies.Length != 17 ||
    assemblies.Distinct(StringComparer.Ordinal).Count() != 17 ||
    assemblies.Any(string.IsNullOrWhiteSpace))
{
    throw new InvalidOperationException(
        "The complete Skopka.Chat package surface could not be loaded.");
}

Console.WriteLine(string.Join(Environment.NewLine, assemblies));

internal sealed class PackageContentSender : IChatContentSender
{
    public ValueTask<ChatContentSendResult> SendAsync(
        ConversationId conversationId,
        ChatContent content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ChatContentSendResult.Failed);
    }
}
