using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Stable identifiers and bounds for server integration events.</summary>
public static class ChatServerEventTypes
{
    /// <summary>One encrypted envelope became durable and available for ordinary history delivery.</summary>
    public const string EncryptedEnvelopeAccepted = "skopka.chat.encrypted-envelope-accepted";

    /// <summary>Current schema version of <see cref="EncryptedEnvelopeAcceptedEventV1"/>.</summary>
    public const int EncryptedEnvelopeAcceptedVersion = 1;

    /// <summary>Maximum serialized event payload accepted by the transport-neutral outbox boundary.</summary>
    public const int MaxPayloadBytes = 16 * 1024;

    /// <summary>Maximum event type or partition-key length.</summary>
    public const int MaxIdentifierLength = 128;
}

/// <summary>
/// Version-one notification that a recipient-specific encrypted envelope was committed.
/// It deliberately contains no ciphertext, plaintext, attachment key, or device key material.
/// </summary>
public sealed record EncryptedEnvelopeAcceptedEventV1
{
    /// <summary>Creates a validated version-one event.</summary>
    public EncryptedEnvelopeAcceptedEventV1(
        Guid eventId,
        Guid messageId,
        Guid conversationId,
        Guid senderDeviceId,
        Guid recipientDeviceId,
        int protocolVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? expiresAt,
        DateTimeOffset acceptedAt)
    {
        if (eventId == Guid.Empty || messageId == Guid.Empty || conversationId == Guid.Empty ||
            senderDeviceId == Guid.Empty || recipientDeviceId == Guid.Empty ||
            protocolVersion <= 0 || sentAt == default || acceptedAt == default ||
            (expiresAt is not null && expiresAt <= sentAt))
        {
            throw new ArgumentException("The encrypted-envelope event is invalid.");
        }

        EventId = eventId;
        MessageId = messageId;
        ConversationId = conversationId;
        SenderDeviceId = senderDeviceId;
        RecipientDeviceId = recipientDeviceId;
        ProtocolVersion = protocolVersion;
        SentAt = sentAt;
        ExpiresAt = expiresAt;
        AcceptedAt = acceptedAt;
    }

    /// <summary>Idempotency key for every delivery of this integration event.</summary>
    public Guid EventId { get; }

    /// <summary>Recipient-specific immutable envelope identifier.</summary>
    public Guid MessageId { get; }

    /// <summary>Conversation containing the encrypted envelope.</summary>
    public Guid ConversationId { get; }

    /// <summary>Public sender device identifier.</summary>
    public Guid SenderDeviceId { get; }

    /// <summary>Public recipient device identifier and Kafka partition key.</summary>
    public Guid RecipientDeviceId { get; }

    /// <summary>Outer encrypted-envelope protocol version.</summary>
    public int ProtocolVersion { get; }

    /// <summary>Sender-provided timestamp already visible to the server.</summary>
    public DateTimeOffset SentAt { get; }

    /// <summary>Optional server-visible retention deadline.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Timestamp at which PostgreSQL accepted the envelope.</summary>
    public DateTimeOffset AcceptedAt { get; }
}

/// <summary>Exact transport-neutral event bytes stored before any broker interaction.</summary>
public sealed class ChatServerOutboxMessage
{
    private readonly byte[] _payload;

    /// <summary>Creates a bounded immutable outbox message.</summary>
    public ChatServerOutboxMessage(
        Guid eventId,
        string eventType,
        int eventVersion,
        DateTimeOffset occurredAt,
        string partitionKey,
        ReadOnlySpan<byte> payload)
    {
        if (eventId == Guid.Empty || occurredAt == default || eventVersion <= 0 ||
            string.IsNullOrWhiteSpace(eventType) || eventType.Length > ChatServerEventTypes.MaxIdentifierLength ||
            string.IsNullOrWhiteSpace(partitionKey) || partitionKey.Length > ChatServerEventTypes.MaxIdentifierLength ||
            payload.IsEmpty || payload.Length > ChatServerEventTypes.MaxPayloadBytes)
        {
            throw new ArgumentException("The server event is invalid.");
        }

        EventId = eventId;
        EventType = eventType;
        EventVersion = eventVersion;
        OccurredAt = occurredAt;
        PartitionKey = partitionKey;
        _payload = payload.ToArray();
    }

    /// <summary>Idempotency key that consumers must persist before applying side effects.</summary>
    public Guid EventId { get; }

    /// <summary>Stable event type independent of a broker topic name.</summary>
    public string EventType { get; }

    /// <summary>Schema version for <see cref="Payload"/>.</summary>
    public int EventVersion { get; }

