using System.Text;
using Confluent.Kafka;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server.Kafka;

namespace Skopka.Chat.Server.Tests;

public sealed class ServerEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encrypted_envelope_event_v1_has_stable_metadata_only_json()
    {
        var envelope = Envelope();
        var eventId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var message = ChatServerEventFactory.EncryptedEnvelopeAccepted(eventId, envelope, Now.AddSeconds(1));
        var json = Encoding.UTF8.GetString(message.Payload.Span);

        Assert.Equal(ChatServerEventTypes.EncryptedEnvelopeAccepted, message.EventType);
        Assert.Equal(1, message.EventVersion);
        Assert.Equal(envelope.RecipientDeviceId.Value.ToString("D"), message.PartitionKey);
        Assert.Equal(
            "{\"eventId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"messageId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"conversationId\":\"10213243-5465-7687-98a9-bacbdcedfe0f\",\"senderDeviceId\":\"11223344-5566-7788-99aa-bbccddeeff00\",\"recipientDeviceId\":\"ffeeddcc-bbaa-9988-7766-554433221100\",\"protocolVersion\":1,\"sentAt\":\"2026-09-04T12:00:00+00:00\",\"expiresAt\":\"2026-09-05T12:00:00+00:00\",\"acceptedAt\":\"2026-09-04T12:00:01+00:00\"}",
            json);
        Assert.DoesNotContain("cipher", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plain", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Domain_server_has_no_kafka_dependency()
    {
        var references = typeof(ChatServerEngine).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name?.Contains("Kafka", StringComparison.OrdinalIgnoreCase) == true ||
            reference.Name?.Contains("Confluent", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(typeof(KafkaChatServerEventPublisher).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Confluent.Kafka");
    }

    [Fact]
    public void Kafka_adapter_disables_topic_creation_and_maps_stable_key_headers_and_payload()
    {
        var options = new KafkaChatServerEventOptions { BootstrapServers = "kafka:9092" };
        var message = ChatServerEventFactory.EncryptedEnvelopeAccepted(Guid.NewGuid(), Envelope(), Now);

        var config = KafkaChatServerEventPublisher.CreateProducerConfig(options);
        var request = KafkaChatServerEventPublisher.CreatePublishRequest(options, message);

        Assert.False(config.AllowAutoCreateTopics);
        Assert.True(config.EnableIdempotence);
        Assert.Equal(Acks.All, config.Acks);
        Assert.Equal(KafkaChatServerEventTopics.EncryptedEnvelopeAcceptedV1, request.Topic);
        Assert.Equal(message.PartitionKey, request.Message.Key);
        Assert.Equal(message.Payload.ToArray(), request.Message.Value);
        Assert.Equal(message.EventId.ToString("D"),
            Encoding.UTF8.GetString(request.Message.Headers.GetLastBytes("skopka-event-id")));
        Assert.Equal(ChatServerEventTypes.EncryptedEnvelopeAccepted,
            Encoding.UTF8.GetString(request.Message.Headers.GetLastBytes("skopka-event-type")));
        Assert.Equal("1", Encoding.ASCII.GetString(
            request.Message.Headers.GetLastBytes("skopka-event-version")));
    }

    [Fact]
    public async Task Publisher_failure_reschedules_same_event_id_with_bounded_backoff_then_retries()
    {
        var clock = new ManualTimeProvider(Now);
        var message = ChatServerEventFactory.EncryptedEnvelopeAccepted(Guid.NewGuid(), Envelope(), Now);
        var outbox = new FakeOutbox(message);
        var publisher = new FakePublisher(failures: 1);
        var dispatcher = new ChatServerEventDispatcher(
            outbox,
            publisher,
            clock,
            new ChatServerEventDispatchOptions
            {
                InitialBackoff = TimeSpan.FromSeconds(1),
                MaximumBackoff = TimeSpan.FromSeconds(1)
            });

        var failed = await dispatcher.DispatchBatchAsync("worker-a");

        Assert.Equal(new ChatServerEventDispatchResult(1, 0, 1), failed);
        Assert.Equal(message.EventId, outbox.Message.EventId);
        Assert.InRange(outbox.NextAttemptAt - Now, TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250));

        clock.Advance(TimeSpan.FromSeconds(2));
        var retried = await dispatcher.DispatchBatchAsync("worker-b");

        Assert.Equal(new ChatServerEventDispatchResult(1, 1, 0), retried);
        Assert.Equal(2, publisher.Attempts);
        Assert.True(outbox.Published);
    }

    [Fact]
    public async Task Controlled_stop_leaves_claim_for_lease_expiry_without_marking_or_rescheduling()
    {
        var clock = new ManualTimeProvider(Now);
        var outbox = new FakeOutbox(
            ChatServerEventFactory.EncryptedEnvelopeAccepted(Guid.NewGuid(), Envelope(), Now));
        var publisher = new CancellingPublisher();
        var dispatcher = new ChatServerEventDispatcher(outbox, publisher, clock);
        using var cancellation = new CancellationTokenSource();
        var dispatch = dispatcher.DispatchBatchAsync("worker-a", cancellation.Token).AsTask();
        await publisher.Started.Task;

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.False(outbox.Published);
        Assert.Equal(default, outbox.NextAttemptAt);
    }

    private static EncryptedEnvelope Envelope() => new(
        ProtocolVersions.V1,
        new MessageId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
        new ConversationId(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f")),
        new DeviceId(Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00")),
        new DeviceId(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")),
        new KeyId(Guid.Parse("12345678-90ab-cdef-1234-567890abcdef")),
        new KeyId(Guid.Parse("fedcba09-8765-4321-fedc-ba0987654321")),
        Now,
        Now.AddDays(1),
        new byte[32],
        new byte[24],
        [0x53, 0x45, 0x43, 0x52, 0x45, 0x54],
        new byte[16],
        new byte[64]);

    private sealed class FakeOutbox(ChatServerOutboxMessage message) : IChatServerEventOutbox
    {
        private int _attemptCount;
        public ChatServerOutboxMessage Message { get; } = message;
        public DateTimeOffset NextAttemptAt { get; private set; }
        public bool Published { get; private set; }

        public ValueTask<IReadOnlyList<ClaimedChatServerEvent>> ClaimPendingAsync(
            string leaseOwner,
            int maximumCount,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ClaimedChatServerEvent> result = Published || (NextAttemptAt != default && NextAttemptAt > now)
                ? []
                : [new ClaimedChatServerEvent(Message, ++_attemptCount)];
            return ValueTask.FromResult(result);
        }

        public ValueTask<bool> MarkPublishedAsync(
            Guid eventId,
            string leaseOwner,
            DateTimeOffset publishedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Published = eventId == Message.EventId;
            return ValueTask.FromResult(Published);
        }

        public ValueTask<bool> RescheduleAsync(
            Guid eventId,
            string leaseOwner,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (eventId != Message.EventId)
            {
                return ValueTask.FromResult(false);
            }

            NextAttemptAt = nextAttemptAt;
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> DeletePublishedBeforeAsync(
            DateTimeOffset cutoff,
            int maximumCount,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }

    private sealed class FakePublisher(int failures) : IChatServerEventPublisher
    {
        private int _remainingFailures = failures;
        public int Attempts { get; private set; }

        public ValueTask PublishAsync(
            ChatServerOutboxMessage message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (_remainingFailures-- > 0)
            {
                throw new InvalidOperationException("Synthetic broker failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingPublisher : IChatServerEventPublisher
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PublishAsync(
            ChatServerOutboxMessage message,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
