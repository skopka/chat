using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// EF1002: only the schema identifier validated by ServerTrustedChatSchema.IsValid ([a-z_][a-z0-9_]{0,47}) is
// interpolated into SQL text; every value is passed as a database parameter.
#pragma warning disable EF1002

namespace Skopka.Chat.ServerTrusted.PostgreSql;

/// <summary>
/// PostgreSQL store. Every conversation-scoped write locks the conversation row (<c>FOR UPDATE</c>) first, so sequence
/// and revision allocation, closure, membership and moderation changes serialize per conversation; outbox rows and
/// <c>pg_notify</c> signals are written in the same transaction and become visible only on commit.
/// </summary>
public sealed class PostgreSqlServerTrustedChatStore : IServerTrustedChatStore
{
    private readonly IDbContextFactory<ServerTrustedChatDbContext> _factory;
    private readonly TimeProvider _time;

    /// <summary>Creates a store over a context factory (works outside request scopes, for example in Blazor circuits).</summary>
    public PostgreSqlServerTrustedChatStore(IDbContextFactory<ServerTrustedChatDbContext> factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<(TrustedConversation Conversation, bool Created)> GetOrCreateConversationAsync(TrustedConversationCreate request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlRawAsync(
            $$"""
            INSERT INTO {{Table(db, "conversations")}} (id, host_key, kind, status, created_at, closed_at, last_activity_at, last_sequence, change_revision)
            VALUES ({0}, {1}, {2}, 1, {3}, NULL, {3}, 0, 0)
            ON CONFLICT (host_key) DO NOTHING
            """,
            [request.Id.Value, request.HostKey, (short)request.Kind, request.CreatedAt],
            cancellationToken).ConfigureAwait(false);
        if (inserted == 1)
        {
            foreach (var spec in request.Participants)
            {
                db.Participants.Add(new ParticipantRow
                {
                    ConversationId = request.Id.Value,
                    UserId = spec.UserId,
                    Role = (short)spec.Role,
                    CanRead = spec.CanRead,
                    Label = spec.Label,
                    JoinedAt = request.CreatedAt,
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var conversation = await db.Conversations.AsNoTracking().SingleAsync(item => item.HostKey == request.HostKey, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (conversation.ToDomain(), inserted == 1);
    }

    /// <inheritdoc />
    public async ValueTask<TrustedConversation?> GetConversationAsync(TrustedConversationId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id.Value, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async ValueTask<TrustedConversation?> FindConversationAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(item => item.HostKey == hostKey, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async ValueTask<TrustedStoreOutcome> SetStatusAsync(TrustedConversationId id, TrustedConversationStatus status, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await LockConversationAsync(db, id.Value, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return TrustedStoreOutcome.NotFoundOrForbidden;
        }

        if (conversation.Status == (short)status)
        {
            return TrustedStoreOutcome.Unchanged;
        }

        conversation.Status = (short)status;
        conversation.ClosedAt = status == TrustedConversationStatus.Closed ? at : null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(db, $"C|{id}|{conversation.ChangeRevision}", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TrustedStoreOutcome.Applied;
    }

    /// <inheritdoc />
    public async ValueTask<TrustedStoreOutcome> UpsertParticipantAsync(TrustedConversationId id, TrustedParticipantSpec participant, DateTimeOffset at, int maxParticipants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await LockConversationAsync(db, id.Value, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return TrustedStoreOutcome.NotFoundOrForbidden;
        }

        var existing = await db.Participants.SingleOrDefaultAsync(item => item.ConversationId == id.Value && item.UserId == participant.UserId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Role == (short)participant.Role && existing.CanRead == participant.CanRead && existing.Label == participant.Label)
            {
                return TrustedStoreOutcome.Unchanged;
            }

            existing.Role = (short)participant.Role;
            existing.CanRead = participant.CanRead;
            existing.Label = participant.Label;
        }
        else
        {
            if (await db.Participants.CountAsync(item => item.ConversationId == id.Value, cancellationToken).ConfigureAwait(false) >= maxParticipants)
            {
                return TrustedStoreOutcome.LimitReached;
            }

            db.Participants.Add(new ParticipantRow
            {
                ConversationId = id.Value,
                UserId = participant.UserId,
                Role = (short)participant.Role,
                CanRead = participant.CanRead,
                Label = participant.Label,
                JoinedAt = at,
                HistoryVisibleFrom = participant.SeesHistory ? 0 : conversation.LastSequence + 1,
                ReadThrough = participant.SeesHistory ? 0 : conversation.LastSequence,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(db, $"C|{id}|{conversation.ChangeRevision}", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TrustedStoreOutcome.Applied;
    }

    /// <inheritdoc />
    public async ValueTask<TrustedStoreOutcome> RemoveParticipantAsync(TrustedConversationId id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await LockConversationAsync(db, id.Value, cancellationToken).ConfigureAwait(false);
        if (conversation is null ||
            await db.Participants.Where(item => item.ConversationId == id.Value && item.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            return TrustedStoreOutcome.NotFoundOrForbidden;
        }

        await NotifyAsync(db, $"C|{id}|{conversation.ChangeRevision}", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TrustedStoreOutcome.Applied;
    }

    /// <inheritdoc />
    public async ValueTask<TrustedParticipant?> GetParticipantAsync(TrustedConversationId id, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Participants.AsNoTracking().SingleOrDefaultAsync(item => item.ConversationId == id.Value && item.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TrustedParticipant>> ListParticipantsAsync(TrustedConversationId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Participants.AsNoTracking().Where(item => item.ConversationId == id.Value)
            .OrderBy(item => item.JoinedAt).ThenBy(item => item.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(static item => item.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TrustedConversationSummary>> ListConversationsAsync(Guid userId, int limit, int unreadCap, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await (
            from participant in db.Participants.AsNoTracking()
            join conversation in db.Conversations.AsNoTracking() on participant.ConversationId equals conversation.Id
            where participant.UserId == userId && participant.CanRead
            orderby conversation.LastActivityAt descending, conversation.Id
            select new { conversation, participant }).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TrustedConversationSummary>(rows.Count);
        foreach (var row in rows)
        {
            var unread = await CountUnreadAsync(db, row.participant, unreadCap, cancellationToken).ConfigureAwait(false);
            result.Add(new TrustedConversationSummary(row.conversation.ToDomain(), row.participant.ToDomain(), unread));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<int> GetUnreadTotalAsync(Guid userId, int unreadCap, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var total = await db.Database.SqlQueryRaw<long>(
            $$"""
            SELECT COALESCE(SUM(capped.unread), 0)::bigint AS "Value"
            FROM {{Table(db, "participants")}} AS p
            CROSS JOIN LATERAL (
                SELECT count(*) AS unread FROM (
                    SELECT 1 FROM {{Table(db, "messages")}} AS m
                    WHERE m.conversation_id = p.conversation_id
                      AND m.sequence > p.read_through
                      AND m.sequence >= p.history_visible_from
                      AND m.author_user_id <> p.user_id
                      AND m.deleted_at IS NULL AND m.hidden_at IS NULL AND NOT m.is_erased
                    LIMIT {1}) AS limited) AS capped
            WHERE p.user_id = {0} AND p.can_read
            """,
            userId, unreadCap).SingleAsync(cancellationToken).ConfigureAwait(false);
        return (int)Math.Min(int.MaxValue, total);
    }

    /// <inheritdoc />
    public async ValueTask<int> GetUnreadCountAsync(TrustedConversationId id, Guid userId, int unreadCap, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var participant = await db.Participants.AsNoTracking().SingleOrDefaultAsync(item => item.ConversationId == id.Value && item.UserId == userId, cancellationToken).ConfigureAwait(false);
        return participant is { CanRead: true } ? await CountUnreadAsync(db, participant, unreadCap, cancellationToken).ConfigureAwait(false) : 0;
    }

    /// <inheritdoc />
    public async ValueTask<TrustedSendResult> SendAsync(TrustedSendRecord request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await LockConversationAsync(db, request.ConversationId.Value, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return new TrustedSendResult(TrustedStoreOutcome.NotFoundOrForbidden, null);
        }

        var fingerprint = Fingerprint(request);
        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ConversationId == request.ConversationId.Value && item.AuthorUserId == request.AuthorUserId && item.ClientMessageId == request.ClientMessageId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return CryptographicOperations.FixedTimeEquals(existing.Fingerprint, fingerprint)
                ? new TrustedSendResult(TrustedStoreOutcome.Duplicate, (await ViewAsync(db, [existing], cancellationToken).ConfigureAwait(false))[0])
                : new TrustedSendResult(TrustedStoreOutcome.Conflict, null);
        }

        var author = await db.Participants.AsNoTracking().SingleOrDefaultAsync(item => item.ConversationId == conversation.Id && item.UserId == request.AuthorUserId, cancellationToken).ConfigureAwait(false);
        if (author is not { Role: (short)TrustedParticipantRole.Writer, CanRead: true })
        {
            return new TrustedSendResult(TrustedStoreOutcome.NotFoundOrForbidden, null);
        }

        if (conversation.Status != (short)TrustedConversationStatus.Open)
        {
            return new TrustedSendResult(TrustedStoreOutcome.Closed, null);
        }

        if (request.ReplyToMessageId is { } replyId)
        {
            var reply = await db.Messages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == replyId.Value, cancellationToken).ConfigureAwait(false);
            if (reply is null || reply.ConversationId != conversation.Id || reply.Sequence < author.HistoryVisibleFrom || reply.Tombstone != TrustedMessageTombstone.None)
            {
                return new TrustedSendResult(TrustedStoreOutcome.Conflict, null);
            }
        }

        var attachmentIds = request.AttachmentIds.Select(static item => item.Value).ToArray();
        var attachments = attachmentIds.Length == 0
            ? []
            : await db.Attachments.FromSqlRaw($"SELECT * FROM {Table(db, "attachments")} WHERE id = ANY({{0}}) FOR UPDATE", attachmentIds)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (attachments.Count != attachmentIds.Length || attachments.Any(item =>
                !item.IsReady || item.MessageId is not null || item.ConversationId != conversation.Id || item.UploaderUserId != request.AuthorUserId))
        {
            return new TrustedSendResult(TrustedStoreOutcome.Conflict, null);
        }

        conversation.LastSequence++;
        conversation.ChangeRevision++;
        conversation.LastActivityAt = request.CreatedAt;
        var message = new MessageRow
        {
            Id = request.MessageId.Value,
            ConversationId = conversation.Id,
            Sequence = conversation.LastSequence,
            ChangeRevision = conversation.ChangeRevision,
            AuthorUserId = request.AuthorUserId,
            AuthorLabel = author.Label,
            ClientMessageId = request.ClientMessageId,
            Fingerprint = fingerprint,
            Text = request.Text,
            ReplyToMessageId = request.ReplyToMessageId?.Value,
            ReferencesJson = SerializeReferences(request.References),
            CreatedAt = request.CreatedAt,
        };
        db.Messages.Add(message);
        foreach (var attachment in attachments)
        {
            attachment.MessageId = message.Id;
            attachment.Position = Array.IndexOf(attachmentIds, attachment.Id);
        }

        var view = ToView(message, attachments.OrderBy(static item => item.Position).Select(static item => item.ToInfo()).ToArray());
        db.Outbox.Add(OutboxRow.From(ServerTrustedChatEventFactory.MessageCreated(request.EventId, view)));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(db, $"M|{conversation.Id:D}|{conversation.ChangeRevision}", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TrustedSendResult(TrustedStoreOutcome.Applied, view);
    }

    /// <inheritdoc />
    public async ValueTask<TrustedMutationResult> EditAsync(TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, string text, DateTimeOffset at, bool retainPriorVersion, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (outcome, conversation, message) = await LockOwnMessageAsync(db, id, messageId, authorUserId, cancellationToken).ConfigureAwait(false);
        if (outcome is not null)
        {
            return new TrustedMutationResult(outcome.Value, null);
        }

        if (message!.Tombstone != TrustedMessageTombstone.None)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.Conflict, null);
        }

        if (message.Text == text)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.Unchanged, (await ViewAsync(db, [message], cancellationToken).ConfigureAwait(false))[0]);
        }

        if (retainPriorVersion && message.Text is not null)
        {
            var version = await db.MessageVersions.Where(item => item.MessageId == message.Id).Select(static item => (int?)item.Version).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            db.MessageVersions.Add(new MessageVersionRow { MessageId = message.Id, Version = version + 1, Text = message.Text });
        }

        message.Text = text;
        message.EditedAt = at;
        message.ChangeRevision = ++conversation!.ChangeRevision;
        return await CommitMutationAsync(db, transaction, conversation, message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TrustedMutationResult> DeleteOwnAsync(TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, DateTimeOffset at, bool retainContent, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (outcome, conversation, message) = await LockOwnMessageAsync(db, id, messageId, authorUserId, cancellationToken).ConfigureAwait(false);
        if (outcome is not null)
        {
            return new TrustedMutationResult(outcome.Value, null);
        }

        if (message!.DeletedAt is not null)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.Duplicate, (await ViewAsync(db, [message], cancellationToken).ConfigureAwait(false))[0]);
        }

        if (message.Tombstone != TrustedMessageTombstone.None)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.Conflict, null);
        }

        message.DeletedAt = at;
        message.RetainedText = retainContent ? message.Text : null;
        message.Text = null;
        if (!retainContent)
        {
            await db.MessageVersions.Where(item => item.MessageId == message.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await QueueMessageAttachmentsAsync(db, [message.Id], at, cancellationToken).ConfigureAwait(false);
        }

        message.ChangeRevision = ++conversation!.ChangeRevision;
        return await CommitMutationAsync(db, transaction, conversation, message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TrustedMutationResult> SetHiddenAsync(TrustedMessageId messageId, bool hidden, TrustedAccessLogEntry logEntry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logEntry);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversationId = await db.Messages.AsNoTracking().Where(item => item.Id == messageId.Value).Select(static item => (Guid?)item.ConversationId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (conversationId is null)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.NotFoundOrForbidden, null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await LockConversationAsync(db, conversationId.Value, cancellationToken).ConfigureAwait(false);
        var message = conversation is null ? null : await db.Messages.SingleOrDefaultAsync(item => item.Id == messageId.Value, cancellationToken).ConfigureAwait(false);
        if (conversation is null || message is null)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.NotFoundOrForbidden, null);
        }

        if (message.IsErased)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.Conflict, null);
        }

        if ((message.HiddenAt is not null) == hidden)
        {
            return new TrustedMutationResult(TrustedStoreOutcome.Unchanged, (await ViewAsync(db, [message], cancellationToken).ConfigureAwait(false))[0]);
        }

        message.HiddenAt = hidden ? logEntry.At : null;
        message.ChangeRevision = ++conversation.ChangeRevision;
        db.AccessLog.Add(AccessLogRow.From(logEntry));
        return await CommitMutationAsync(db, transaction, conversation, message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TrustedMessage?> GetMessageAsync(TrustedMessageId messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Messages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == messageId.Value, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await ViewAsync(db, [row], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TrustedMessage>> GetMessagesAsync(TrustedConversationId id, TrustedMessageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = db.Messages.AsNoTracking().Where(item => item.ConversationId == id.Value && item.Sequence >= query.MinSequence);
        List<MessageRow> rows;
        if (query.ChangedSinceRevision is { } since)
        {
            rows = await source.Where(item => item.ChangeRevision > since).OrderBy(static item => item.ChangeRevision).Take(query.Limit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var before = query.BeforeSequence ?? long.MaxValue;
            rows = await source.Where(item => item.Sequence < before).OrderByDescending(static item => item.Sequence).Take(query.Limit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            rows.Reverse();
        }

        return await ViewAsync(db, rows, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TrustedReadMarkerResult> MarkReadAsync(TrustedConversationId id, Guid userId, long throughSequence, DateTimeOffset at, Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var advanced = await db.Database.SqlQueryRaw<long>(
            $$"""
            UPDATE {{Table(db, "participants")}} AS p
            SET read_through = LEAST({2}, c.last_sequence)
            FROM {{Table(db, "conversations")}} AS c
            WHERE c.id = p.conversation_id AND p.conversation_id = {0} AND p.user_id = {1} AND p.can_read
              AND p.read_through < LEAST({2}, c.last_sequence)
            RETURNING p.read_through AS "Value"
            """,
            id.Value, userId, throughSequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (advanced.Count == 1)
        {
            db.Outbox.Add(OutboxRow.From(ServerTrustedChatEventFactory.ReadMarkerAdvanced(eventId, id, userId, advanced[0], at)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync(db, string.Create(CultureInfo.InvariantCulture, $"R|{id}|0|{userId:D}|{advanced[0]}"), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TrustedReadMarkerResult(TrustedStoreOutcome.Applied, advanced[0]);
        }

        var participant = await db.Participants.AsNoTracking().SingleOrDefaultAsync(item => item.ConversationId == id.Value && item.UserId == userId, cancellationToken).ConfigureAwait(false);
        return participant is { CanRead: true }
            ? new TrustedReadMarkerResult(TrustedStoreOutcome.Unchanged, participant.ReadThrough)
            : new TrustedReadMarkerResult(TrustedStoreOutcome.NotFoundOrForbidden, 0);
    }

    /// <inheritdoc />
    public async ValueTask BeginAttachmentAsync(TrustedAttachmentRecord attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Attachments.Add(new AttachmentRow
        {
            Id = attachment.Info.Id.Value,
            ConversationId = attachment.ConversationId.Value,
            UploaderUserId = attachment.UploaderUserId,
            FileName = attachment.Info.FileName,
            ContentType = attachment.Info.ContentType,
            Length = attachment.Info.Length,
            CreatedAt = attachment.CreatedAt,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> CompleteAttachmentAsync(TrustedAttachmentId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Attachments.Where(item => item.Id == id.Value && item.MessageId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsReady, true), cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<TrustedAttachmentRecord?> GetAttachmentAsync(TrustedAttachmentId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id.Value, cancellationToken).ConfigureAwait(false);
        return row is null
            ? null
            : new TrustedAttachmentRecord(row.ToInfo(), new TrustedConversationId(row.ConversationId), row.UploaderUserId, row.CreatedAt,
                row.MessageId is { } message ? new TrustedMessageId(message) : null, row.IsReady);
    }

    /// <inheritdoc />
    public async ValueTask<(TrustedStoreOutcome Outcome, TrustedReport? Report)> CreateReportAsync(TrustedReportCreate request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = request.Report;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlRawAsync(
            $$"""
            INSERT INTO {{Table(db, "reports")}} (id, conversation_id, message_id, reporter_user_id, reason_code, comment, created_at)
            SELECT {0}, m.conversation_id, m.id, {2}, {3}, {4}, {5}
            FROM {{Table(db, "messages")}} AS m WHERE m.id = {1} AND m.conversation_id = {6}
            ON CONFLICT (message_id, reporter_user_id) DO NOTHING
            """,
            [report.Id.Value, report.MessageId.Value, report.ReporterUserId, report.ReasonCode, new NpgsqlParameter("comment", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)report.Comment ?? DBNull.Value }, report.CreatedAt, report.ConversationId.Value],
            cancellationToken).ConfigureAwait(false);
        if (inserted == 1)
        {
            db.Outbox.Add(OutboxRow.From(ServerTrustedChatEventFactory.MessageReported(request.EventId, report)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (TrustedStoreOutcome.Applied, report);
        }

        var existing = await db.Reports.AsNoTracking().SingleOrDefaultAsync(item => item.MessageId == report.MessageId.Value && item.ReporterUserId == report.ReporterUserId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existing is null ? (TrustedStoreOutcome.NotFoundOrForbidden, null) : (TrustedStoreOutcome.Duplicate, existing.ToDomain());
    }

    /// <inheritdoc />
    public async ValueTask<TrustedReport?> GetReportAsync(TrustedReportId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Reports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id.Value, cancellationToken).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ModerationMessage>> ReadReportWindowAsync(TrustedReport report, long fromSequence, long toSequence, TrustedAccessLogEntry logEntry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logEntry);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.AccessLog.Add(AccessLogRow.From(logEntry));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Messages.AsNoTracking()
            .Where(item => item.ConversationId == report.ConversationId.Value && item.Sequence >= fromSequence && item.Sequence <= toSequence)
            .OrderBy(static item => item.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(static item => item.Id).ToArray();
        var versions = await db.MessageVersions.AsNoTracking().Where(item => ids.Contains(item.MessageId)).OrderBy(static item => item.Version)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var attachments = await LoadAttachmentsAsync(db, ids, cancellationToken).ConfigureAwait(false);
        var views = await ViewAsync(db, rows, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select((row, index) => new ModerationMessage(
            views[index],
            row.IsErased ? null : row.Text ?? row.RetainedText,
            row.IsErased ? [] : attachments.TryGetValue(row.Id, out var list) ? list : [],
            versions.Where(item => item.MessageId == row.Id).Select(static item => item.Text).ToArray(),
            row.DeletedAt is not null,
            row.HiddenAt is not null,
            row.IsErased,
            row.Id == report.MessageId.Value)).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask AppendAccessLogAsync(TrustedAccessLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AccessLog.Add(AccessLogRow.From(entry));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TrustedAccessLogEntry>> GetAccessLogAsync(TrustedConversationId id, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AccessLog.AsNoTracking().Where(item => item.ConversationId == id.Value)
            .OrderByDescending(static item => item.At).ThenByDescending(static item => item.Id).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(static item => item.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<bool?> IsReadThroughAsync(TrustedConversationId id, Guid userId, long sequence, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var readThrough = await db.Participants.AsNoTracking().Where(item => item.ConversationId == id.Value && item.UserId == userId)
            .Select(static item => (long?)item.ReadThrough).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return readThrough is null ? null : readThrough >= sequence;
    }

    /// <inheritdoc />
    public async ValueTask<TrustedBlobDeletions> ForgetUserAsync(Guid userId, TrustedForgetMode mode, Guid surrogateUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var conversationIds = await db.Participants.Where(item => item.UserId == userId).Select(static item => item.ConversationId)
            .Union(db.Messages.Where(item => item.AuthorUserId == userId).Select(static item => item.ConversationId))
            .OrderBy(static item => item).ToListAsync(cancellationToken).ConfigureAwait(false);
        var conversations = new Dictionary<Guid, ConversationRow>();
        foreach (var conversationId in conversationIds)
        {
            if (await LockConversationAsync(db, conversationId, cancellationToken).ConfigureAwait(false) is { } locked)
            {
                conversations.Add(conversationId, locked);
            }
        }

        await db.Participants.Where(item => item.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var messages = await db.Messages.Where(item => item.AuthorUserId == userId).OrderBy(static item => item.ConversationId).ThenBy(static item => item.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var messageIds = messages.Select(static item => item.Id).ToArray();
        var queued = new List<Guid>();
        if (mode == TrustedForgetMode.EraseContent)
        {
            await db.MessageVersions.Where(item => messageIds.Contains(item.MessageId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            queued.AddRange(await QueueMessageAttachmentsAsync(db, messageIds, now, cancellationToken).ConfigureAwait(false));
        }

        foreach (var message in messages)
        {
            message.AuthorUserId = surrogateUserId;
            message.ClientMessageId = Guid.NewGuid();
            if (mode == TrustedForgetMode.EraseContent)
            {
                message.IsErased = true;
                message.Text = null;
                message.RetainedText = null;
                message.ReferencesJson = "[]";
            }

            message.ChangeRevision = ++conversations[message.ConversationId].ChangeRevision;
        }

        var pending = await db.Attachments.Where(item => item.UploaderUserId == userId && item.MessageId == null).Select(static item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        queued.AddRange(await QueueAttachmentsAsync(db, pending, now, cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.Reports.Where(item => item.ReporterUserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReporterUserId, surrogateUserId), cancellationToken).ConfigureAwait(false);
        await db.AccessLog.Where(item => item.ActorUserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ActorUserId, surrogateUserId), cancellationToken).ConfigureAwait(false);
        foreach (var conversation in conversations.Values)
        {
            await NotifyAsync(db, $"M|{conversation.Id:D}|{conversation.ChangeRevision}", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TrustedBlobDeletions(queued.Select(static item => new TrustedAttachmentId(item)).ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<TrustedBlobDeletions?> DeleteConversationAsync(TrustedConversationId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await LockConversationAsync(db, id.Value, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var queued = await DeleteConversationLockedAsync(db, id.Value, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TrustedBlobDeletions(queued);
    }

    /// <inheritdoc />
    public async ValueTask<(int Conversations, TrustedBlobDeletions Blobs)> PurgeClosedBeforeAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var targets = await db.Conversations.FromSqlRaw(
                $"SELECT * FROM {Table(db, "conversations")} WHERE status = 2 AND closed_at < {{0}} ORDER BY closed_at, id LIMIT {{1}} FOR UPDATE SKIP LOCKED",
                cutoff, maximumCount)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var queued = new List<TrustedAttachmentId>();
        foreach (var target in targets)
        {
            queued.AddRange(await DeleteConversationLockedAsync(db, target.Id, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (targets.Count, new TrustedBlobDeletions(queued));
    }

    /// <inheritdoc />
    public async ValueTask<TrustedBlobDeletions> ExpirePendingAttachmentsAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var expired = await db.Attachments.FromSqlRaw(
                $"SELECT * FROM {Table(db, "attachments")} WHERE message_id IS NULL AND created_at < {{0}} ORDER BY created_at LIMIT {{1}} FOR UPDATE SKIP LOCKED",
                cutoff, maximumCount)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var queued = await QueueAttachmentsAsync(db, expired.Select(static item => item.Id).ToArray(), _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TrustedBlobDeletions(queued.Select(static item => new TrustedAttachmentId(item)).ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<int> PurgeAccessLogBeforeAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM {Table(db, "access_log")} WHERE id IN (SELECT id FROM {Table(db, "access_log")} WHERE at < {{0}} ORDER BY at, id LIMIT {{1}})",
            [cutoff, maximumCount], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TrustedBlobDeletions> GetQueuedBlobDeletionsAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = await db.BlobDeletions.AsNoTracking().OrderBy(static item => item.QueuedAt).Take(maximumCount).Select(static item => item.AttachmentId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new TrustedBlobDeletions(ids.Select(static item => new TrustedAttachmentId(item)).ToArray());
    }

    /// <inheritdoc />
    public async ValueTask CompleteBlobDeletionsAsync(IReadOnlyList<TrustedAttachmentId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        var values = ids.Select(static item => item.Value).ToArray();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.BlobDeletions.Where(item => values.Contains(item.AttachmentId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private static string Table(ServerTrustedChatDbContext db, string table) => $"\"{db.Schema}\".{table}";

    private static async ValueTask<ConversationRow?> LockConversationAsync(ServerTrustedChatDbContext db, Guid id, CancellationToken cancellationToken) =>
        (await db.Conversations.FromSqlRaw($"SELECT * FROM {Table(db, "conversations")} WHERE id = {{0}} FOR UPDATE", id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    private static async ValueTask<(TrustedStoreOutcome? Outcome, ConversationRow? Conversation, MessageRow? Message)> LockOwnMessageAsync(
        ServerTrustedChatDbContext db, TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, CancellationToken cancellationToken)
    {
        var conversation = await LockConversationAsync(db, id.Value, cancellationToken).ConfigureAwait(false);
        var message = conversation is null ? null : await db.Messages.SingleOrDefaultAsync(item => item.Id == messageId.Value, cancellationToken).ConfigureAwait(false);
        var author = message is null ? null : await db.Participants.AsNoTracking().SingleOrDefaultAsync(item => item.ConversationId == id.Value && item.UserId == authorUserId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || message is null || message.ConversationId != id.Value || message.AuthorUserId != authorUserId ||
            author is not { Role: (short)TrustedParticipantRole.Writer, CanRead: true })
        {
            return (TrustedStoreOutcome.NotFoundOrForbidden, null, null);
        }

        return conversation.Status == (short)TrustedConversationStatus.Open ? (null, conversation, message) : (TrustedStoreOutcome.Closed, null, null);
    }

    private static async ValueTask<TrustedMutationResult> CommitMutationAsync(
        ServerTrustedChatDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, ConversationRow conversation, MessageRow message, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(db, $"M|{conversation.Id:D}|{conversation.ChangeRevision}", cancellationToken).ConfigureAwait(false);
        var view = (await ViewAsync(db, [message], cancellationToken).ConfigureAwait(false))[0];
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TrustedMutationResult(TrustedStoreOutcome.Applied, view);
    }

    private static async ValueTask<IReadOnlyList<TrustedAttachmentId>> DeleteConversationLockedAsync(ServerTrustedChatDbContext db, Guid id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var attachmentIds = await db.Attachments.Where(item => item.ConversationId == id).Select(static item => item.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var queued = await QueueAttachmentsAsync(db, attachmentIds, at, cancellationToken).ConfigureAwait(false);
        await db.Conversations.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(db, $"C|{id:D}|{long.MaxValue}", cancellationToken).ConfigureAwait(false);
        return queued.Select(static item => new TrustedAttachmentId(item)).ToArray();
    }

    private static async ValueTask<IReadOnlyList<Guid>> QueueMessageAttachmentsAsync(ServerTrustedChatDbContext db, Guid[] messageIds, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (messageIds.Length == 0)
        {
            return [];
        }

        var ids = await db.Attachments.Where(item => item.MessageId != null && messageIds.Contains(item.MessageId.Value)).Select(static item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await QueueAttachmentsAsync(db, ids, at, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<Guid>> QueueAttachmentsAsync(ServerTrustedChatDbContext db, IReadOnlyList<Guid> ids, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var array = ids.ToArray();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO {Table(db, "blob_deletions")} (attachment_id, queued_at) SELECT unnest({{0}}), {{1}} ON CONFLICT (attachment_id) DO NOTHING",
            [array, at], cancellationToken).ConfigureAwait(false);
        await db.Attachments.Where(item => array.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private static async ValueTask<int> CountUnreadAsync(ServerTrustedChatDbContext db, ParticipantRow participant, int cap, CancellationToken cancellationToken) =>
        await db.Messages.AsNoTracking()
            .Where(item => item.ConversationId == participant.ConversationId &&
                item.Sequence > participant.ReadThrough &&
                item.Sequence >= participant.HistoryVisibleFrom &&
                item.AuthorUserId != participant.UserId &&
                item.DeletedAt == null && item.HiddenAt == null && !item.IsErased)
            .Take(cap).CountAsync(cancellationToken).ConfigureAwait(false);

    private static async ValueTask<Dictionary<Guid, TrustedAttachmentInfo[]>> LoadAttachmentsAsync(ServerTrustedChatDbContext db, Guid[] messageIds, CancellationToken cancellationToken)
    {
        if (messageIds.Length == 0)
        {
            return [];
        }

        var rows = await db.Attachments.AsNoTracking().Where(item => item.MessageId != null && messageIds.Contains(item.MessageId.Value))
            .OrderBy(static item => item.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(static item => item.MessageId!.Value).ToDictionary(static item => item.Key, static item => item.Select(static row => row.ToInfo()).ToArray());
    }

    private static async ValueTask<IReadOnlyList<TrustedMessage>> ViewAsync(ServerTrustedChatDbContext db, IReadOnlyList<MessageRow> rows, CancellationToken cancellationToken)
    {
        var attachments = await LoadAttachmentsAsync(db, rows.Select(static item => item.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        return rows.Select(row => ToView(row, attachments.TryGetValue(row.Id, out var list) ? list : [])).ToArray();
    }

    private static TrustedMessage ToView(MessageRow row, IReadOnlyList<TrustedAttachmentInfo> attachments)
    {
        var tombstone = row.Tombstone;
        return new TrustedMessage(
            new TrustedMessageId(row.Id),
            new TrustedConversationId(row.ConversationId),
            row.Sequence,
            row.ChangeRevision,
            row.AuthorUserId,
            row.AuthorLabel,
            tombstone == TrustedMessageTombstone.None ? row.Text : null,
            row.ReplyToMessageId is { } reply ? new TrustedMessageId(reply) : null,
            tombstone == TrustedMessageTombstone.None ? DeserializeReferences(row.ReferencesJson) : [],
            tombstone == TrustedMessageTombstone.None ? attachments : [],
            row.CreatedAt,
            row.EditedAt,
            tombstone);
    }

    private static async ValueTask NotifyAsync(ServerTrustedChatDbContext db, string payload, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync("SELECT pg_notify({0}, {1})", [ServerTrustedChatSchema.ChannelFor(db.Schema), payload], cancellationToken).ConfigureAwait(false);

    private static byte[] Fingerprint(TrustedSendRecord request)
    {
        var builder = new StringBuilder();
        builder.Append(request.Text is null ? "\u0000" : request.Text).Append('\u001f');
        builder.Append(request.ReplyToMessageId?.ToString() ?? "-").Append('\u001f');
        foreach (var reference in request.References)
        {
            builder.Append(reference.Type).Append('\u001d').Append(reference.Id).Append('\u001e');
        }

        builder.Append('\u001f');
        foreach (var attachment in request.AttachmentIds)
        {
            builder.Append(attachment.ToString()).Append('\u001e');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string SerializeReferences(IReadOnlyList<HostReference> references) =>
        JsonSerializer.Serialize(references.Select(static item => new ReferenceDocument(item.Type, item.Id)).ToArray(), ReferenceJsonContext.Default.ReferenceDocumentArray);

    private static HostReference[] DeserializeReferences(string json) =>
        (JsonSerializer.Deserialize(json, ReferenceJsonContext.Default.ReferenceDocumentArray) ?? []).Select(static item => new HostReference(item.Type, item.Id)).ToArray();

    internal sealed record ReferenceDocument(string Type, string Id);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PostgreSqlServerTrustedChatStore.ReferenceDocument[]))]
internal sealed partial class ReferenceJsonContext : JsonSerializerContext;
