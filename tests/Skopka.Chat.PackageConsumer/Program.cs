using Skopka.Chat.Attachments;
using Skopka.Chat.Attachments.PostgreSql;
using Skopka.Chat.Attachments.S3;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Media;
using Skopka.Chat.Media.FFmpeg;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Transport.Http;
using Skopka.Chat.UI;
using Skopka.Chat.UI.Blazor;

var typedContent = new ChatTextContent(ChatContentId.New(), "package consumer");
_ = ChatContentEncoding.Decode(ChatContentEncoding.Encode(typedContent));
_ = new ChatConversationProjection(ConversationId.New());
_ = new ChatViewModel(ConversationId.New(), UserId.New(), new PackageContentSender());

Type[] packageSurfaces =
[
    typeof(ProtocolVersions),
    typeof(AttachmentStorageService),
    typeof(AttachmentDbContext),
    typeof(S3AttachmentStore),
    typeof(ChatCryptoService),
    typeof(ChatMediaAttachmentService),
    typeof(FfmpegMediaPreparationService),
    typeof(ChatViewModel),
    typeof(SkopkaChat),
    typeof(SkopkaChatHttpRoutes),
    typeof(SkopkaChatHttpClient),
    typeof(ChatServerEngine),
    typeof(SkopkaChatEndpointRouteBuilderExtensions),
    typeof(ChatDbContext)
];

var assemblies = packageSurfaces
    .Select(type => type.Assembly.GetName())
    .Select(name => $"{name.Name} {name.Version}")
    .ToArray();

if (assemblies.Length != 14 ||
    assemblies.Distinct(StringComparer.Ordinal).Count() != 14 ||
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
