using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Hard bounds for recipient-device fan-out.</summary>
public static class ChatFanOutLimits
{
    /// <summary>Maximum required remote or sibling-device envelopes for one logical event.</summary>
    public const int MaxRecipientDevices = 100;
}

/// <summary>Outcome of atomically storing an immutable fan-out plan.</summary>
public enum ChatFanOutPlanStoreResult
{
    /// <summary>The plan was stored before network submission.</summary>
    Stored = 1,

    /// <summary>The exact plan already exists.</summary>
    Duplicate = 2,

    /// <summary>The logical content ID is already bound to different data.</summary>
    Conflict = 3,
}

/// <summary>One immutable recipient-specific envelope and its acceptance state.</summary>
public sealed record ChatEnvelopePlanItem(EncryptedEnvelope Envelope, bool IsAccepted);

/// <summary>Immutable encrypted fan-out plan persisted before the first network attempt.</summary>
public sealed class ChatFanOutPlan
{
    private readonly byte[] _contentHash;
    private readonly ChatEnvelopePlanItem[] _envelopes;

    /// <summary>Creates a bounded plan containing ciphertext but no message plaintext.</summary>
    public ChatFanOutPlan(
        ConversationId conversationId,
        ChatContentId contentId,
        UserId senderUserId,
        DeviceId senderDeviceId,
        MessageId localEchoMessageId,
        DateTimeOffset sentAt,
        ReadOnlySpan<byte> contentHash,
        IReadOnlyList<ChatEnvelopePlanItem> envelopes,
        DateTimeOffset? completedAt = null)
    {
        if (conversationId.Value == Guid.Empty || contentId.Value == Guid.Empty ||
            senderUserId.Value == Guid.Empty || senderDeviceId.Value == Guid.Empty ||
            localEchoMessageId.Value == Guid.Empty || sentAt == default ||
            contentHash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("The fan-out plan is invalid.");
        }

        ArgumentNullException.ThrowIfNull(envelopes);
        if (envelopes.Count is < 1 or > ChatFanOutLimits.MaxRecipientDevices)
        {
            throw new ArgumentOutOfRangeException(nameof(envelopes));
        }

        _envelopes = envelopes.ToArray();
        var recipients = new HashSet<DeviceId>();
        var messages = new HashSet<MessageId>();
        foreach (var item in _envelopes)
        {
            ArgumentNullException.ThrowIfNull(item);
            ProtocolValidator.Validate(item.Envelope);
            if (item.Envelope.ConversationId != conversationId ||
                item.Envelope.SenderDeviceId != senderDeviceId ||
                item.Envelope.SentAt != sentAt ||
                !recipients.Add(item.Envelope.RecipientDeviceId) ||
                !messages.Add(item.Envelope.MessageId) ||
                item.Envelope.MessageId == localEchoMessageId)
            {
                throw new ArgumentException("The fan-out plan is inconsistent.", nameof(envelopes));
            }
        }

        if (completedAt < sentAt)
        {
            throw new ArgumentException("The fan-out completion timestamp is invalid.", nameof(completedAt));
        }

        ConversationId = conversationId;
        ContentId = contentId;
        SenderUserId = senderUserId;
        SenderDeviceId = senderDeviceId;
        LocalEchoMessageId = localEchoMessageId;
        SentAt = sentAt;
        _contentHash = contentHash.ToArray();
        CompletedAt = completedAt;
    }

    /// <summary>Conversation receiving the logical event.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Stable logical event identifier.</summary>
    public ChatContentId ContentId { get; }

    /// <summary>Authenticated sending user.</summary>
    public UserId SenderUserId { get; }

    /// <summary>Authenticated sending device.</summary>
    public DeviceId SenderDeviceId { get; }

    /// <summary>Stable local journal identifier for the current-device echo.</summary>
    public MessageId LocalEchoMessageId { get; }

    /// <summary>Sender timestamp shared by every recipient envelope and the local echo.</summary>
    public DateTimeOffset SentAt { get; }

    /// <summary>SHA-256 of canonical typed content, used only for conflict detection.</summary>
    public ReadOnlyMemory<byte> ContentHash => _contentHash;

    /// <summary>Recipient-specific immutable ciphertext plans.</summary>
    public IReadOnlyList<ChatEnvelopePlanItem> Envelopes => _envelopes;

