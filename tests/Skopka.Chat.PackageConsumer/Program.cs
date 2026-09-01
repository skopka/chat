using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Transport.Http;

var typedContent = new ChatTextContent(ChatContentId.New(), "package consumer");
_ = ChatContentEncoding.Decode(ChatContentEncoding.Encode(typedContent));
_ = new ChatConversationProjection(ConversationId.New());

Type[] packageSurfaces =
[
    typeof(ProtocolVersions),
    typeof(ChatCryptoService),
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

if (assemblies.Length != 7 ||
    assemblies.Distinct(StringComparer.Ordinal).Count() != 7 ||
    assemblies.Any(string.IsNullOrWhiteSpace))
{
    throw new InvalidOperationException(
        "The complete Skopka.Chat package surface could not be loaded.");
}

Console.WriteLine(string.Join(Environment.NewLine, assemblies));
