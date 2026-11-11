namespace Skopka.Chat.ServerTrusted;

/// <summary>
/// Single-process reference store for tests and samples. It holds plaintext in memory without durability and is
/// never a production recommendation; use the PostgreSQL store.
/// </summary>
public sealed class InMemoryServerTrustedChatStore : IServerTrustedChatStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TrustedConversationId, Conversation> _conversations = [];
    private readonly Dictionary<TrustedMessageId, Message> _messages = [];
    private readonly Dictionary<TrustedAttachmentId, Attachment> _attachments = [];
    private readonly Dictionary<TrustedReportId, TrustedReport> _reports = [];
    private readonly List<TrustedAccessLogEntry> _accessLog = [];
    private readonly HashSet<TrustedAttachmentId> _blobDeletions = [];
    private readonly List<ServerTrustedChatEvent> _events = [];

    /// <summary>Events written atomically with domain changes, in commit order.</summary>
    public IReadOnlyList<ServerTrustedChatEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<(TrustedConversation Conversation, bool Created)> GetOrCreateConversationAsync(TrustedConversationCreate request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var existing = _conversations.Values.FirstOrDefault(item => item.HostKey == request.HostKey);
            if (existing is not null)
            {
                return ValueTask.FromResult((existing.Snapshot(), false));
            }

            var conversation = new Conversation(request.Id, request.HostKey, request.Kind, request.CreatedAt);
            foreach (var spec in request.Participants)
            {
                conversation.Participants.Add(new Participant(spec.UserId, spec.Role, spec.CanRead, spec.Label, request.CreatedAt, 0, 0));
            }

            _conversations.Add(conversation.Id, conversation);
            return ValueTask.FromResult((conversation.Snapshot(), true));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedConversation?> GetConversationAsync(TrustedConversationId id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_conversations.TryGetValue(id, out var item) ? item.Snapshot() : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedConversation?> FindConversationAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_conversations.Values.FirstOrDefault(item => item.HostKey == hostKey)?.Snapshot());
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedStoreOutcome> SetStatusAsync(TrustedConversationId id, TrustedConversationStatus status, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(id, out var conversation))
            {
                return ValueTask.FromResult(TrustedStoreOutcome.NotFoundOrForbidden);
            }

            if (conversation.Status == status)
            {
                return ValueTask.FromResult(TrustedStoreOutcome.Unchanged);
            }

            conversation.Status = status;
            conversation.ClosedAt = status == TrustedConversationStatus.Closed ? at : null;
            return ValueTask.FromResult(TrustedStoreOutcome.Applied);
        }
    }

    /// <summary>Test hook: sets the closure time used by retention.</summary>
    public void SetClosedAtForTesting(TrustedConversationId id, DateTimeOffset closedAt)
    {
        lock (_gate)
        {
            _conversations[id].ClosedAt = closedAt;
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedStoreOutcome> UpsertParticipantAsync(TrustedConversationId id, TrustedParticipantSpec participant, DateTimeOffset at, int maxParticipants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (_gate)
        {
            if (!_conversations.TryGetValue(id, out var conversation))
            {
                return ValueTask.FromResult(TrustedStoreOutcome.NotFoundOrForbidden);
            }

            var index = conversation.Participants.FindIndex(item => item.UserId == participant.UserId);
            if (index >= 0)
            {
                var current = conversation.Participants[index];
                var updated = current with { Role = participant.Role, CanRead = participant.CanRead, Label = participant.Label };
                if (updated == current)
                {
                    return ValueTask.FromResult(TrustedStoreOutcome.Unchanged);
                }

                conversation.Participants[index] = updated;
                return ValueTask.FromResult(TrustedStoreOutcome.Applied);
            }

            if (conversation.Participants.Count >= maxParticipants)
            {
                return ValueTask.FromResult(TrustedStoreOutcome.LimitReached);
            }

            var visibleFrom = participant.SeesHistory ? 0 : conversation.LastSequence + 1;
            var readThrough = participant.SeesHistory ? 0 : conversation.LastSequence;
            conversation.Participants.Add(new Participant(participant.UserId, participant.Role, participant.CanRead, participant.Label, at, visibleFrom, readThrough));
            return ValueTask.FromResult(TrustedStoreOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedStoreOutcome> RemoveParticipantAsync(TrustedConversationId id, Guid userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_conversations.TryGetValue(id, out var conversation) &&
                conversation.Participants.RemoveAll(item => item.UserId == userId) > 0
                    ? TrustedStoreOutcome.Applied
                    : TrustedStoreOutcome.NotFoundOrForbidden);
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedParticipant?> GetParticipantAsync(TrustedConversationId id, Guid userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_conversations.TryGetValue(id, out var conversation)
                ? conversation.Participants.FirstOrDefault(item => item.UserId == userId)?.ToPublic()
                : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TrustedParticipant>> ListParticipantsAsync(TrustedConversationId id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TrustedParticipant> result = _conversations.TryGetValue(id, out var conversation)
                ? conversation.Participants.Select(static item => item.ToPublic()).ToArray()
                : [];
            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TrustedConversationSummary>> ListConversationsAsync(Guid userId, int limit, int unreadCap, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TrustedConversationSummary> result = _conversations.Values
                .Select(conversation => (conversation, participant: conversation.Participants.FirstOrDefault(item => item.UserId == userId)))
                .Where(static item => item.participant is { CanRead: true })
                .OrderByDescending(static item => item.conversation.LastActivityAt)
                .ThenBy(static item => item.conversation.Id.Value)
                .Take(limit)
                .Select(item => new TrustedConversationSummary(item.conversation.Snapshot(), item.participant!.ToPublic(), CountUnread(item.conversation, item.participant!, unreadCap)))
                .ToArray();
            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> GetUnreadTotalAsync(Guid userId, int unreadCap, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var total = 0;
            foreach (var conversation in _conversations.Values)
            {
                var participant = conversation.Participants.FirstOrDefault(item => item.UserId == userId);
                if (participant is { CanRead: true })
                {
                    total += CountUnread(conversation, participant, unreadCap);
                }
            }

            return ValueTask.FromResult(total);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> GetUnreadCountAsync(TrustedConversationId id, Guid userId, int unreadCap, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var participant = _conversations.TryGetValue(id, out var conversation)
                ? conversation.Participants.FirstOrDefault(item => item.UserId == userId)
                : null;
            return ValueTask.FromResult(participant is { CanRead: true } ? CountUnread(conversation!, participant, unreadCap) : 0);
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedSendResult> SendAsync(TrustedSendRecord request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_conversations.TryGetValue(request.ConversationId, out var conversation))
            {
                return Result(TrustedStoreOutcome.NotFoundOrForbidden);
            }

            var key = (request.AuthorUserId, request.ClientMessageId);
            if (conversation.Idempotency.TryGetValue(key, out var existingId))
            {
                var existing = _messages[existingId];
                return Result(existing.Fingerprint == Fingerprint(request) ? TrustedStoreOutcome.Duplicate : TrustedStoreOutcome.Conflict,
                    existing.Fingerprint == Fingerprint(request) ? View(existing) : null);
            }

            var author = conversation.Participants.FirstOrDefault(item => item.UserId == request.AuthorUserId);
            if (author is not { Role: TrustedParticipantRole.Writer, CanRead: true })
            {
                return Result(TrustedStoreOutcome.NotFoundOrForbidden);
            }

            if (conversation.Status != TrustedConversationStatus.Open)
            {
                return Result(TrustedStoreOutcome.Closed);
            }

            if (request.ReplyToMessageId is { } replyId &&
                (!_messages.TryGetValue(replyId, out var reply) || reply.ConversationId != conversation.Id ||
                 reply.Sequence < author.HistoryVisibleFromSequence || reply.Tombstone != TrustedMessageTombstone.None))
            {
                return Result(TrustedStoreOutcome.Conflict);
            }

            var attachments = new List<Attachment>();
            foreach (var attachmentId in request.AttachmentIds)
            {
                if (!_attachments.TryGetValue(attachmentId, out var attachment) || !attachment.IsReady || attachment.MessageId is not null ||
                    attachment.ConversationId != conversation.Id || attachment.UploaderUserId != request.AuthorUserId)
                {
                    return Result(TrustedStoreOutcome.Conflict);
                }

                attachments.Add(attachment);
            }

            var message = new Message
            {
                Id = request.MessageId,
                ConversationId = conversation.Id,
                Sequence = ++conversation.LastSequence,
                ChangeRevision = ++conversation.ChangeRevision,
                AuthorUserId = request.AuthorUserId,
                AuthorLabel = author.Label,
                Text = request.Text,
                ReplyToMessageId = request.ReplyToMessageId,
                References = [.. request.References],
                AttachmentIds = [.. request.AttachmentIds],
                CreatedAt = request.CreatedAt,
                Fingerprint = Fingerprint(request),
            };
            foreach (var attachment in attachments)
            {
                attachment.MessageId = message.Id;
            }

            conversation.LastActivityAt = request.CreatedAt;
            conversation.Idempotency.Add(key, message.Id);
            _messages.Add(message.Id, message);
            var view = View(message);
            _events.Add(ServerTrustedChatEventFactory.MessageCreated(request.EventId, view));
            return Result(TrustedStoreOutcome.Applied, view);
        }

        static ValueTask<TrustedSendResult> Result(TrustedStoreOutcome outcome, TrustedMessage? message = null) =>
            ValueTask.FromResult(new TrustedSendResult(outcome, message));
    }

    /// <inheritdoc />
    public ValueTask<TrustedMutationResult> EditAsync(TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, string text, DateTimeOffset at, bool retainPriorVersion, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var check = CheckOwnWrite(id, messageId, authorUserId, out var conversation, out var message);
            if (check is not null)
            {
                return Mutation(check.Value);
            }

            if (message!.Tombstone != TrustedMessageTombstone.None)
            {
                return Mutation(TrustedStoreOutcome.Conflict);
            }

            if (message.Text == text)
            {
                return Mutation(TrustedStoreOutcome.Unchanged, View(message));
            }

            if (retainPriorVersion && message.Text is not null)
            {
                message.PriorVersions.Add(message.Text);
            }

            message.Text = text;
            message.EditedAt = at;
            message.ChangeRevision = ++conversation!.ChangeRevision;
            return Mutation(TrustedStoreOutcome.Applied, View(message));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedMutationResult> DeleteOwnAsync(TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, DateTimeOffset at, bool retainContent, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var check = CheckOwnWrite(id, messageId, authorUserId, out var conversation, out var message);
            if (check is not null)
            {
                return Mutation(check.Value);
            }

            if (message!.DeletedAt is not null)
            {
                return Mutation(TrustedStoreOutcome.Duplicate, View(message));
            }

            if (message.Tombstone != TrustedMessageTombstone.None)
            {
                return Mutation(TrustedStoreOutcome.Conflict);
            }

            message.DeletedAt = at;
            message.RetainedText = retainContent ? message.Text : null;
            message.Text = null;
            if (!retainContent)
            {
                message.PriorVersions.Clear();
                QueueAttachmentsOf(message);
            }

            message.ChangeRevision = ++conversation!.ChangeRevision;
            return Mutation(TrustedStoreOutcome.Applied, View(message));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedMutationResult> SetHiddenAsync(TrustedMessageId messageId, bool hidden, TrustedAccessLogEntry logEntry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logEntry);
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var message) || !_conversations.TryGetValue(message.ConversationId, out var conversation))
            {
                return Mutation(TrustedStoreOutcome.NotFoundOrForbidden);
            }

            if (message.IsErased)
            {
                return Mutation(TrustedStoreOutcome.Conflict);
            }

            if ((message.HiddenAt is not null) == hidden)
            {
                return Mutation(TrustedStoreOutcome.Unchanged, View(message));
            }

            message.HiddenAt = hidden ? logEntry.At : null;
            message.ChangeRevision = ++conversation.ChangeRevision;
            _accessLog.Add(logEntry);
            return Mutation(TrustedStoreOutcome.Applied, View(message));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedMessage?> GetMessageAsync(TrustedMessageId messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_messages.TryGetValue(messageId, out var message) ? View(message) : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TrustedMessage>> GetMessagesAsync(TrustedConversationId id, TrustedMessageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            var source = _messages.Values.Where(item => item.ConversationId == id && item.Sequence >= query.MinSequence);
            IReadOnlyList<TrustedMessage> result = query.ChangedSinceRevision is { } since
                ? source.Where(item => item.ChangeRevision > since).OrderBy(static item => item.ChangeRevision).Take(query.Limit).Select(View).ToArray()
                : source.Where(item => item.Sequence < (query.BeforeSequence ?? long.MaxValue))
                    .OrderByDescending(static item => item.Sequence).Take(query.Limit).OrderBy(static item => item.Sequence).Select(View).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedReadMarkerResult> MarkReadAsync(TrustedConversationId id, Guid userId, long throughSequence, DateTimeOffset at, Guid eventId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(id, out var conversation))
            {
                return ValueTask.FromResult(new TrustedReadMarkerResult(TrustedStoreOutcome.NotFoundOrForbidden, 0));
            }

            var index = conversation.Participants.FindIndex(item => item.UserId == userId);
            if (index < 0 || !conversation.Participants[index].CanRead)
            {
                return ValueTask.FromResult(new TrustedReadMarkerResult(TrustedStoreOutcome.NotFoundOrForbidden, 0));
            }

            var participant = conversation.Participants[index];
            var target = Math.Min(throughSequence, conversation.LastSequence);
            if (target <= participant.ReadThroughSequence)
            {
                return ValueTask.FromResult(new TrustedReadMarkerResult(TrustedStoreOutcome.Unchanged, participant.ReadThroughSequence));
            }

            conversation.Participants[index] = participant with { ReadThroughSequence = target };
            _events.Add(ServerTrustedChatEventFactory.ReadMarkerAdvanced(eventId, id, userId, target, at));
            return ValueTask.FromResult(new TrustedReadMarkerResult(TrustedStoreOutcome.Applied, target));
        }
    }

    /// <inheritdoc />
    public ValueTask BeginAttachmentAsync(TrustedAttachmentRecord attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        lock (_gate)
        {
            _attachments.Add(attachment.Info.Id, new Attachment(attachment.Info, attachment.ConversationId, attachment.UploaderUserId, attachment.CreatedAt));
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> CompleteAttachmentAsync(TrustedAttachmentId id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_attachments.TryGetValue(id, out var attachment))
            {
                return ValueTask.FromResult(false);
            }

            attachment.IsReady = true;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedAttachmentRecord?> GetAttachmentAsync(TrustedAttachmentId id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_attachments.TryGetValue(id, out var item)
                ? new TrustedAttachmentRecord(item.Info, item.ConversationId, item.UploaderUserId, item.CreatedAt, item.MessageId, item.IsReady)
                : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<(TrustedStoreOutcome Outcome, TrustedReport? Report)> CreateReportAsync(TrustedReportCreate request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_messages.ContainsKey(request.Report.MessageId))
            {
                return ValueTask.FromResult<(TrustedStoreOutcome, TrustedReport?)>((TrustedStoreOutcome.NotFoundOrForbidden, null));
            }

            var existing = _reports.Values.FirstOrDefault(item => item.MessageId == request.Report.MessageId && item.ReporterUserId == request.Report.ReporterUserId);
            if (existing is not null)
            {
                return ValueTask.FromResult<(TrustedStoreOutcome, TrustedReport?)>((TrustedStoreOutcome.Duplicate, existing));
            }

            _reports.Add(request.Report.Id, request.Report);
            _events.Add(ServerTrustedChatEventFactory.MessageReported(request.EventId, request.Report));
            return ValueTask.FromResult<(TrustedStoreOutcome, TrustedReport?)>((TrustedStoreOutcome.Applied, request.Report));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedReport?> GetReportAsync(TrustedReportId id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_reports.TryGetValue(id, out var report) ? report : null);
        }
    }

    /// <summary>When set, access-log writes fail; used to prove fail-closed moderation reads.</summary>
    public bool FailAccessLogWritesForTesting { get; set; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModerationMessage>> ReadReportWindowAsync(TrustedReport report, long fromSequence, long toSequence, TrustedAccessLogEntry logEntry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logEntry);
        lock (_gate)
        {
            IReadOnlyList<ModerationMessage> window = _messages.Values
                .Where(item => item.ConversationId == report.ConversationId && item.Sequence >= fromSequence && item.Sequence <= toSequence)
                .OrderBy(static item => item.Sequence)
                .Select(item => new ModerationMessage(
                    View(item),
                    item.IsErased ? null : item.Text ?? item.RetainedText,
                    item.IsErased ? [] : item.AttachmentIds.Where(_attachments.ContainsKey).Select(attachment => _attachments[attachment].Info).ToArray(),
                    [.. item.PriorVersions],
                    item.DeletedAt is not null,
                    item.HiddenAt is not null,
                    item.IsErased,
                    item.Id == report.MessageId))
                .ToArray();
            AppendLog(logEntry);
            return ValueTask.FromResult(window);
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAccessLogAsync(TrustedAccessLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            AppendLog(entry);
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TrustedAccessLogEntry>> GetAccessLogAsync(TrustedConversationId id, int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TrustedAccessLogEntry> result = _accessLog.Where(item => item.ConversationId == id).Reverse().Take(limit).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool?> IsReadThroughAsync(TrustedConversationId id, Guid userId, long sequence, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var participant = _conversations.TryGetValue(id, out var conversation)
                ? conversation.Participants.FirstOrDefault(item => item.UserId == userId)
                : null;
            return ValueTask.FromResult<bool?>(participant is null ? null : participant.ReadThroughSequence >= sequence);
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedBlobDeletions> ForgetUserAsync(Guid userId, TrustedForgetMode mode, Guid surrogateUserId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var before = _blobDeletions.ToHashSet();
            foreach (var conversation in _conversations.Values)
            {
                conversation.Participants.RemoveAll(item => item.UserId == userId);
                foreach (var key in conversation.Idempotency.Keys.Where(item => item.Author == userId).ToArray())
                {
                    conversation.Idempotency.Remove(key);
                }
            }

            foreach (var message in _messages.Values.Where(item => item.AuthorUserId == userId))
            {
                message.AuthorUserId = surrogateUserId;
                if (mode == TrustedForgetMode.EraseContent)
                {
                    message.IsErased = true;
                    message.Text = null;
                    message.RetainedText = null;
                    message.PriorVersions.Clear();
                    message.References.Clear();
                    QueueAttachmentsOf(message);
                    message.AttachmentIds.Clear();
                }

                message.ChangeRevision = ++_conversations[message.ConversationId].ChangeRevision;
            }

            foreach (var attachment in _attachments.Values.Where(item => item.UploaderUserId == userId && item.MessageId is null).ToArray())
            {
                _blobDeletions.Add(attachment.Info.Id);
                _attachments.Remove(attachment.Info.Id);
            }

            foreach (var report in _reports.Values.Where(item => item.ReporterUserId == userId).ToArray())
            {
                _reports[report.Id] = report with { ReporterUserId = surrogateUserId };
            }

            for (var index = 0; index < _accessLog.Count; index++)
            {
                if (_accessLog[index].ActorUserId == userId)
                {
                    _accessLog[index] = _accessLog[index] with { ActorUserId = surrogateUserId };
                }
            }

            return ValueTask.FromResult(new TrustedBlobDeletions(_blobDeletions.Except(before).ToArray()));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedBlobDeletions?> DeleteConversationAsync(TrustedConversationId id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_conversations.ContainsKey(id) ? DeleteConversationLocked(id) : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<(int Conversations, TrustedBlobDeletions Blobs)> PurgeClosedBeforeAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var targets = _conversations.Values
                .Where(item => item.Status == TrustedConversationStatus.Closed && item.ClosedAt < cutoff)
                .OrderBy(static item => item.ClosedAt)
                .Take(maximumCount)
                .Select(static item => item.Id)
                .ToArray();
            var blobs = targets.SelectMany(item => DeleteConversationLocked(item).AttachmentIds).ToArray();
            return ValueTask.FromResult((targets.Length, new TrustedBlobDeletions(blobs)));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedBlobDeletions> ExpirePendingAttachmentsAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var expired = _attachments.Values.Where(item => item.MessageId is null && item.CreatedAt < cutoff).Take(maximumCount).ToArray();
            foreach (var attachment in expired)
            {
                _attachments.Remove(attachment.Info.Id);
                _blobDeletions.Add(attachment.Info.Id);
            }

            return ValueTask.FromResult(new TrustedBlobDeletions(expired.Select(static item => item.Info.Id).ToArray()));
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeAccessLogBeforeAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var targets = _accessLog.Where(item => item.At < cutoff).OrderBy(static item => item.At).Take(maximumCount).ToHashSet();
            return ValueTask.FromResult(_accessLog.RemoveAll(targets.Contains));
        }
    }

    /// <inheritdoc />
    public ValueTask<TrustedBlobDeletions> GetQueuedBlobDeletionsAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(new TrustedBlobDeletions(_blobDeletions.Take(maximumCount).ToArray()));
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteBlobDeletionsAsync(IReadOnlyList<TrustedAttachmentId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            foreach (var id in ids)
            {
                _blobDeletions.Remove(id);
            }

            return ValueTask.CompletedTask;
        }
    }

    private void AppendLog(TrustedAccessLogEntry entry)
    {
        if (FailAccessLogWritesForTesting)
        {
            throw new InvalidOperationException("The access log is unavailable.");
        }

        _accessLog.Add(entry);
    }

    private TrustedBlobDeletions DeleteConversationLocked(TrustedConversationId id)
    {
        var attachments = _attachments.Values.Where(item => item.ConversationId == id).Select(static item => item.Info.Id).ToArray();
        foreach (var attachmentId in attachments)
        {
            _attachments.Remove(attachmentId);
            _blobDeletions.Add(attachmentId);
        }

        foreach (var messageId in _messages.Values.Where(item => item.ConversationId == id).Select(static item => item.Id).ToArray())
        {
            _messages.Remove(messageId);
        }

        foreach (var reportId in _reports.Values.Where(item => item.ConversationId == id).Select(static item => item.Id).ToArray())
        {
            _reports.Remove(reportId);
        }

        _conversations.Remove(id);
        return new TrustedBlobDeletions(attachments);
    }

    private void QueueAttachmentsOf(Message message)
    {
        foreach (var attachmentId in message.AttachmentIds)
        {
            if (_attachments.Remove(attachmentId))
            {
                _blobDeletions.Add(attachmentId);
            }
        }
    }

    private TrustedStoreOutcome? CheckOwnWrite(TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, out Conversation? conversation, out Message? message)
    {
        message = null;
        if (!_conversations.TryGetValue(id, out conversation) || !_messages.TryGetValue(messageId, out message) ||
            message.ConversationId != id || message.AuthorUserId != authorUserId ||
            conversation.Participants.FirstOrDefault(item => item.UserId == authorUserId) is not { Role: TrustedParticipantRole.Writer, CanRead: true })
        {
            return TrustedStoreOutcome.NotFoundOrForbidden;
        }

        return conversation.Status == TrustedConversationStatus.Open ? null : TrustedStoreOutcome.Closed;
    }

    private int CountUnread(Conversation conversation, Participant participant, int cap) =>
        Math.Min(cap, _messages.Values.Count(item =>
            item.ConversationId == conversation.Id &&
            item.Sequence > participant.ReadThroughSequence &&
            item.Sequence >= participant.HistoryVisibleFromSequence &&
            item.AuthorUserId != participant.UserId &&
            item.Tombstone == TrustedMessageTombstone.None));

    private TrustedMessage View(Message message)
    {
        var tombstone = message.Tombstone;
        IReadOnlyList<TrustedAttachmentInfo> attachments = tombstone == TrustedMessageTombstone.None
            ? message.AttachmentIds.Where(_attachments.ContainsKey).Select(item => _attachments[item].Info).ToArray()
            : [];
        return new TrustedMessage(
            message.Id,
            message.ConversationId,
            message.Sequence,
            message.ChangeRevision,
            message.AuthorUserId,
            message.AuthorLabel,
            tombstone == TrustedMessageTombstone.None ? message.Text : null,
            message.ReplyToMessageId,
            tombstone == TrustedMessageTombstone.None ? [.. message.References] : [],
            attachments,
            message.CreatedAt,
            message.EditedAt,
            tombstone);
    }

    private static string Fingerprint(TrustedSendRecord request) =>
        string.Join('\u001f',
            request.Text ?? "\u0000",
            request.ReplyToMessageId?.ToString() ?? "-",
            string.Join('\u001e', request.References.Select(static item => item.Type + "\u001d" + item.Id)),
            string.Join('\u001e', request.AttachmentIds.Select(static item => item.ToString())));

    private static ValueTask<TrustedMutationResult> Mutation(TrustedStoreOutcome outcome, TrustedMessage? message = null) =>
        ValueTask.FromResult(new TrustedMutationResult(outcome, message));

    private sealed class Conversation(TrustedConversationId id, string hostKey, TrustedConversationKind kind, DateTimeOffset createdAt)
    {
        public TrustedConversationId Id { get; } = id;
        public string HostKey { get; } = hostKey;
        public TrustedConversationKind Kind { get; } = kind;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public TrustedConversationStatus Status { get; set; } = TrustedConversationStatus.Open;
        public DateTimeOffset? ClosedAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;
        public long LastSequence { get; set; }
        public long ChangeRevision { get; set; }
        public List<Participant> Participants { get; } = [];
        public Dictionary<(Guid Author, Guid ClientId), TrustedMessageId> Idempotency { get; } = [];

        public TrustedConversation Snapshot() => new(Id, HostKey, Kind, Status, CreatedAt, LastSequence, ChangeRevision);
    }

    private sealed record Participant(Guid UserId, TrustedParticipantRole Role, bool CanRead, string Label, DateTimeOffset JoinedAt, long HistoryVisibleFromSequence, long ReadThroughSequence)
    {
        public TrustedParticipant ToPublic() => new(UserId, Role, CanRead, Label, JoinedAt, HistoryVisibleFromSequence, ReadThroughSequence);
    }

    private sealed class Message
    {
        public required TrustedMessageId Id { get; init; }
        public required TrustedConversationId ConversationId { get; init; }
        public required long Sequence { get; init; }
        public required long ChangeRevision { get; set; }
        public required Guid AuthorUserId { get; set; }
        public required string AuthorLabel { get; init; }
        public string? Text { get; set; }
        public string? RetainedText { get; set; }
        public List<string> PriorVersions { get; } = [];
        public TrustedMessageId? ReplyToMessageId { get; init; }
        public required List<HostReference> References { get; init; }
        public required List<TrustedAttachmentId> AttachmentIds { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? EditedAt { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public DateTimeOffset? HiddenAt { get; set; }
        public bool IsErased { get; set; }
        public required string Fingerprint { get; init; }

        public TrustedMessageTombstone Tombstone =>
            IsErased ? TrustedMessageTombstone.Erased
            : HiddenAt is not null ? TrustedMessageTombstone.HiddenByModerator
            : DeletedAt is not null ? TrustedMessageTombstone.DeletedByAuthor
            : TrustedMessageTombstone.None;
    }

    private sealed class Attachment(TrustedAttachmentInfo info, TrustedConversationId conversationId, Guid uploaderUserId, DateTimeOffset createdAt)
    {
        public TrustedAttachmentInfo Info { get; } = info;
        public TrustedConversationId ConversationId { get; } = conversationId;
        public Guid UploaderUserId { get; } = uploaderUserId;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public TrustedMessageId? MessageId { get; set; }
        public bool IsReady { get; set; }
    }
}