    /// <summary>Completion time after every required envelope was accepted.</summary>
    public DateTimeOffset? CompletedAt { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatFanOutPlan(ContentId={ContentId}, Envelopes={_envelopes.Length}, Payload=[REDACTED])";
}

/// <summary>Plan persistence boundary used by the transport-independent sender.</summary>
public interface IChatFanOutPlanStore
{
    /// <summary>Loads an existing logical operation and recipient acceptance state.</summary>
    ValueTask<ChatFanOutPlan?> LoadAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically stores or compares a complete encrypted plan before network use.</summary>
    ValueTask<ChatFanOutPlanStoreResult> StoreAsync(
        ChatFanOutPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>Marks one exact recipient envelope accepted or duplicated by the transport.</summary>
    ValueTask MarkAcceptedAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        MessageId messageId,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the logical operation complete after all required envelopes are accepted.</summary>
    ValueTask MarkCompletedAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Non-durable fan-out plan store for tests and short-lived hosts.</summary>
public sealed class InMemoryChatFanOutPlanStore : IChatFanOutPlanStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(ConversationId ConversationId, ChatContentId ContentId), ChatFanOutPlan> _plans = [];

    /// <inheritdoc />
    public ValueTask<ChatFanOutPlan?> LoadAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_plans.GetValueOrDefault((conversationId, contentId)));
        }
    }

    /// <inheritdoc />
    public ValueTask<ChatFanOutPlanStoreResult> StoreAsync(
        ChatFanOutPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (plan.ConversationId, plan.ContentId);
            if (_plans.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(AreEquivalent(existing, plan)
                    ? ChatFanOutPlanStoreResult.Duplicate
                    : ChatFanOutPlanStoreResult.Conflict);
            }

            _plans.Add(key, plan);
            return ValueTask.FromResult(ChatFanOutPlanStoreResult.Stored);
        }
    }

    /// <inheritdoc />
    public ValueTask MarkAcceptedAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        MessageId messageId,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (conversationId, contentId);
            var plan = _plans.GetValueOrDefault(key) ??
                throw new InvalidOperationException("The fan-out plan was not found.");
            var found = false;
            var items = plan.Envelopes.Select(item =>
            {
                if (item.Envelope.MessageId != messageId)
                {
                    return item;
                }

                found = true;
                return item with { IsAccepted = true };
            }).ToArray();
            if (!found || acceptedAt < plan.SentAt)
            {
                throw new InvalidOperationException("The fan-out acceptance state is invalid.");
            }

            _plans[key] = Copy(plan, items, plan.CompletedAt);
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public ValueTask MarkCompletedAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (conversationId, contentId);
            var plan = _plans.GetValueOrDefault(key) ??
                throw new InvalidOperationException("The fan-out plan was not found.");
            if (plan.Envelopes.Any(item => !item.IsAccepted))
            {
                throw new InvalidOperationException("An incomplete fan-out plan cannot be completed.");
            }

            _plans[key] = Copy(plan, plan.Envelopes, completedAt);
            return ValueTask.CompletedTask;
        }
    }

    private static ChatFanOutPlan Copy(
        ChatFanOutPlan plan,
        IReadOnlyList<ChatEnvelopePlanItem> items,
        DateTimeOffset? completedAt) =>
        new(
            plan.ConversationId,
            plan.ContentId,
            plan.SenderUserId,
            plan.SenderDeviceId,
            plan.LocalEchoMessageId,
            plan.SentAt,
            plan.ContentHash.Span,
            items,
            completedAt);

    private static bool AreEquivalent(ChatFanOutPlan left, ChatFanOutPlan right)
    {
        if (left.ConversationId != right.ConversationId || left.ContentId != right.ContentId ||
            left.SenderUserId != right.SenderUserId || left.SenderDeviceId != right.SenderDeviceId ||
            left.LocalEchoMessageId != right.LocalEchoMessageId || left.SentAt != right.SentAt ||
            left.Envelopes.Count != right.Envelopes.Count ||
            !CryptographicOperations.FixedTimeEquals(left.ContentHash.Span, right.ContentHash.Span))
        {
            return false;
        }

        for (var index = 0; index < left.Envelopes.Count; index++)
        {
            if (!CanonicalEnvelopeEncoding.EncodeEnvelope(left.Envelopes[index].Envelope)
                .AsSpan()
                .SequenceEqual(CanonicalEnvelopeEncoding.EncodeEnvelope(right.Envelopes[index].Envelope)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Bounded logical send outcome without remote response bodies or payload data.</summary>
public sealed class ChatFanOutSendResult
{
    private ChatFanOutSendResult(
        bool succeeded,
        int acceptedCount,
        int requiredCount,
        ReceivedChatContent? localEcho)
    {
        Succeeded = succeeded;
        AcceptedCount = acceptedCount;
        RequiredCount = requiredCount;
        LocalEcho = localEcho;
    }

    /// <summary>Whether every required envelope was accepted.</summary>
    public bool Succeeded { get; }

    /// <summary>Number of recipient envelopes already accepted.</summary>
    public int AcceptedCount { get; }

    /// <summary>Total number of required recipient envelopes.</summary>
    public int RequiredCount { get; }

    /// <summary>Current-device local echo only after complete fan-out.</summary>
    public ReceivedChatContent? LocalEcho { get; }

    /// <summary>Creates a bounded incomplete result.</summary>
    public static ChatFanOutSendResult Incomplete(int acceptedCount, int requiredCount) =>
        new(false, acceptedCount, requiredCount, null);

    /// <summary>Creates a completed result and authenticated host-side local echo.</summary>
    public static ChatFanOutSendResult Complete(
        int requiredCount,
        ReceivedChatContent localEcho) =>
        new(true, requiredCount, requiredCount, localEcho ?? throw new ArgumentNullException(nameof(localEcho)));

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatFanOutSendResult(Succeeded={Succeeded}, Accepted={AcceptedCount}/{RequiredCount}, Payload=[REDACTED])";
}

/// <summary>Encrypts and submits one typed event to every active peer and sibling device.</summary>
public sealed class ChatMultiDeviceSender
{
    private readonly UserId _currentUserId;
    private readonly DeviceId _currentDeviceId;
    private readonly ChatCryptoService _crypto;
    private readonly IRecipientDeviceDirectory _directory;
    private readonly IChatTransport _transport;
    private readonly IChatFanOutPlanStore _plans;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Exception, bool> _isExpectedFailure;

    /// <summary>Creates a sender over transport-independent directory, crypto, transport and plan storage.</summary>
    public ChatMultiDeviceSender(
        UserId currentUserId,
        DeviceId currentDeviceId,
        ChatCryptoService crypto,
        IRecipientDeviceDirectory directory,
        IChatTransport transport,
        IChatFanOutPlanStore plans,
        TimeProvider? timeProvider = null,
        Func<Exception, bool>? isExpectedFailure = null)
    {
        if (currentUserId.Value == Guid.Empty || currentDeviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("The current chat session identity is invalid.");
        }

        _currentUserId = currentUserId;
        _currentDeviceId = currentDeviceId;
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isExpectedFailure = isExpectedFailure ?? (static exception =>
            exception is HttpRequestException or TimeoutException);
    }

    /// <summary>Sends or resumes one logical event while preserving stored ciphertext and message IDs.</summary>
    public async ValueTask<ChatFanOutSendResult> SendAsync(
        ConversationId conversationId,
        ChatContent content,
        CancellationToken cancellationToken = default)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        ArgumentNullException.ThrowIfNull(content);
        var contentBytes = ChatContentEncoding.Encode(content);
        var contentHash = SHA256.HashData(contentBytes);
        CryptographicOperations.ZeroMemory(contentBytes);
        try
        {
            var plan = await _plans.LoadAsync(conversationId, content.ContentId, cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                plan = await CreatePlanAsync(
                    conversationId,
                    content,
                    contentHash,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ValidateExistingPlan(plan, contentHash);
            }

            if (plan.CompletedAt.HasValue)
            {
                return ChatFanOutSendResult.Complete(plan.Envelopes.Count, CreateLocalEcho(plan, content));
            }

            foreach (var item in plan.Envelopes)
            {
                if (item.IsAccepted)
                {
                    continue;
                }

                try
                {
                    await _transport.SendAsync(item.Envelope, cancellationToken).ConfigureAwait(false);
                    await _plans.MarkAcceptedAsync(
                        plan.ConversationId,
                        plan.ContentId,
                        item.Envelope.MessageId,
                        _timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (_isExpectedFailure(exception))
                {
                    var current = await _plans.LoadAsync(
                        plan.ConversationId,
                        plan.ContentId,
                        cancellationToken).ConfigureAwait(false) ?? plan;
                    return ChatFanOutSendResult.Incomplete(
                        current.Envelopes.Count(envelope => envelope.IsAccepted),
                        current.Envelopes.Count);
                }
            }

            var completedAt = _timeProvider.GetUtcNow();
            await _plans.MarkCompletedAsync(
                plan.ConversationId,
                plan.ContentId,
                completedAt,
                cancellationToken).ConfigureAwait(false);
            return ChatFanOutSendResult.Complete(plan.Envelopes.Count, CreateLocalEcho(plan, content));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentHash);
        }
    }

    private async ValueTask<ChatFanOutPlan> CreatePlanAsync(
        ConversationId conversationId,
        ChatContent content,
        byte[] contentHash,
        CancellationToken cancellationToken)
    {
        var page = await _directory.ListConversationDevicesAsync(
            conversationId,
            maximumCount: ChatFanOutLimits.MaxRecipientDevices,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (page.NextCursor is not null || page.Items.Count is 0 or > ChatFanOutLimits.MaxRecipientDevices)
        {
            throw new InvalidOperationException("The active recipient device directory exceeds the supported bound.");
        }

        var currentSeen = false;
        var peerSeen = false;
        UserId? peerUserId = null;
        var recipients = new List<PublicDevice>(page.Items.Count);
        var deviceIds = new HashSet<DeviceId>();
        foreach (var device in page.Items)
        {
            ProtocolValidator.Validate(device);
            if (device.IsRevoked || !deviceIds.Add(device.DeviceId))
            {
                throw new InvalidOperationException("The active recipient device directory is invalid.");
            }

            if (device.UserId == _currentUserId)
            {
                if (device.DeviceId == _currentDeviceId)
                {
                    currentSeen = true;
                    continue;
                }
            }
            else
            {
                if (peerUserId.HasValue && peerUserId.Value != device.UserId)
                {
                    throw new InvalidOperationException("The device directory contains unrelated users.");
                }

                peerUserId = device.UserId;
                peerSeen = true;
            }

            recipients.Add(device);
        }

        if (!currentSeen || !peerSeen || recipients.Count == 0)
        {
            throw new InvalidOperationException("The conversation has no valid active recipient set.");
        }

        var sentAt = _timeProvider.GetUtcNow();
        var items = new ChatEnvelopePlanItem[recipients.Count];
        for (var index = 0; index < recipients.Count; index++)
        {
            var envelope = await _crypto.EncryptContentAsync(
                content,
                conversationId,
                MessageId.New(),
                _currentDeviceId,
                recipients[index],
                sentAt,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            items[index] = new ChatEnvelopePlanItem(envelope, false);
        }

        var plan = new ChatFanOutPlan(
            conversationId,
            content.ContentId,
            _currentUserId,
            _currentDeviceId,
            MessageId.New(),
            sentAt,
            contentHash,
            items);
        var stored = await _plans.StoreAsync(plan, cancellationToken).ConfigureAwait(false);
        if (stored == ChatFanOutPlanStoreResult.Conflict)
        {
            throw new InvalidOperationException("The logical chat send conflicts with an existing plan.");
        }

        if (stored == ChatFanOutPlanStoreResult.Duplicate)
        {
            var existing = await _plans.LoadAsync(conversationId, content.ContentId, cancellationToken)
                .ConfigureAwait(false) ??
                throw new InvalidOperationException("The fan-out plan store returned an invalid result.");
            ValidateExistingPlan(existing, contentHash);
            return existing;
        }

        return plan;
    }

    private void ValidateExistingPlan(ChatFanOutPlan plan, ReadOnlySpan<byte> contentHash)
    {
        if (plan.SenderUserId != _currentUserId || plan.SenderDeviceId != _currentDeviceId ||
            !CryptographicOperations.FixedTimeEquals(plan.ContentHash.Span, contentHash))
        {
            throw new InvalidOperationException("The logical chat send conflicts with an existing plan.");
        }
    }

    private static ReceivedChatContent CreateLocalEcho(ChatFanOutPlan plan, ChatContent content) =>
        new(
            plan.LocalEchoMessageId,
            plan.ConversationId,
            plan.SenderUserId,
            plan.SenderDeviceId,
            plan.SentAt,
            content);
}
