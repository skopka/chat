using System.Security.Cryptography;
using NSec.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server.NSec;

/// <summary>Verifies binding-v1 Ed25519 proofs with NSec; has no private-key or decryption API.</summary>
public sealed class NSecDeviceProofVerifier : IDeviceProofVerifier
{
    /// <inheritdoc />
    public bool Verify(DeviceBindingChallenge challenge, DeviceBindingProof proof)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(proof);
        if (challenge.ChallengeId != proof.ChallengeId) { return false; }
        try
        {
            var key = PublicKey.Import(SignatureAlgorithm.Ed25519, challenge.Device.SigningPublicKey.Span, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(key, DeviceBindingEncoding.Encode(challenge), proof.Signature.Span);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            return false;
        }
    }
}
