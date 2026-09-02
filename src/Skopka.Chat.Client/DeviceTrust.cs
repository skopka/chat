using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Host-visible trust state for one observed public device identity.</summary>
public enum ChatDeviceTrustState
{
    /// <summary>No user-confirmed identity is stored.</summary>
    Unknown = 0,

    /// <summary>The user confirmed this exact key ID and public-key pair.</summary>
    Verified = 1,

    /// <summary>The directory returned a different key ID or public-key pair.</summary>
    Changed = 2,

    /// <summary>The device was explicitly revoked.</summary>
    Revoked = 3,
}

/// <summary>Small host-independent trust record containing public identity data only.</summary>
public sealed class ChatDeviceTrustRecord
{
    private readonly byte[] _encryptionPublicKey;
    private readonly byte[] _signingPublicKey;

    /// <summary>Creates a trust record that can be persisted by a host-selected adapter.</summary>
    public ChatDeviceTrustRecord(
        UserId userId,
        DeviceId deviceId,
        KeyId keyId,
        ReadOnlySpan<byte> encryptionPublicKey,
        ReadOnlySpan<byte> signingPublicKey,
        ChatDeviceTrustState state,
        DateTimeOffset recordedAt)
    {
        var device = new PublicDevice(
            userId,
            deviceId,
            keyId,
            encryptionPublicKey,
            signingPublicKey,
            recordedAt,
            state == ChatDeviceTrustState.Revoked ? recordedAt : null);
        ProtocolValidator.Validate(device);
        if (state is < ChatDeviceTrustState.Unknown or > ChatDeviceTrustState.Revoked)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        UserId = userId;
        DeviceId = deviceId;
        KeyId = keyId;
        _encryptionPublicKey = encryptionPublicKey.ToArray();
        _signingPublicKey = signingPublicKey.ToArray();
        State = state;
        RecordedAt = recordedAt;
    }

    /// <summary>Owner of the observed device.</summary>
    public UserId UserId { get; }

    /// <summary>Observed device identifier.</summary>
    public DeviceId DeviceId { get; }

    /// <summary>Observed key version.</summary>
    public KeyId KeyId { get; }

    /// <summary>Observed X25519 public key.</summary>
    public ReadOnlyMemory<byte> EncryptionPublicKey => _encryptionPublicKey;

    /// <summary>Observed Ed25519 public key.</summary>
    public ReadOnlyMemory<byte> SigningPublicKey => _signingPublicKey;

    /// <summary>User-confirmed or derived trust state.</summary>
    public ChatDeviceTrustState State { get; }

    /// <summary>Trusted host timestamp for the record.</summary>
    public DateTimeOffset RecordedAt { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatDeviceTrustRecord(DeviceId={DeviceId}, KeyId={KeyId}, State={State}, PublicKeys=[REDACTED])";
}

/// <summary>Host-selected persistence for small device trust records.</summary>
public interface IChatDeviceTrustStore
{
    /// <summary>Loads the last record for one user/device pair, or null when unknown.</summary>
    ValueTask<ChatDeviceTrustRecord?> LoadAsync(
        UserId userId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>Saves a host-approved trust record.</summary>
    ValueTask SaveAsync(
        ChatDeviceTrustRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one local trust decision.</summary>
    ValueTask DeleteAsync(
        UserId userId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Evaluates observed device data without automatically trusting key changes.</summary>
public static class ChatDeviceTrust
{
    /// <summary>Returns the effective state for current directory data and a stored decision.</summary>
    public static ChatDeviceTrustState Evaluate(PublicDevice current, ChatDeviceTrustRecord? stored)
    {
        ProtocolValidator.Validate(current);
        if (current.IsRevoked)
        {
            return ChatDeviceTrustState.Revoked;
        }

        if (stored is null || stored.UserId != current.UserId || stored.DeviceId != current.DeviceId)
        {
            return ChatDeviceTrustState.Unknown;
        }

        if (stored.State == ChatDeviceTrustState.Revoked)
        {
            return ChatDeviceTrustState.Revoked;
        }

        if (stored.KeyId != current.KeyId ||
            !stored.EncryptionPublicKey.Span.SequenceEqual(current.EncryptionPublicKey.Span) ||
            !stored.SigningPublicKey.Span.SequenceEqual(current.SigningPublicKey.Span))
        {
            return ChatDeviceTrustState.Changed;
        }

        return stored.State == ChatDeviceTrustState.Verified
            ? ChatDeviceTrustState.Verified
            : ChatDeviceTrustState.Unknown;
    }
}
