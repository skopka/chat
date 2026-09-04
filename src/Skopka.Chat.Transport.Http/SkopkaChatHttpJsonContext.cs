using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skopka.Chat.Transport.Http;

/// <summary>Source-generated System.Text.Json metadata for the complete HTTP contract.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    AllowDuplicateProperties = false,
    AllowTrailingCommas = false,
    MaxDepth = SkopkaChatHttpJson.MaximumDepth,
    NumberHandling = JsonNumberHandling.Strict,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RegisterDeviceRequest))]
[JsonSerializable(typeof(CreateConversationRequest))]
[JsonSerializable(typeof(GetOrCreateConversationRequest))]
[JsonSerializable(typeof(CreateGroupConversationRequest))]
[JsonSerializable(typeof(RenameGroupConversationRequest))]
[JsonSerializable(typeof(AddGroupMemberRequest))]
[JsonSerializable(typeof(ChangeGroupMemberRoleRequest))]
[JsonSerializable(typeof(PublicDeviceResponse))]
[JsonSerializable(typeof(PersonalConversationResponse))]
[JsonSerializable(typeof(ConversationDirectoryResponse))]
[JsonSerializable(typeof(GroupConversationMemberResponse))]
[JsonSerializable(typeof(GroupConversationResponse))]
[JsonSerializable(typeof(GroupConversationDirectoryResponse))]
[JsonSerializable(typeof(DeviceDirectoryResponse))]
[JsonSerializable(typeof(EncryptedEnvelopeDto))]
[JsonSerializable(typeof(PendingDeliveryResponse[]))]
[JsonSerializable(typeof(SubmitEnvelopeResponse))]
[JsonSerializable(typeof(DeviceBindingIssueRequest))]
[JsonSerializable(typeof(DeviceBindingChallengeResponse))]
[JsonSerializable(typeof(DeviceBindingCompleteRequest))]
[JsonSerializable(typeof(DeviceBindingResultResponse))]
public sealed partial class SkopkaChatHttpJsonContext : JsonSerializerContext;

/// <summary>Applies the strict JSON profile used by the Skopka.Chat HTTP boundary.</summary>
public static class SkopkaChatHttpJson
{
    /// <summary>Maximum nesting depth accepted from an untrusted HTTP peer.</summary>
    public const int MaximumDepth = 16;

    /// <summary>Configures serializer options to reject ambiguous or unexpected JSON.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AllowDuplicateProperties = false;
        options.AllowTrailingCommas = false;
        options.MaxDepth = MaximumDepth;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.PropertyNameCaseInsensitive = false;
        options.ReadCommentHandling = JsonCommentHandling.Disallow;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;

        if (!options.TypeInfoResolverChain.Contains(SkopkaChatHttpJsonContext.Default))
        {
            options.TypeInfoResolverChain.Insert(0, SkopkaChatHttpJsonContext.Default);
        }
    }
}
