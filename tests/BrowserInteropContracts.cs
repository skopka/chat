using System.Text.Json.Serialization;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Browser.Testing;

// Synthetic test fixtures only. Never mounted by the sample/production BFF.
internal sealed record InteropFixture(PublicDeviceResponse Alice, PublicDeviceResponse Bob, byte[] AliceEncryption,
    byte[] AliceSigning, byte[] BobEncryption, byte[] BobSigning, EncryptedEnvelopeDto Envelope,
    byte[] EnvelopeCanonical, byte[] BindingCanonical, byte[] BindingSignature,
    byte[] BackupKey, byte[] BackupArchive, byte[] BackupPart, byte[] BackupSeal);
internal sealed record InteropResult(EncryptedEnvelopeDto Envelope, byte[] BindingSignature, byte[] BackupPart, byte[] BackupSeal);
[JsonSerializable(typeof(InteropFixture))]
[JsonSerializable(typeof(InteropResult))]
internal sealed partial class InteropJson : JsonSerializerContext;
