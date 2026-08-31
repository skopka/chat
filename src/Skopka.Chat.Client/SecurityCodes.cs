using System.Security.Cryptography;
using System.Text;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Creates human-comparable fingerprints from canonical public device data.</summary>
public static class SecurityCodes
{
    private static readonly byte[] FingerprintDomain = Encoding.ASCII.GetBytes("skopka.chat.fingerprint.v1");
    private static readonly byte[] PairDomain = Encoding.ASCII.GetBytes("skopka.chat.security-code.v1");

    /// <summary>Computes a grouped SHA-256 fingerprint for one device.</summary>
    public static string Fingerprint(PublicDevice device)
    {
        ProtocolValidator.Validate(device);
        using var input = new MemoryStream(128);
        input.Write(FingerprintDomain);
        WriteGuid(input, device.DeviceId.Value);
        WriteGuid(input, device.KeyId.Value);
        input.Write(device.EncryptionPublicKey.Span);
        input.Write(device.SigningPublicKey.Span);
        return GroupHex(SHA256.HashData(input.ToArray()));
    }

    /// <summary>Computes an order-independent code that two users can compare out of band.</summary>
    public static string Between(PublicDevice first, PublicDevice second)
    {
        ProtocolValidator.Validate(first);
        ProtocolValidator.Validate(second);
        var firstFingerprint = Convert.FromHexString(Fingerprint(first).Replace("-", string.Empty, StringComparison.Ordinal));
        var secondFingerprint = Convert.FromHexString(Fingerprint(second).Replace("-", string.Empty, StringComparison.Ordinal));
        if (first.DeviceId.Value.CompareTo(second.DeviceId.Value) > 0)
        {
            (firstFingerprint, secondFingerprint) = (secondFingerprint, firstFingerprint);
        }

        using var input = new MemoryStream(96);
        input.Write(PairDomain);
        input.Write(firstFingerprint);
        input.Write(secondFingerprint);
        return GroupHex(SHA256.HashData(input.ToArray()));
    }

    private static void WriteGuid(Stream output, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        output.Write(bytes);
    }

    private static string GroupHex(ReadOnlySpan<byte> hash)
    {
        var hex = Convert.ToHexString(hash);
        return string.Join('-', Enumerable.Range(0, hex.Length / 8).Select(index => hex.Substring(index * 8, 8)));
    }
}
