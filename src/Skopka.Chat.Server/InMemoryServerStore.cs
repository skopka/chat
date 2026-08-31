using System.Collections.Concurrent;
using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Thread-safe, non-persistent implementation for tests and the vertical-slice sample.</summary>
public sealed class InMemoryServerStore : IDeviceRepository, IConversationRepository, IEnvelopeRepository
{
    private readonly ConcurrentDictionary<DeviceId, PublicDevice> _devices = new();
    private readonly ConcurrentDictionary<ConversationId, PersonalConversation> _conversations = new();
    private readonly ConcurrentDictionary<MessageId, EnvelopeEntry> _envelopes = new();

    /// <summary>Returns a point-in-time copy of encrypted server records for diagnostics and tests.</summary>
    public IReadOnlyList<StoredEnvelope> SnapshotEnvelopes() =>
        _envelopes.Values.Select(entry => entry.Record).ToArray();

    /// <inheritdoc />
    ValueTask<bool> IDeviceRepository.TryAddAsync(PublicDevice device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_devices.TryAdd(device.DeviceId, device));
    }

    /// <inheritdoc />
    ValueTask<PublicDevice?> IDeviceRepository.GetAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_devices.GetValueOrDefault(deviceId));
    }

    /// <inheritdoc />
    ValueTask<bool> IDeviceRepository.RevokeAsync(DeviceId deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_devices.TryGetValue(deviceId, out var current))
        {
            if (current.IsRevoked)
            {
                return ValueTask.FromResult(false);
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(revokedAt, current.RegisteredAt);

            if (_devices.TryUpdate(deviceId, current.Revoke(revokedAt), current))
            {
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    ValueTask<bool> IConversationRepository.TryAddAsync(PersonalConversation conversation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_conversations.TryAdd(conversation.ConversationId, conversation));
    }

    /// <inheritdoc />
    ValueTask<PersonalConversation?> IConversationRepository.GetAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_conversations.GetValueOrDefault(conversationId));
    }

    /// <inheritdoc />
    ValueTask<EnvelopeStoreResult> IEnvelopeRepository.TryAddAsync(
        EncryptedEnvelope envelope,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = SHA256.HashData(CanonicalEnvelopeEncoding.EncodeEnvelope(envelope));
        var entry = new EnvelopeEntry(new StoredEnvelope(envelope, acceptedAt), hash);
        if (_envelopes.TryAdd(envelope.MessageId, entry))
        {
            return ValueTask.FromResult(EnvelopeStoreResult.Inserted);
        }

        var existing = _envelopes[envelope.MessageId];
        return ValueTask.FromResult(CryptographicOperations.FixedTimeEquals(existing.CanonicalHash, hash)
            ? EnvelopeStoreResult.Duplicate
            : EnvelopeStoreResult.Conflict);
    }

    /// <inheritdoc />
    ValueTask<IReadOnlyList<StoredEnvelope>> IEnvelopeRepository.GetPendingAsync(
        DeviceId recipientDeviceId,
        int maximumCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StoredEnvelope> result = _envelopes.Values
            .Select(entry => entry.Record)
            .Where(record =>
                record.Envelope.RecipientDeviceId == recipientDeviceId &&
                !record.AcknowledgedAt.HasValue &&
                (!record.Envelope.ExpiresAt.HasValue || record.Envelope.ExpiresAt > now))
            .OrderBy(record => record.AcceptedAt)
            .Take(maximumCount)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    ValueTask<bool> IEnvelopeRepository.AcknowledgeAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_envelopes.TryGetValue(messageId, out var current))
        {
            if (current.Record.Envelope.RecipientDeviceId != recipientDeviceId || current.Record.AcknowledgedAt.HasValue)
            {
                return ValueTask.FromResult(false);
            }

            var updated = current with { Record = current.Record with { AcknowledgedAt = acknowledgedAt } };
            if (_envelopes.TryUpdate(messageId, updated, current))
            {
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    ValueTask<int> IEnvelopeRepository.DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = 0;
        foreach (var item in _envelopes)
        {
            if (item.Value.Record.Envelope.ExpiresAt <= now && _envelopes.TryRemove(item.Key, out _))
            {
                deleted++;
            }
        }

        return ValueTask.FromResult(deleted);
    }

    private sealed record EnvelopeEntry(StoredEnvelope Record, byte[] CanonicalHash);
}
