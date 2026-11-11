using Microsoft.EntityFrameworkCore;
using Skopka.Chat.Server;

// EF1002: only the schema identifier validated by ServerTrustedChatSchema.IsValid ([a-z_][a-z0-9_]{0,47}) is
// interpolated into SQL text; every value is passed as a database parameter.
#pragma warning disable EF1002

namespace Skopka.Chat.ServerTrusted.PostgreSql;

/// <summary>
/// Lease-based outbox over the server-trusted schema with ADR 0022 semantics. It implements the existing
/// <see cref="IChatServerEventOutbox"/> so the unchanged <see cref="ChatServerEventDispatcher"/> publishes its rows.
/// </summary>
public sealed class PostgreSqlServerTrustedChatEventOutbox : IChatServerEventOutbox
{
    private readonly IDbContextFactory<ServerTrustedChatDbContext> _factory;

    /// <summary>Creates the outbox over a context factory.</summary>
    public PostgreSqlServerTrustedChatEventOutbox(IDbContextFactory<ServerTrustedChatDbContext> factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClaimedChatServerEvent>> ClaimPendingAsync(string leaseOwner, int maximumCount, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > ChatServerEventTypes.MaxIdentifierLength ||
            maximumCount is < 1 or > 500 || now == default || leaseDuration < TimeSpan.FromSeconds(5) || leaseDuration > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentException("The event claim request is invalid.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Outbox.FromSqlRaw(
                $"""
                SELECT * FROM "{db.Schema}".event_outbox
                WHERE published_at IS NULL AND next_attempt_at <= {"{0}"} AND (lease_expires_at IS NULL OR lease_expires_at <= {"{0}"})
                ORDER BY occurred_at, event_id
                LIMIT {"{1}"}
                FOR UPDATE SKIP LOCKED
                """,
                now, maximumCount)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.LeaseOwner = leaseOwner;
            row.LeaseExpiresAt = now + leaseDuration;
            row.AttemptCount = checked(row.AttemptCount + 1);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(static row => new ClaimedChatServerEvent(
            new ChatServerOutboxMessage(row.EventId, row.EventType, row.EventVersion, row.OccurredAt, row.PartitionKey, row.Payload),
            row.AttemptCount)).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkPublishedAsync(Guid eventId, string leaseOwner, DateTimeOffset publishedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Outbox
            .Where(item => item.EventId == eventId && item.PublishedAt == null && item.LeaseOwner == leaseOwner && item.LeaseExpiresAt > publishedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PublishedAt, publishedAt)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RescheduleAsync(Guid eventId, string leaseOwner, DateTimeOffset failedAt, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default)
    {
        if (nextAttemptAt <= failedAt)
        {
            throw new ArgumentException("The next event attempt must be in the future.", nameof(nextAttemptAt));
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Outbox
            .Where(item => item.EventId == eventId && item.PublishedAt == null && item.LeaseOwner == leaseOwner && item.LeaseExpiresAt > failedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LastFailedAt, failedAt)
                .SetProperty(item => item.NextAttemptAt, nextAttemptAt)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeletePublishedBeforeAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (cutoff == default || maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentException("The completed-event cleanup request is invalid.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Database.ExecuteSqlRawAsync(
            $"""
            WITH completed AS (
                SELECT event_id FROM "{db.Schema}".event_outbox
                WHERE published_at IS NOT NULL AND published_at < {"{0}"}
                ORDER BY published_at, event_id
                LIMIT {"{1}"})
            DELETE FROM "{db.Schema}".event_outbox AS outbox USING completed WHERE outbox.event_id = completed.event_id
            """,
            [cutoff, maximumCount], cancellationToken).ConfigureAwait(false);
    }
}
