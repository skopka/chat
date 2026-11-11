using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skopka.Chat.ServerTrusted;

/// <summary>Stable event types and versions of the server-trusted mode.</summary>
public static class ServerTrustedChatEventTypes
{
    /// <summary>A message was committed.</summary>
    public const string MessageCreated = "skopka.chat.server-trusted.message-created";

    /// <summary>A participant's read marker advanced.</summary>
    public const string ReadMarkerAdvanced = "skopka.chat.server-trusted.read-marker-advanced";

    /// <summary>A message was reported.</summary>
    public const string MessageReported = "skopka.chat.server-trusted.message-reported";

    /// <summary>Current schema version of every event above.</summary>
    public const int Version = 1;
}

/// <summary>Version-one metadata that a message was created. It never contains text, file names or reference IDs.</summary>
public sealed record TrustedMessageCreatedEventV1(
    Guid EventId,
    Guid MessageId,
    Guid ConversationId,
    long Sequence,
    Guid AuthorUserId,
    string AuthorLabel,
    int AttachmentCount,
    IReadOnlyList<string> ReferenceTypes,
    DateTimeOffset CreatedAt);

/// <summary>Version-one metadata that a read marker advanced.</summary>
public sealed record TrustedReadMarkerAdvancedEventV1(
    Guid EventId,
    Guid ConversationId,
    Guid UserId,
    long ReadThroughSequence,
    DateTimeOffset At);

/// <summary>Version-one metadata that a message was reported. The comment is never included.</summary>
public sealed record TrustedMessageReportedEventV1(
    Guid EventId,
    Guid ReportId,
    Guid MessageId,
    Guid ConversationId,
    Guid ReporterUserId,
    string ReasonCode,
    DateTimeOffset CreatedAt);

/// <summary>Creates exact event bytes for the store to write in the domain transaction.</summary>
public static class ServerTrustedChatEventFactory
{
    /// <summary>Message-created event.</summary>
    public static ServerTrustedChatEvent MessageCreated(Guid eventId, TrustedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = new TrustedMessageCreatedEventV1(
            eventId,
            message.Id.Value,
            message.ConversationId.Value,
            message.Sequence,
            message.AuthorUserId,
            message.AuthorLabel,
            message.Attachments.Count,
            message.References.Select(static item => item.Type).Distinct(StringComparer.Ordinal).ToArray(),
            message.CreatedAt);
        return Create(eventId, ServerTrustedChatEventTypes.MessageCreated, message.CreatedAt, message.ConversationId,
            JsonSerializer.SerializeToUtf8Bytes(payload, ServerTrustedChatEventJsonContext.Default.TrustedMessageCreatedEventV1));
    }

    /// <summary>Read-marker event.</summary>
    public static ServerTrustedChatEvent ReadMarkerAdvanced(Guid eventId, TrustedConversationId conversationId, Guid userId, long readThroughSequence, DateTimeOffset at)
    {
        var payload = new TrustedReadMarkerAdvancedEventV1(eventId, conversationId.Value, userId, readThroughSequence, at);
        return Create(eventId, ServerTrustedChatEventTypes.ReadMarkerAdvanced, at, conversationId,
            JsonSerializer.SerializeToUtf8Bytes(payload, ServerTrustedChatEventJsonContext.Default.TrustedReadMarkerAdvancedEventV1));
    }

    /// <summary>Report event.</summary>
    public static ServerTrustedChatEvent MessageReported(Guid eventId, TrustedReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var payload = new TrustedMessageReportedEventV1(
            eventId, report.Id.Value, report.MessageId.Value, report.ConversationId.Value, report.ReporterUserId, report.ReasonCode, report.CreatedAt);
        return Create(eventId, ServerTrustedChatEventTypes.MessageReported, report.CreatedAt, report.ConversationId,
            JsonSerializer.SerializeToUtf8Bytes(payload, ServerTrustedChatEventJsonContext.Default.TrustedMessageReportedEventV1));
    }

    private static ServerTrustedChatEvent Create(Guid eventId, string type, DateTimeOffset at, TrustedConversationId conversationId, byte[] payload) =>
        new(eventId, type, ServerTrustedChatEventTypes.Version, at, conversationId.Value.ToString("D"), payload);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TrustedMessageCreatedEventV1))]
[JsonSerializable(typeof(TrustedReadMarkerAdvancedEventV1))]
[JsonSerializable(typeof(TrustedMessageReportedEventV1))]
internal sealed partial class ServerTrustedChatEventJsonContext : JsonSerializerContext;
