using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skopka.Chat.Bots.AspNetCore;

internal sealed record UpdatesRequest(int Limit);
internal sealed record AcknowledgeRequest(long UpdateId);
internal sealed record SendRequest(Guid ConversationId, Guid RequestId, string Text, Guid? ReplyToContentId = null)
{
    public override string ToString() => "SendRequest(Payload=[REDACTED])";
}
internal sealed record UpdateResponse(long UpdateId, Guid ConversationId, Guid SenderUserId, Guid ContentId,
    string Text, Guid? ReplyToContentId, bool IsForwarded)
{
    public override string ToString() => "UpdateResponse(Payload=[REDACTED])";
}
internal sealed record UpdatesResponse(UpdateResponse[] Updates);
internal sealed record SendResponse(Guid ContentId, bool Succeeded, int AcceptedCount, int RequiredCount);
internal sealed record ProfileResponse(Guid BotUserId, string Name, string OperatorId, string OperatorName, string Hosting, Guid Revision);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, NumberHandling = JsonNumberHandling.Strict,
    ReadCommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 16,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(UpdatesRequest))]
[JsonSerializable(typeof(AcknowledgeRequest))]
[JsonSerializable(typeof(SendRequest))]
[JsonSerializable(typeof(UpdatesResponse))]
[JsonSerializable(typeof(SendResponse))]
[JsonSerializable(typeof(ProfileResponse))]
internal sealed partial class BotHttpJson : JsonSerializerContext;
