using System.Buffers.Binary;
using System.Text;

namespace Skopka.Chat.Protocol;

/// <summary>Produces the only byte representations accepted for AEAD and signatures in protocol v1.</summary>
public static class CanonicalEnvelopeEncoding
{
    private static readonly byte[] HeaderDomain = Encoding.ASCII.GetBytes("skopka.chat.header.v1");
    private static readonly byte[] AadDomain = Encoding.ASCII.GetBytes("skopka.chat.aad.v1");
    private static readonly byte[] SignatureDomain = Encoding.ASCII.GetBytes("skopka.chat.signature.v1");
    private static readonly byte[] EnvelopeDomain = Encoding.ASCII.GetBytes("skopka.chat.envelope.v1");

    /// <summary>Encodes authenticated routing fields using network byte order and RFC 4122 UUID byte order.</summary>
    public static byte[] EncodeHeader(EncryptedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var output = new MemoryStream(192);
        WriteBytes(output, HeaderDomain);
        WriteInt32(output, envelope.ProtocolVersion);
        WriteGuid(output, envelope.MessageId.Value);
        WriteGuid(output, envelope.ConversationId.Value);
        WriteGuid(output, envelope.SenderDeviceId.Value);
        WriteGuid(output, envelope.RecipientDeviceId.Value);
        WriteGuid(output, envelope.SenderSigningKeyId.Value);
        WriteGuid(output, envelope.RecipientEncryptionKeyId.Value);
        WriteInt64(output, envelope.SentAt.ToUnixTimeMilliseconds());
        WriteInt64(output, envelope.ExpiresAt?.ToUnixTimeMilliseconds() ?? -1);
        return output.ToArray();
    }

    /// <summary>Encodes AEAD associated data, binding the header to the ephemeral public key.</summary>
    public static byte[] EncodeAssociatedData(EncryptedEnvelope envelope)
    {
        using var output = new MemoryStream(256);
        WriteBytes(output, AadDomain);
        WriteBytes(output, EncodeHeader(envelope));
        WriteBytes(output, envelope.EphemeralPublicKey.Span);
        return output.ToArray();
    }

    /// <summary>Encodes every envelope field except the signature itself.</summary>
    public static byte[] EncodeForSignature(EncryptedEnvelope envelope)
    {
        using var output = new MemoryStream(envelope.Ciphertext.Length + 384);
        WriteBytes(output, SignatureDomain);
        WriteBytes(output, EncodeHeader(envelope));
        WriteBytes(output, envelope.EphemeralPublicKey.Span);
        WriteBytes(output, envelope.Nonce.Span);
        WriteBytes(output, envelope.Ciphertext.Span);
        WriteBytes(output, envelope.AuthenticationTag.Span);
        return output.ToArray();
    }

    /// <summary>Encodes a complete envelope for stable storage, hashing and golden vectors.</summary>
    public static byte[] EncodeEnvelope(EncryptedEnvelope envelope)
    {
        ProtocolValidator.Validate(envelope);
        using var output = new MemoryStream(envelope.Ciphertext.Length + 512);
        WriteBytes(output, EnvelopeDomain);
        WriteBytes(output, EncodeForSignature(envelope));
        WriteBytes(output, envelope.Signature.Span);
        return output.ToArray();
    }

    private static void WriteGuid(Stream output, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != bytes.Length)
        {
            throw new InvalidOperationException("Could not encode a UUID.");
        }

        output.Write(bytes);
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteInt64(Stream output, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteBytes(Stream output, ReadOnlySpan<byte> bytes)
    {
        WriteInt32(output, bytes.Length);
        output.Write(bytes);
    }
}
