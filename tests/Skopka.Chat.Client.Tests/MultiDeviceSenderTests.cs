using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Tests;

public sealed class MultiDeviceSenderTests
{
    [Fact]
    public async Task Partial_retry_reuses_message_ids_and_ciphertext_for_peer_and_sibling_devices()
    {
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var alice = UserId.New();
        var bob = UserId.New();
        var keys = new InMemoryDeviceKeyStore();
        var identities = new DeviceIdentityService(keys);
        var current = await identities.CreateAsync(alice, DeviceId.New(), now);
        var sibling = await identities.CreateAsync(alice, DeviceId.New(), now);
        var bobOne = await identities.CreateAsync(bob, DeviceId.New(), now);
        var bobTwo = await identities.CreateAsync(bob, DeviceId.New(), now);
        var directory = new FixedDirectory([current, sibling, bobOne, bobTwo]);
        var transport = new RecordingTransport { FailAttempt = 2 };
        var plans = new InMemoryChatFanOutPlanStore();
        var sender = new ChatMultiDeviceSender(
            alice,
            current.DeviceId,
            new ChatCryptoService(keys),
            directory,
            transport,
            plans,
            new FixedTimeProvider(now));
        var content = new ChatTextContent(ChatContentId.New(), "fan-out");

        var partial = await sender.SendAsync(ConversationId.New(), content);
        var failedEnvelope = transport.Attempts[1];
        var failedBytes = CanonicalEnvelopeEncoding.EncodeEnvelope(failedEnvelope);
        transport.FailAttempt = null;
        var complete = await sender.SendAsync(failedEnvelope.ConversationId, content);

        Assert.False(partial.Succeeded);
        Assert.Equal(1, partial.AcceptedCount);
        Assert.Equal(3, partial.RequiredCount);
        Assert.True(complete.Succeeded);
        Assert.Equal(content.ContentId, complete.LocalEcho!.Content.ContentId);
        Assert.Equal(current.DeviceId, complete.LocalEcho.SenderDeviceId);
        Assert.Equal(4, transport.Attempts.Count);
        Assert.Equal(failedEnvelope.MessageId, transport.Attempts[2].MessageId);
        Assert.Equal(failedBytes, CanonicalEnvelopeEncoding.EncodeEnvelope(transport.Attempts[2]));
        Assert.Equal(3, transport.Attempts.Select(static item => item.RecipientDeviceId).Distinct().Count());
        Assert.DoesNotContain(current.DeviceId, transport.Attempts.Select(static item => item.RecipientDeviceId));
    }

    [Fact]
    public async Task Cancellation_is_propagated_and_does_not_become_incomplete_result()
    {
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var alice = UserId.New();
        var bob = UserId.New();
        var keys = new InMemoryDeviceKeyStore();
        var identities = new DeviceIdentityService(keys);
        var current = await identities.CreateAsync(alice, DeviceId.New(), now);
        var peer = await identities.CreateAsync(bob, DeviceId.New(), now);
        var sender = new ChatMultiDeviceSender(
            alice,
            current.DeviceId,
            new ChatCryptoService(keys),
            new FixedDirectory([current, peer]),
            new CancellingTransport(),
            new InMemoryChatFanOutPlanStore(),
            new FixedTimeProvider(now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sender.SendAsync(ConversationId.New(), new ChatTextContent(ChatContentId.New(), "cancel"), cancellation.Token));
    }

    private sealed class FixedDirectory(IReadOnlyList<PublicDevice> devices) : IRecipientDeviceDirectory
    {
        public ValueTask<ChatDevicePage> ListConversationDevicesAsync(
            ConversationId conversationId,
            string? cursor = null,
            int maximumCount = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ChatDevicePage(devices, null));
        }
    }

    private sealed class RecordingTransport : IChatTransport
    {
        internal List<EncryptedEnvelope> Attempts { get; } = [];
        internal int? FailAttempt { get; set; }

        public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PublicDevice?>(null);

        public ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(envelope);
            if (FailAttempt == Attempts.Count)
            {
                throw new HttpRequestException("Synthetic bounded network failure.");
            }

            return ValueTask.FromResult(TransportSendStatus.Accepted);
        }

        public ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(DeviceId recipientDeviceId, int maximumCount, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask AcknowledgeAsync(DeviceId recipientDeviceId, MessageId messageId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CancellingTransport : IChatTransport
    {
        public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PublicDevice?>(null);

        public ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }

        public ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(DeviceId recipientDeviceId, int maximumCount, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask AcknowledgeAsync(DeviceId recipientDeviceId, MessageId messageId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
