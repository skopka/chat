using System.Text;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots;

/// <summary>Owner-hosted client endpoint. Never register this runtime in the main chat server.</summary>
public sealed class ChatBotRuntime : IDisposable
{
    private readonly DeviceId _deviceId;
    private readonly IChatTransport _transport;
    private readonly ChatCryptoService _crypto;
    private readonly ChatMultiDeviceSender _sender;
    private readonly IChatBotInbox _inbox;
    private readonly IChatBotConsentProvider _consent;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates one bot endpoint with mandatory live consent and durable inbox/outbox adapters.</summary>
    public ChatBotRuntime(ChatBotProfile profile, DeviceId deviceId, IChatTransport transport,
        ChatCryptoService crypto, IRecipientDeviceDirectory directory, IChatFanOutPlanStore outbox,
        IChatBotInbox inbox, IChatBotConsentProvider consent, TimeProvider? timeProvider = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (deviceId.Value == Guid.Empty) { throw new ArgumentException("The bot device is invalid.", nameof(deviceId)); }
        _deviceId = deviceId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _time = timeProvider ?? TimeProvider.System;
        _sender = new(profile.BotUserId, deviceId, crypto, directory, transport, outbox, _time);
    }

    /// <summary>Trusted configured operator disclosure.</summary>
    public ChatBotProfile Profile { get; }

    /// <summary>Authenticates, durably records/suppresses, then acknowledges one bounded chat batch.</summary>
    public async ValueTask<int> SynchronizeAsync(int maximumCount = ChatBotLimits.MaxUpdates, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCount(maximumCount);
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = await _transport.ReceiveAsync(_deviceId, maximumCount, cancellationToken).ConfigureAwait(false);
            if (batch is null || batch.Count > maximumCount) { throw new ChatBotException(); }
            foreach (var item in batch)
            {
                var envelope = item?.Envelope;
                if (envelope is null || envelope.RecipientDeviceId != _deviceId) { throw new ChatBotException(); }
                var sender = await _transport.GetDeviceAsync(envelope.SenderDeviceId, cancellationToken).ConfigureAwait(false)
                    ?? throw new ChatBotException();
                var content = await _crypto.DecryptContentAsync(envelope, sender, cancellationToken).ConfigureAwait(false);
                var delivery = new ReceivedChatContent(envelope.MessageId, envelope.ConversationId, sender.UserId,
                    sender.DeviceId, envelope.SentAt, content);
                var grant = await GetGrantAsync(delivery.ConversationId, cancellationToken).ConfigureAwait(false);
                var accepted = grant?.UserId == sender.UserId && sender.UserId != Profile.BotUserId &&
                    content is ChatTextContent text && IsSupportedText(text.Text);
                var result = await _inbox.StoreAsync(delivery, accepted ? grant!.GrantId : null, cancellationToken).ConfigureAwait(false);
                RequireStored(result);
                await _transport.AcknowledgeAsync(_deviceId, envelope.MessageId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
            return batch.Count;
        }
        finally { _pollGate.Release(); }
    }

    /// <summary>Reads pending updates with live consent checks; polling does not acknowledge processing.</summary>
    public async ValueTask<IReadOnlyList<ChatBotUpdate>> GetUpdatesAsync(int maximumCount = ChatBotLimits.MaxUpdates,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCount(maximumCount);
        var result = new List<ChatBotUpdate>();
        long after = 0;
        // Bound work per request while allowing suppressed prefixes to drain durably on subsequent polls.
        for (var page = 0; page < 10 && result.Count < maximumCount; page++)
        {
            var updates = await _inbox.ReadAsync(after, maximumCount - result.Count, cancellationToken).ConfigureAwait(false);
            if (updates.Count > maximumCount - result.Count) { throw new ChatBotException(); }
            foreach (var update in updates)
            {
                if (update.UpdateId <= after) { throw new ChatBotException(); }
                after = update.UpdateId;
                var grant = await GetGrantAsync(update.ConversationId, cancellationToken).ConfigureAwait(false);
                if (grant?.GrantId == update.GrantId && grant.UserId == update.SenderUserId)
                {
                    result.Add(update);
                }
                else
                {
                    await _inbox.AcknowledgeAsync(update.UpdateId, cancellationToken).ConfigureAwait(false);
                }
            }
            if (updates.Count == 0) { break; }
        }
        return result;
    }

    /// <summary>Explicit bot-processing acknowledgement; already completed updates are idempotent.</summary>
    public ValueTask AcknowledgeUpdateAsync(long updateId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(updateId);
        return _inbox.AcknowledgeAsync(updateId, cancellationToken);
    }

    /// <summary>Sends only to an actively consenting conversation; reuse requestId for an exact retry.</summary>
    public async ValueTask<ChatFanOutSendResult> SendMessageAsync(ConversationId conversationId, Guid requestId,
        string text, ChatContentId? replyToContentId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (conversationId.Value == Guid.Empty || requestId == Guid.Empty || !IsSupportedText(text))
        {
            throw new ArgumentException("The bot message is invalid.");
        }
        var content = new ChatTextContent(new(requestId), text, replyToContentId);
        var grant = await GetGrantAsync(conversationId, cancellationToken).ConfigureAwait(false) ?? throw new ChatBotException();
        RequireStored(await _inbox.ReserveSendAsync(conversationId, grant.GrantId, content, cancellationToken).ConfigureAwait(false));
        // The durable fan-out plan preserves recipient IDs/ciphertext on partial acceptance and restart.
        return await _sender.SendAsync(conversationId, content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _pollGate.Dispose();
    }

    private async ValueTask<ChatBotConsent?> GetGrantAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        var grant = await _consent.GetConsentAsync(conversationId, cancellationToken).ConfigureAwait(false);
        return grant?.Allows(Profile, conversationId, _time.GetUtcNow()) == true ? grant : null;
    }

    private static void RequireStored(ChatBotStoreResult result)
    {
        if (result is not ChatBotStoreResult.Stored and not ChatBotStoreResult.Duplicate) { throw new ChatBotException(); }
    }

    private static void ValidateCount(int count)
    {
        if (count is < 1 or > ChatBotLimits.MaxUpdates) { throw new ArgumentOutOfRangeException(nameof(count)); }
    }

    private static bool IsSupportedText(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= ChatBotLimits.MaxTextUtf8Bytes && Encoding.UTF8.GetByteCount(text) <= ChatBotLimits.MaxTextUtf8Bytes;
}
