using Skopka.Chat.Protocol;

namespace Skopka.Chat.Protocol.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Canonical_envelope_matches_golden_vector()
    {
        var actual = Convert.ToHexString(CanonicalEnvelopeEncoding.EncodeEnvelope(CreateGoldenEnvelope()));

        Assert.Equal(
            "00000017736B6F706B612E636861742E656E76656C6F70652E76310000010900000018736B6F706B612E636861742E7369676E61747572652E76310000008D00000015736B6F706B612E636861742E6865616465722E76310000000100112233445566778899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0F112233445566778899AABBCCDDEEFF00FFEEDDCCBBAA998877665544332211001234567890ABCDEF1234567890ABCDEFFEDCBA0987654321FEDCBA0987654321000001A3185C5000000001A31D82AC0000000020000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F00000018202122232425262728292A2B2C2D2E2F303132333435363700000004CAFEBABE00000010A5A5A5A5A5A5A5A5A5A5A5A5A5A5A5A500000040FFFEFDFCFBFAF9F8F7F6F5F4F3F2F1F0EFEEEDECEBEAE9E8E7E6E5E4E3E2E1E0DFDEDDDCDBDAD9D8D7D6D5D4D3D2D1D0CFCECDCCCBCAC9C8C7C6C5C4C3C2C1C0",
            actual);
    }

    [Fact]
    public void Oversized_ciphertext_is_rejected()
    {
        var valid = CreateGoldenEnvelope();
        var oversized = Clone(valid, ciphertext: new byte[ProtocolLimits.MaxCiphertextBytes + 1]);

        Assert.Throws<ProtocolValidationException>(() => ProtocolValidator.Validate(oversized));
    }

    [Fact]
    public void Protocol_has_no_framework_or_client_dependencies()
    {
        var references = typeof(EncryptedEnvelope).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();

        Assert.DoesNotContain("Skopka.Chat.Client", references);
        Assert.DoesNotContain("Microsoft.AspNetCore", references);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("NSec.Cryptography", references);
    }

    private static EncryptedEnvelope CreateGoldenEnvelope() => new(
        ProtocolVersions.V1,
        new MessageId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
        new ConversationId(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f")),
        new DeviceId(Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00")),
        new DeviceId(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")),
        new KeyId(Guid.Parse("12345678-90ab-cdef-1234-567890abcdef")),
        new KeyId(Guid.Parse("fedcba09-8765-4321-fedc-ba0987654321")),
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000),
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_086_400_000),
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
        Enumerable.Range(32, 24).Select(value => (byte)value).ToArray(),
        [0xCA, 0xFE, 0xBA, 0xBE],
        Enumerable.Repeat((byte)0xA5, 16).ToArray(),
        Enumerable.Range(0, 64).Select(value => (byte)(255 - value)).ToArray());

    private static EncryptedEnvelope Clone(EncryptedEnvelope source, byte[] ciphertext) => new(
        source.ProtocolVersion,
        source.MessageId,
        source.ConversationId,
        source.SenderDeviceId,
        source.RecipientDeviceId,
        source.SenderSigningKeyId,
        source.RecipientEncryptionKeyId,
        source.SentAt,
        source.ExpiresAt,
        source.EphemeralPublicKey.Span,
        source.Nonce.Span,
        ciphertext,
        source.AuthenticationTag.Span,
        source.Signature.Span);
}
