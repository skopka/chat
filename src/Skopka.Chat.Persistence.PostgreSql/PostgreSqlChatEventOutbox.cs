using Microsoft.EntityFrameworkCore;
using Skopka.Chat.Server;

namespace Skopka.Chat.Persistence.PostgreSql;

/// <summary>PostgreSQL lease-based outbox for committed server integration events.</summary>
public sealed class PostgreSqlChatEventOutbox : IChatServerEventOutbox
{
    private readonly ChatDbContext _context;

    /// <summary>Creates a scoped outbox over the same database schema as encrypted envelopes.</summary>
    public PostgreSqlChatEventOutbox(ChatDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClaimedChatServerEvent>> ClaimPendingAsync(
        string leaseOwner,
        int maximumCount,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(leaseOwner, maximumCount, now, leaseDuration);
        var leaseExpiresAt = now + leaseDuration;
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entities = await _context.ServerEventOutbox
            .FromSqlInterpolated($"""
                SELECT *
                FROM chat_server_event_outbox
                WHERE published_at IS NULL
                  AND next_attempt_at <= {now}
                  AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
                ORDER BY occurred_at, event_id
                LIMIT {maximumCount}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var entity in entities)
        {
            entity.LeaseOwner = leaseOwner;
            entity.LeaseExpiresAt = leaseExpiresAt;
            entity.AttemptCount = checked(entity.AttemptCount + 1);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var result = entities
            .Select(static entity => new ClaimedChatServerEvent(entity.ToDomain(), entity.AttemptCount))
            .ToArray();
        _context.ChangeTracker.Clear();
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkPublishedAsync(
        Guid eventId,
        string leaseOwner,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateCompletion(eventId, leaseOwner, publishedAt);
        var affected = await _context.ServerEventOutbox
            .Where(item =>
                item.EventId == eventId &&
                item.PublishedAt == null &&
                item.LeaseOwner == leaseOwner &&
                item.LeaseExpiresAt > publishedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PublishedAt, publishedAt)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RescheduleAsync(
        Guid eventId,
        string leaseOwner,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ValidateCompletion(eventId, leaseOwner, failedAt);
        if (nextAttemptAt <= failedAt)
        {
            throw new ArgumentException("The next event attempt must be in the future.", nameof(nextAttemptAt));
        }

        var affected = await _context.ServerEventOutbox
            .Where(item =>
                item.EventId == eventId &&
                item.PublishedAt == null &&
                item.LeaseOwner == leaseOwner &&
                item.LeaseExpiresAt > failedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LastFailedAt, failedAt)
                .SetProperty(item => item.NextAttemptAt, nextAttemptAt)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeletePublishedBeforeAsync(
        DateTimeOffset cutoff,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (cutoff == default || maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentException("The completed-event cleanup request is invalid.");
        }

        return await _context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH completed AS (
                SELECT event_id
                FROM chat_server_event_outbox
                WHERE published_at IS NOT NULL AND published_at < {cutoff}
                ORDER BY published_at, event_id
                LIMIT {maximumCount}
            )
            DELETE FROM chat_server_event_outbox AS outbox
            USING completed
            WHERE outbox.event_id = completed.event_id
            """, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLease(
        string leaseOwner,
        int maximumCount,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > ChatServerEventTypes.MaxIdentifierLength ||
            maximumCount is < 1 or > 500 || now == default ||
            leaseDuration < TimeSpan.FromSeconds(5) || leaseDuration > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentException("The event claim request is invalid.");
        }
    }

    private static void ValidateCompletion(Guid eventId, string leaseOwner, DateTimeOffset timestamp)
    {
        if (eventId == Guid.Empty || string.IsNullOrWhiteSpace(leaseOwner) ||
            leaseOwner.Length > ChatServerEventTypes.MaxIdentifierLength || timestamp == default)
        {
            throw new ArgumentException("The event completion request is invalid.");
        }
    }
}
