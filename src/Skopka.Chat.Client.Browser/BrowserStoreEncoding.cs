using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Browser;

internal static class BrowserStoreEncoding
{
    internal static byte[] Encode<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(value, type);
    internal static T Decode<T>(byte[]? bytes, JsonTypeInfo<T> type)
    {
        try
        {
            if (bytes is null || bytes.Length is < 1 or > 16 * 1024 * 1024) { throw new BrowserStorageException("corrupt"); }
            return JsonSerializer.Deserialize(bytes, type) ?? throw new BrowserStorageException("corrupt");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or OverflowException)
        { throw new BrowserStorageException("corrupt"); }
        finally { if (bytes is not null) { CryptographicOperations.ZeroMemory(bytes); } }
    }
    internal static void Version(int version) { if (version != 1) { throw new BrowserStorageException("corrupt"); } }
    internal static string Id(Guid id) => id != Guid.Empty ? id.ToString("N") : throw new ArgumentException("A non-empty identifier is required.");
}

internal sealed record BrowserKeyRecord(int Version, Guid User, Guid Device, Guid Key, byte[] Encryption, byte[] Signing);
internal sealed record BrowserIdentityRecord(int Version, string Partition, Guid Device, Guid Key, DateTimeOffset CreatedAt,
    PublicDeviceResponse? PublicDevice, bool Registered, bool Revoked);
internal sealed record BrowserEventRecord(int Version, Guid Message, Guid Conversation, Guid User, Guid Device, DateTimeOffset SentAt, byte[] Content)
{
    public static BrowserEventRecord FromDomain(ReceivedChatContent value) => new(1, value.DeliveryMessageId.Value, value.ConversationId.Value,
        value.SenderUserId.Value, value.SenderDeviceId.Value, value.SentAt.ToUniversalTime(), ChatContentEncoding.Encode(value.Content));
    public ReceivedChatContent ToDomain()
    {
        BrowserStoreEncoding.Version(Version);
        return new(new MessageId(Message), new ConversationId(Conversation), new UserId(User), new DeviceId(Device), SentAt, ChatContentEncoding.Decode(Content));
    }
}
internal sealed record BrowserEnvelopeRecord(EncryptedEnvelopeDto Envelope, bool Accepted);
internal sealed record BrowserPlanRecord(int Version, Guid Conversation, Guid Content, Guid User, Guid Device, Guid Echo,
    DateTimeOffset SentAt, byte[] Hash, BrowserEnvelopeRecord[] Envelopes, DateTimeOffset? CompletedAt)
{
    public static BrowserPlanRecord FromDomain(ChatFanOutPlan plan) => new(1, plan.ConversationId.Value, plan.ContentId.Value,
        plan.SenderUserId.Value, plan.SenderDeviceId.Value, plan.LocalEchoMessageId.Value, plan.SentAt, plan.ContentHash.ToArray(),
        plan.Envelopes.Select(item => new BrowserEnvelopeRecord(EncryptedEnvelopeDto.FromDomain(item.Envelope), item.IsAccepted)).ToArray(), plan.CompletedAt);
    public ChatFanOutPlan ToDomain()
    {
        BrowserStoreEncoding.Version(Version);
        return new(new ConversationId(Conversation), new ChatContentId(Content), new UserId(User), new DeviceId(Device), new MessageId(Echo),
            SentAt, Hash, Envelopes.Select(item => new ChatEnvelopePlanItem(item.Envelope.ToDomain(), item.Accepted)).ToArray(), CompletedAt);
    }
}
internal sealed record BrowserJobRecord(int Version, Guid Conversation, Guid Content, Guid User, Guid Device, byte[] Body);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false, MaxDepth = 16)]
[JsonSerializable(typeof(BrowserKeyRecord))]
[JsonSerializable(typeof(BrowserIdentityRecord))]
[JsonSerializable(typeof(BrowserEventRecord))]
[JsonSerializable(typeof(BrowserPlanRecord))]
[JsonSerializable(typeof(BrowserJobRecord))]
internal sealed partial class BrowserStoreJson : JsonSerializerContext;
