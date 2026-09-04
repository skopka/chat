using System.Collections.Concurrent;
using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Thread-safe, non-persistent implementation for tests and the vertical-slice sample.</summary>
public sealed partial class InMemoryServerStore : IDeviceRepository, IConversationRepository, IGroupConversationRepository, IEnvelopeRepository
{
    private readonly ConcurrentDictionary<DeviceId, PublicDevice> _devices = new();
    private readonly ConcurrentDictionary<ConversationId, PersonalConversation> _conversations = new();
    private readonly ConcurrentDictionary<(UserId First, UserId Second), ConversationId> _conversationPairs = new();
    private readonly ConcurrentDictionary<ConversationId, GroupConversation> _groupConversations = new();
    private readonly ConcurrentDictionary<MessageId, EnvelopeEntry> _envelopes = new();
    private readonly object _conversationGate = new();

    /// <summary>Returns a point-in-time copy of encrypted server records for diagnostics and tests.</summary>
    public IReadOnlyList<StoredEnvelope> SnapshotEnvelopes() =>
        _envelopes.Values.Select(entry => entry.Record).ToArray();

    /// <inheritdoc />
    ValueTask<bool> IDeviceRepository.TryAddAsync(PublicDevice device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindingGate)
        {
            return ValueTask.FromResult(_devices.TryAdd(device.DeviceId, device));
        }
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
        lock (_bindingGate)
        {
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
    }

    /// <inheritdoc />
    ValueTask<DeviceDirectoryPage> IDeviceRepository.ListActiveForParticipantsAsync(
        UserId firstUserId,
        UserId secondUserId,
        DeviceDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = _devices.Values
            .Where(item =>
                !item.IsRevoked &&
                (item.UserId == firstUserId || item.UserId == secondUserId) &&
                (!cursor.HasValue || CompareDevice(item, cursor.Value) > 0))
            .OrderBy(item => item.UserId.Value)
            .ThenBy(item => item.DeviceId.Value)
            .Take(maximumCount + 1)
            .ToArray();
        var hasMore = ordered.Length > maximumCount;
        var items = ordered.Take(maximumCount).ToArray();
        DeviceDirectoryCursor? next = hasMore && items.Length > 0
            ? new DeviceDirectoryCursor(items[^1].UserId, items[^1].DeviceId)
            : null;
        return ValueTask.FromResult(new DeviceDirectoryPage(items, next));
    }

    /// <inheritdoc />
    ValueTask<DeviceDirectoryPage> IDeviceRepository.ListActiveForUsersAsync(
        IReadOnlyCollection<UserId> userIds,
        DeviceDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var users = userIds.ToHashSet();
        var ordered = _devices.Values
            .Where(item =>
                !item.IsRevoked &&
                users.Contains(item.UserId) &&
                (!cursor.HasValue || CompareDevice(item, cursor.Value) > 0))
            .OrderBy(item => item.UserId.Value)
            .ThenBy(item => item.DeviceId.Value)
            .Take(maximumCount + 1)
            .ToArray();
        var hasMore = ordered.Length > maximumCount;
        var items = ordered.Take(maximumCount).ToArray();
        DeviceDirectoryCursor? next = hasMore && items.Length > 0
            ? new DeviceDirectoryCursor(items[^1].UserId, items[^1].DeviceId)
            : null;
        return ValueTask.FromResult(new DeviceDirectoryPage(items, next));
    }

