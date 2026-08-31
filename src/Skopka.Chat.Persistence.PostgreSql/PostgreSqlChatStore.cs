using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Persistence.PostgreSql;

/// <summary>EF Core/PostgreSQL implementation of every transport-neutral server repository.</summary>
public sealed class PostgreSqlChatStore : IDeviceRepository, IConversationRepository, IEnvelopeRepository
{
    private readonly ChatDbContext _context;

    /// <summary>Creates a scoped repository set over one EF context.</summary>
    public PostgreSqlChatStore(ChatDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    async ValueTask<bool> IDeviceRepository.TryAddAsync(PublicDevice device, CancellationToken cancellationToken)
    {
        _context.Devices.Add(DeviceEntity.FromDomain(device));
        return await TrySaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    async ValueTask<PublicDevice?> IDeviceRepository.GetAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        var entity = await _context.Devices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DeviceId == deviceId.Value, cancellationToken)
            .ConfigureAwait(false);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    async ValueTask<bool> IDeviceRepository.RevokeAsync(
        DeviceId deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var affected = await _context.Devices
            .Where(item => item.DeviceId == deviceId.Value && item.RevokedAt == null && item.RegisteredAt <= revokedAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RevokedAt, revokedAt), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    /// <inheritdoc />
    async ValueTask<bool> IConversationRepository.TryAddAsync(
        PersonalConversation conversation,
        CancellationToken cancellationToken)
    {
        _context.Conversations.Add(ConversationEntity.FromDomain(conversation));
        return await TrySaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    async ValueTask<PersonalConversation?> IConversationRepository.GetAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        var entity = await _context.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ConversationId == conversationId.Value, cancellationToken)
            .ConfigureAwait(false);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    async ValueTask<EnvelopeStoreResult> IEnvelopeRepository.TryAddAsync(
        EncryptedEnvelope envelope,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        var canonicalHash = SHA256.HashData(CanonicalEnvelopeEncoding.EncodeEnvelope(envelope));
        _context.Envelopes.Add(EnvelopeEntity.FromDomain(envelope, acceptedAt, canonicalHash));
        if (await TrySaveAsync(cancellationToken).ConfigureAwait(false))
        {
            return EnvelopeStoreResult.Inserted;
        }

        var existingHash = await _context.Envelopes.AsNoTracking()
            .Where(item => item.MessageId == envelope.MessageId.Value)
            .Select(item => item.CanonicalHash)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return existingHash is not null && CryptographicOperations.FixedTimeEquals(existingHash, canonicalHash)
            ? EnvelopeStoreResult.Duplicate
            : EnvelopeStoreResult.Conflict;
    }

    /// <inheritdoc />
    async ValueTask<IReadOnlyList<StoredEnvelope>> IEnvelopeRepository.GetPendingAsync(
        DeviceId recipientDeviceId,
        int maximumCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entities = await _context.Envelopes.AsNoTracking()
            .Where(item =>
                item.RecipientDeviceId == recipientDeviceId.Value &&
                item.AcknowledgedAt == null &&
                (item.ExpiresAt == null || item.ExpiresAt > now))
            .OrderBy(item => item.AcceptedAt)
            .Take(maximumCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.Select(item => item.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    async ValueTask<bool> IEnvelopeRepository.AcknowledgeAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        var affected = await _context.Envelopes
            .Where(item =>
                item.MessageId == messageId.Value &&
                item.RecipientDeviceId == recipientDeviceId.Value &&
                item.AcknowledgedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.AcknowledgedAt, acknowledgedAt), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    /// <inheritdoc />
    async ValueTask<int> IEnvelopeRepository.DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await _context.Envelopes.Where(item => item.ExpiresAt != null && item.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _context.ChangeTracker.Clear();
            return false;
        }
    }
}
