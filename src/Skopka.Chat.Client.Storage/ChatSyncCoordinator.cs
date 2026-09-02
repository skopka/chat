using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage;

/// <summary>Raised when a delivery cannot safely proceed through the local synchronization pipeline.</summary>
public sealed class ChatSynchronizationException : Exception
{
    /// <summary>Creates a content-free synchronization failure.</summary>
    public ChatSynchronizationException(string message) : base(message)
    {
    }
}

/// <summary>Counts one completed bounded synchronization batch.</summary>
public sealed record ChatSyncBatchResult(int Received, int Stored, int Duplicates, int Acknowledged);

/// <summary>
/// Serializes polling and processes each delivery as authenticate/decrypt, durable store, idempotent apply, acknowledge.
/// </summary>
public sealed class ChatSyncCoordinator : IDisposable, IChatLocalEchoCommitter
{
    private readonly IChatTransport _transport;
    private readonly ChatCryptoService _crypto;
    private readonly IChatEventStore _events;
    private readonly IChatEventApplier _applier;
    private readonly DeviceId _recipientDeviceId;
    private readonly TimeProvider _timeProvider;
    private readonly bool _restoreAllHistory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restored;
    private bool _disposed;

    /// <summary>Creates a coordinator for exactly one local recipient device.</summary>
    public ChatSyncCoordinator(
        IChatTransport transport,
        ChatCryptoService crypto,
        IChatEventStore events,
        IChatEventApplier applier,
        DeviceId recipientDeviceId,
        TimeProvider? timeProvider = null,
        bool restoreAllHistory = true)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        if (recipientDeviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("Recipient device ID must not be empty.", nameof(recipientDeviceId));
        }

        _recipientDeviceId = recipientDeviceId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _restoreAllHistory = restoreAllHistory;
    }

    /// <summary>Restores all committed events into the idempotent applier exactly once per coordinator instance.</summary>
    public async ValueTask<int> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Processes and acknowledges one bounded transport batch.</summary>
    /// <remarks>
    /// An event-store conflict, authentication failure, applier failure or acknowledgement failure stops the batch.
    /// Calling this method again safely retries an unacknowledged exact delivery.
    /// </remarks>
    public async ValueTask<ChatSyncBatchResult> SynchronizeAsync(
        int maximumCount = ProtocolLimits.MaxDeliveryBatch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumCount is < 1 or > ProtocolLimits.MaxDeliveryBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
            var pending = await _transport.ReceiveAsync(
                _recipientDeviceId,
                maximumCount,
                cancellationToken).ConfigureAwait(false);
            if (pending is null || pending.Count > maximumCount)
            {
                throw new ChatSynchronizationException("The chat delivery batch was invalid.");
            }

            var stored = 0;
            var duplicates = 0;
            var acknowledged = 0;
            foreach (var transportDelivery in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = transportDelivery?.Envelope ??
                    throw new ChatSynchronizationException("The chat delivery batch was invalid.");
                if (envelope.RecipientDeviceId != _recipientDeviceId)
                {
                    throw new ChatSynchronizationException("The chat delivery recipient was invalid.");
                }

                var sender = await _transport.GetDeviceAsync(
                    envelope.SenderDeviceId,
                    cancellationToken).ConfigureAwait(false) ??
                    throw new ChatSynchronizationException("The authenticated sender device was unavailable.");
                var content = await _crypto.DecryptContentAsync(envelope, sender, cancellationToken).ConfigureAwait(false);
                var delivery = new ReceivedChatContent(
                    envelope.MessageId,
                    envelope.ConversationId,
                    sender.UserId,
                    envelope.SenderDeviceId,
                    envelope.SentAt,
                    content);
                var storeResult = await StoreAndApplyAsync(delivery, cancellationToken).ConfigureAwait(false);
                if (storeResult == ChatEventStoreResult.Stored)
                {
                    stored++;
                }
                else
                {
                    duplicates++;
                }

                await _transport.AcknowledgeAsync(
                    _recipientDeviceId,
                    envelope.MessageId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                acknowledged++;
            }

            return new ChatSyncBatchResult(pending.Count, stored, duplicates, acknowledged);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Durably stores and applies a host-authenticated local echo without transport acknowledgement.</summary>
    /// <remarks>
    /// The echo must have been created by the host's successful sender for this coordinator's local device.
    /// This covers outgoing history when the server does not deliver the sender's own envelope back to that device.
    /// </remarks>
    public async ValueTask<ChatEventStoreResult> CommitLocalEchoAsync(
        ReceivedChatContent delivery,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.SenderDeviceId != _recipientDeviceId)
        {
            throw new ArgumentException("The local echo sender does not match this coordinator's device.", nameof(delivery));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
            return await StoreAndApplyAsync(delivery, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    async ValueTask IChatLocalEchoCommitter.CommitLocalEchoAsync(
        ReceivedChatContent delivery,
        CancellationToken cancellationToken)
    {
        await CommitLocalEchoAsync(delivery, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async ValueTask<int> EnsureRestoredAsync(CancellationToken cancellationToken)
    {
        if (_restored)
        {
            return 0;
        }

        if (!_restoreAllHistory)
        {
            _restored = true;
            return 0;
        }

        var restored = 0;
        await foreach (var delivery in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _applier.ApplyAsync(delivery, cancellationToken).ConfigureAwait(false);
            restored++;
        }

        _restored = true;
        return restored;
    }

    private async ValueTask<ChatEventStoreResult> StoreAndApplyAsync(
        ReceivedChatContent delivery,
        CancellationToken cancellationToken)
    {
        var storeResult = await _events.StoreAsync(delivery, cancellationToken).ConfigureAwait(false);
        if (storeResult == ChatEventStoreResult.Conflict)
        {
            throw new ChatSynchronizationException("The delivery message ID conflicts with protected local history.");
        }

        if (storeResult is not ChatEventStoreResult.Stored and not ChatEventStoreResult.Duplicate)
        {
            throw new ChatSynchronizationException("The local event store returned an invalid result.");
        }

        await _applier.ApplyAsync(delivery, cancellationToken).ConfigureAwait(false);
        return storeResult;
    }
}