    /// <summary>Time of the domain commit represented by this message.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Opaque stable ordering key for transports that support partitioning.</summary>
    public string PartitionKey { get; }

    /// <summary>Exact versioned bytes to publish; they must never contain plaintext or private keys.</summary>
    public ReadOnlyMemory<byte> Payload => _payload.ToArray();
}

/// <summary>An outbox message held under a finite cooperative publisher lease.</summary>
public sealed record ClaimedChatServerEvent
{
    /// <summary>Creates a validated claim.</summary>
    public ClaimedChatServerEvent(ChatServerOutboxMessage message, int attemptCount)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptCount);
        AttemptCount = attemptCount;
    }

    /// <summary>Exact event to publish.</summary>
    public ChatServerOutboxMessage Message { get; }

    /// <summary>One-based number of times this event has been claimed.</summary>
    public int AttemptCount { get; }
}

/// <summary>Durable lease-based event outbox independent of PostgreSQL and Kafka.</summary>
public interface IChatServerEventOutbox
{
    /// <summary>Claims due messages in stable order; expired leases are eligible for redelivery.</summary>
    ValueTask<IReadOnlyList<ClaimedChatServerEvent>> ClaimPendingAsync(
        string leaseOwner,
        int maximumCount,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a published event complete only while the caller still owns its lease.</summary>
    ValueTask<bool> MarkPublishedAsync(
        Guid eventId,
        string leaseOwner,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a failed event for a later attempt only while the caller owns its lease.</summary>
    ValueTask<bool> RescheduleAsync(
        Guid eventId,
        string leaseOwner,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a bounded set of old completed rows; pending events are never removed.</summary>
    ValueTask<int> DeletePublishedBeforeAsync(
        DateTimeOffset cutoff,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

/// <summary>Broker-independent publisher of exact outbox bytes.</summary>
public interface IChatServerEventPublisher
{
    /// <summary>Publishes one event. Success means the broker acknowledged it, not that a consumer applied it.</summary>
    ValueTask PublishAsync(ChatServerOutboxMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Bounds and retry policy for one server-event dispatcher.</summary>
public sealed class ChatServerEventDispatchOptions
{
    /// <summary>Maximum messages claimed in one database transaction.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Finite ownership lease; it must exceed the broker delivery timeout.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>First failed-publish delay.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for exponential backoff.</summary>
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Delay after an empty batch in a hosted polling loop.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Retention of completed outbox audit rows before bounded cleanup.</summary>
    public TimeSpan PublishedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Rejects settings outside the bounded dispatcher operating envelope.</summary>
    public void Validate()
    {
        if (BatchSize is < 1 or > 500 ||
            LeaseDuration < TimeSpan.FromSeconds(5) || LeaseDuration > TimeSpan.FromMinutes(10) ||
            InitialBackoff < TimeSpan.FromMilliseconds(100) || InitialBackoff > TimeSpan.FromMinutes(5) ||
            MaximumBackoff < InitialBackoff || MaximumBackoff > TimeSpan.FromHours(1) ||
            IdleDelay < TimeSpan.FromMilliseconds(100) || IdleDelay > TimeSpan.FromMinutes(1) ||
            PublishedRetention < TimeSpan.FromHours(1) || PublishedRetention > TimeSpan.FromDays(90))
        {
            throw new ArgumentException("The server event dispatch options are invalid.");
        }
    }
}

/// <summary>Observable outcome of one bounded dispatch pass.</summary>
public readonly record struct ChatServerEventDispatchResult(int Claimed, int Published, int Rescheduled);

/// <summary>Claims, publishes, and completes a bounded outbox batch with at-least-once semantics.</summary>
public sealed class ChatServerEventDispatcher
{
    private static readonly Meter Telemetry = new("Skopka.Chat.Server.Events", "1.0");
    private static readonly ActivitySource Activities = new("Skopka.Chat.Server.Events");
    private static readonly Counter<long> ClaimedCounter = Telemetry.CreateCounter<long>("skopka.chat.server.events.claimed");
    private static readonly Counter<long> PublishedCounter = Telemetry.CreateCounter<long>("skopka.chat.server.events.published");
    private static readonly Counter<long> RetriedCounter = Telemetry.CreateCounter<long>("skopka.chat.server.events.retried");
    private static readonly Histogram<double> LagSeconds = Telemetry.CreateHistogram<double>("skopka.chat.server.events.publish_lag", "s");

    private readonly IChatServerEventOutbox _outbox;
    private readonly IChatServerEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ChatServerEventDispatchOptions _options;

    /// <summary>Creates a dispatcher over host-selected durable storage and broker transport.</summary>
    public ChatServerEventDispatcher(
        IChatServerEventOutbox outbox,
        IChatServerEventPublisher publisher,
        TimeProvider timeProvider,
        ChatServerEventDispatchOptions? options = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? new ChatServerEventDispatchOptions();
        _options.Validate();
    }

    /// <summary>Dispatches at most one configured batch for the given bounded lease-owner identifier.</summary>
    public async ValueTask<ChatServerEventDispatchResult> DispatchBatchAsync(
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > ChatServerEventTypes.MaxIdentifierLength)
        {
            throw new ArgumentException("The lease owner is invalid.", nameof(leaseOwner));
        }

        var now = _timeProvider.GetUtcNow();
        var claimed = await _outbox.ClaimPendingAsync(
            leaseOwner,
            _options.BatchSize,
            now,
            _options.LeaseDuration,
            cancellationToken).ConfigureAwait(false);
        ClaimedCounter.Add(claimed.Count);
        var published = 0;
        var rescheduled = 0;
        foreach (var lease in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var activity = Activities.StartActivity("publish", ActivityKind.Producer);
            activity?.SetTag("messaging.message.id", lease.Message.EventId.ToString("D"));
            activity?.SetTag("messaging.operation.type", "publish");
            activity?.SetTag("skopka.event.type", lease.Message.EventType);
            try
            {
                await _publisher.PublishAsync(lease.Message, cancellationToken).ConfigureAwait(false);
                var completedAt = _timeProvider.GetUtcNow();
                if (await _outbox.MarkPublishedAsync(
                    lease.Message.EventId,
                    leaseOwner,
                    completedAt,
                    cancellationToken).ConfigureAwait(false))
                {
                    published++;
                    PublishedCounter.Add(1);
                    LagSeconds.Record(Math.Max(0, (completedAt - lease.Message.OccurredAt).TotalSeconds));
                }
            }
            catch (Exception error) when (
                error is not OutOfMemoryException and not StackOverflowException &&
                !cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "publish-failed");
                var failedAt = _timeProvider.GetUtcNow();
                var nextAttemptAt = failedAt + ComputeBackoff(lease.Message.EventId, lease.AttemptCount);
                if (await _outbox.RescheduleAsync(
                    lease.Message.EventId,
                    leaseOwner,
                    failedAt,
                    nextAttemptAt,
                    cancellationToken).ConfigureAwait(false))
                {
                    rescheduled++;
                    RetriedCounter.Add(1);
                }
            }
        }

        return new ChatServerEventDispatchResult(claimed.Count, published, rescheduled);
    }

    private TimeSpan ComputeBackoff(Guid eventId, int attemptCount)
    {
        var exponent = Math.Min(attemptCount - 1, 30);
        var multiplier = Math.Pow(2, exponent);
        var cappedMilliseconds = Math.Min(
            _options.MaximumBackoff.TotalMilliseconds,
            _options.InitialBackoff.TotalMilliseconds * multiplier);
        Span<byte> eventBytes = stackalloc byte[16];
        eventId.TryWriteBytes(eventBytes);
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(eventBytes) ^ ((uint)attemptCount * 2654435761U);
        var jitter = 0.75 + ((double)seed / uint.MaxValue * 0.5);
        return TimeSpan.FromMilliseconds(Math.Max(1, cappedMilliseconds * jitter));
    }
}

/// <summary>Creates the versioned bytes written beside an accepted encrypted envelope.</summary>
public static class ChatServerEventFactory
{
    /// <summary>Creates an encrypted-envelope acceptance event with no secret or content fields.</summary>
    public static ChatServerOutboxMessage EncryptedEnvelopeAccepted(
        Guid eventId,
        EncryptedEnvelope envelope,
        DateTimeOffset acceptedAt)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = new EncryptedEnvelopeAcceptedEventV1(
            eventId,
            envelope.MessageId.Value,
            envelope.ConversationId.Value,
            envelope.SenderDeviceId.Value,
            envelope.RecipientDeviceId.Value,
            envelope.ProtocolVersion,
            envelope.SentAt,
            envelope.ExpiresAt,
            acceptedAt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            ChatServerEventJsonContext.Default.EncryptedEnvelopeAcceptedEventV1);
        return new ChatServerOutboxMessage(
            eventId,
            ChatServerEventTypes.EncryptedEnvelopeAccepted,
            ChatServerEventTypes.EncryptedEnvelopeAcceptedVersion,
            acceptedAt,
            envelope.RecipientDeviceId.Value.ToString("D"),
            bytes);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EncryptedEnvelopeAcceptedEventV1))]
internal sealed partial class ChatServerEventJsonContext : JsonSerializerContext;
