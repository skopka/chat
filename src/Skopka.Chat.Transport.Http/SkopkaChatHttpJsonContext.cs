using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skopka.Chat.Transport.Http;

/// <summary>Source-generated System.Text.Json metadata for the complete HTTP contract.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RegisterDeviceRequest))]
[JsonSerializable(typeof(CreateConversationRequest))]
[JsonSerializable(typeof(PublicDeviceResponse))]
[JsonSerializable(typeof(PersonalConversationResponse))]
[JsonSerializable(typeof(EncryptedEnvelopeDto))]
[JsonSerializable(typeof(PendingDeliveryResponse[]))]
[JsonSerializable(typeof(SubmitEnvelopeResponse))]
public sealed partial class SkopkaChatHttpJsonContext : JsonSerializerContext;