    /// <inheritdoc />
    ValueTask<bool> IConversationRepository.TryAddAsync(PersonalConversation conversation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = PersonalConversation.CreateCanonical(
            conversation.ConversationId,
            conversation.FirstUserId,
            conversation.SecondUserId,
            conversation.CreatedAt);
        var pair = (canonical.FirstUserId, canonical.SecondUserId);
        lock (_conversationGate)
        {
            if (_conversationPairs.ContainsKey(pair) || _conversations.ContainsKey(canonical.ConversationId) ||
                _groupConversations.ContainsKey(canonical.ConversationId))
            {
                return ValueTask.FromResult(false);
            }

            if (!_conversationPairs.TryAdd(pair, canonical.ConversationId) ||
                !_conversations.TryAdd(canonical.ConversationId, canonical))
            {
                throw new InvalidOperationException("The in-memory conversation indexes are inconsistent.");
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    ValueTask<PersonalConversation?> IConversationRepository.GetAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_conversations.GetValueOrDefault(conversationId));
    }

    /// <inheritdoc />
    ValueTask<PersonalConversation?> IConversationRepository.GetByParticipantsAsync(
        UserId firstUserId,
        UserId secondUserId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = PersonalConversation.CreateCanonical(
            default,
            firstUserId,
            secondUserId,
            DateTimeOffset.UnixEpoch);
        lock (_conversationGate)
        {
            return ValueTask.FromResult(
                _conversationPairs.TryGetValue((canonical.FirstUserId, canonical.SecondUserId), out var id)
                    ? _conversations.GetValueOrDefault(id)
                    : null);
        }
    }

    /// <inheritdoc />
    ValueTask<ConversationDirectoryPage> IConversationRepository.ListForUserAsync(
        UserId userId,
        ConversationDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = _conversations.Values
            .Where(item => item.Contains(userId) && (!cursor.HasValue || CompareConversation(item, cursor.Value) < 0))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.ConversationId.Value)
            .Take(maximumCount + 1)
            .ToArray();
        var hasMore = ordered.Length > maximumCount;
        var items = ordered.Take(maximumCount).ToArray();
        ConversationDirectoryCursor? next = hasMore && items.Length > 0
            ? new ConversationDirectoryCursor(items[^1].CreatedAt, items[^1].ConversationId)
            : null;
        return ValueTask.FromResult(new ConversationDirectoryPage(items, next));
    }

    /// <inheritdoc />
    ValueTask<bool> IGroupConversationRepository.TryAddAsync(
        GroupConversation conversation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_conversationGate)
        {
            if (_conversations.ContainsKey(conversation.ConversationId) ||
                _groupConversations.ContainsKey(conversation.ConversationId))
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(_groupConversations.TryAdd(conversation.ConversationId, conversation));
        }
    }

    /// <inheritdoc />
    ValueTask<GroupConversation?> IGroupConversationRepository.GetAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_groupConversations.GetValueOrDefault(conversationId));
    }

    /// <inheritdoc />
    ValueTask<GroupConversationDirectoryPage> IGroupConversationRepository.ListForUserAsync(
        UserId userId,
        ConversationDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = _groupConversations.Values
            .Where(item => item.Contains(userId) && (!cursor.HasValue || CompareConversation(item, cursor.Value) < 0))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.ConversationId.Value)
            .Take(maximumCount + 1)
            .ToArray();
        var hasMore = ordered.Length > maximumCount;
        var items = ordered.Take(maximumCount).ToArray();
        ConversationDirectoryCursor? next = hasMore && items.Length > 0
            ? new ConversationDirectoryCursor(items[^1].CreatedAt, items[^1].ConversationId)
            : null;
        return ValueTask.FromResult(new GroupConversationDirectoryPage(items, next));
    }

    /// <inheritdoc />
    ValueTask<GroupConversationStoreResult> IGroupConversationRepository.TryReplaceAsync(
        GroupConversation conversation,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_conversationGate)
        {
            if (!_groupConversations.TryGetValue(conversation.ConversationId, out var current) ||
                current.Revision != expectedRevision ||
                conversation.Revision != expectedRevision + 1)
            {
                return ValueTask.FromResult(GroupConversationStoreResult.Conflict);
            }

            _groupConversations[conversation.ConversationId] = conversation;
            return ValueTask.FromResult(GroupConversationStoreResult.Updated);
        }
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
            .ThenBy(record => record.Envelope.MessageId.Value)
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

    private static int CompareDevice(PublicDevice device, DeviceDirectoryCursor cursor)
    {
        var userComparison = device.UserId.Value.CompareTo(cursor.UserId.Value);
        return userComparison != 0 ? userComparison : device.DeviceId.Value.CompareTo(cursor.DeviceId.Value);
    }

    private static int CompareConversation(PersonalConversation conversation, ConversationDirectoryCursor cursor)
    {
        var createdComparison = conversation.CreatedAt.CompareTo(cursor.CreatedAt);
        return createdComparison != 0
            ? createdComparison
            : conversation.ConversationId.Value.CompareTo(cursor.ConversationId.Value);
    }

    private static int CompareConversation(GroupConversation conversation, ConversationDirectoryCursor cursor)
    {
        var createdComparison = conversation.CreatedAt.CompareTo(cursor.CreatedAt);
        return createdComparison != 0
            ? createdComparison
            : conversation.ConversationId.Value.CompareTo(cursor.ConversationId.Value);
    }
}
