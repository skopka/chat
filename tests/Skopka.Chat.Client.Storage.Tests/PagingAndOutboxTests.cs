using Skopka.Chat.Client;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage.Tests;

public sealed class PagingAndOutboxTests
{
    [Fact]
    public async Task Sqlite_previous_pages_are_stable_bounded_and_have_no_gaps_at_equal_timestamps()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using var store = new SqliteChatEventStore(Connection(path));
            var conversation = new ConversationId(GuidFrom(1));
            var sentAt = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 5; index++)
            {
                await store.StoreAsync(new ReceivedChatContent(
                    new MessageId(GuidFrom(10 + index)),
                    conversation,
                    new UserId(GuidFrom(2)),
                    new DeviceId(GuidFrom(3)),
                    sentAt,
                    new ChatTextContent(new ChatContentId(GuidFrom(20 + index)), $"item-{index}")));
            }

            var newest = await store.ReadPreviousPageAsync(conversation, maximumCount: 2);
            var middle = await store.ReadPreviousPageAsync(conversation, newest.PreviousCursor, 2);
            var oldest = await store.ReadPreviousPageAsync(conversation, middle.PreviousCursor, 2);
            var combined = oldest.Items.Concat(middle.Items).Concat(newest.Items).ToArray();

            Assert.Equal(5, combined.Length);
            Assert.Equal(5, combined.Select(static item => item.DeliveryMessageId).Distinct().Count());
            Assert.Equal(["item-0", "item-1", "item-2", "item-3", "item-4"],
                combined.Select(static item => Assert.IsType<ChatTextContent>(item.Content).Text));
            Assert.Null(oldest.PreviousCursor);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await store.ReadPreviousPageAsync(conversation, "not-canonical", 2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task History_pager_restores_bounded_pages_without_reloading_the_initial_page()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using var store = new SqliteChatEventStore(Connection(path));
            var conversation = new ConversationId(GuidFrom(30));
            var sentAt = new DateTimeOffset(2026, 9, 2, 12, 30, 0, TimeSpan.Zero);
            for (var index = 0; index < 5; index++)
            {
                await store.StoreAsync(new ReceivedChatContent(
                    new MessageId(GuidFrom(40 + index)),
                    conversation,
                    new UserId(GuidFrom(31)),
                    new DeviceId(GuidFrom(32)),
                    sentAt,
                    new ChatTextContent(new ChatContentId(GuidFrom(50 + index)), $"page-{index}")));
            }

            var projections = new ChatConversationProjectionRegistry();
            using var pager = new ChatHistoryPager(store, projections, conversation, pageSize: 2);

            Assert.Equal(new ChatHistoryPageResult(2, true), await pager.LoadInitialAsync());
            Assert.Equal(new ChatHistoryPageResult(0, true), await pager.LoadInitialAsync());
            Assert.Equal(new ChatHistoryPageResult(2, true), await pager.LoadPreviousAsync());
            Assert.Equal(new ChatHistoryPageResult(1, false), await pager.LoadPreviousAsync());
            Assert.Equal(new ChatHistoryPageResult(0, false), await pager.LoadPreviousAsync());
            Assert.Equal(
                ["page-0", "page-1", "page-2", "page-3", "page-4"],
                projections.GetOrCreate(conversation).SnapshotTimeline()
                    .Select(static item => Assert.IsType<ProjectedChatMessage>(item).Text));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sqlite_outbox_restart_resumes_exact_pending_ciphertext_and_cleans_completed_plan()
    {
        var path = TemporaryDatabasePath();
        try
        {
            var plan = CreatePlan();
            await using (var first = new AsyncStore(new SqliteChatOutboxStore(Connection(path))))
            {
                Assert.Equal(ChatFanOutPlanStoreResult.Stored, await first.Store.StoreAsync(plan));
                await first.Store.MarkAcceptedAsync(
                    plan.ConversationId,
                    plan.ContentId,
                    plan.Envelopes[0].Envelope.MessageId,
                    plan.SentAt.AddSeconds(1));
            }

            await using var restarted = new AsyncStore(new SqliteChatOutboxStore(Connection(path)));
            var loaded = await restarted.Store.LoadAsync(plan.ConversationId, plan.ContentId);
            Assert.NotNull(loaded);
            Assert.True(loaded.Envelopes[0].IsAccepted);
            Assert.False(loaded.Envelopes[1].IsAccepted);
            var expectedBytes = CanonicalEnvelopeEncoding.EncodeEnvelope(plan.Envelopes[1].Envelope);
            var transport = new RecordingTransport();
            using var dispatcher = new ChatOutboxDispatcher(
                restarted.Store,
                transport,
                new FixedTimeProvider(plan.SentAt.AddMinutes(1)));

            var result = await dispatcher.DispatchAsync();

            Assert.Equal(new ChatOutboxDispatchResult(1, 1, 1), result);
            Assert.Equal(expectedBytes, CanonicalEnvelopeEncoding.EncodeEnvelope(Assert.Single(transport.Sent)));
            Assert.Empty(await PendingAsync(restarted.Store));
            Assert.Equal(1, await restarted.Store.DeleteCompletedBeforeAsync(plan.SentAt.AddMinutes(2)));
            Assert.Null(await restarted.Store.LoadAsync(plan.ConversationId, plan.ContentId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sqlite_outbox_rejects_conflicting_logical_content_id()
    {
        var path = TemporaryDatabasePath();
        try
        {
            using var store = new SqliteChatOutboxStore(Connection(path));
            var plan = CreatePlan();
            Assert.Equal(ChatFanOutPlanStoreResult.Stored, await store.StoreAsync(plan));
            var conflictingHash = plan.ContentHash.ToArray();
            conflictingHash[0] ^= 1;
            var conflicting = new ChatFanOutPlan(
                plan.ConversationId,
                plan.ContentId,
                plan.SenderUserId,
                plan.SenderDeviceId,
                plan.LocalEchoMessageId,
                plan.SentAt,
                conflictingHash,
                plan.Envelopes);

            Assert.Equal(ChatFanOutPlanStoreResult.Conflict, await store.StoreAsync(conflicting));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ChatFanOutPlan CreatePlan()
    {
        var conversation = new ConversationId(GuidFrom(100));
        var content = new ChatContentId(GuidFrom(101));
        var sender = new DeviceId(GuidFrom(102));
        var sentAt = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        return new ChatFanOutPlan(
            conversation,
            content,
            new UserId(GuidFrom(103)),
            sender,
            new MessageId(GuidFrom(104)),
            sentAt,
            Enumerable.Repeat((byte)7, 32).ToArray(),
            [
                new ChatEnvelopePlanItem(Envelope(conversation, sender, 105, 106, sentAt, 1), false),
                new ChatEnvelopePlanItem(Envelope(conversation, sender, 107, 108, sentAt, 2), false),
            ]);
    }

    private static EncryptedEnvelope Envelope(
        ConversationId conversation,
        DeviceId sender,
        int messageSeed,
        int recipientSeed,
        DateTimeOffset sentAt,
        byte payload) => new(
            ProtocolVersions.Current,
            new MessageId(GuidFrom(messageSeed)),
            conversation,
            sender,
            new DeviceId(GuidFrom(recipientSeed)),
            new KeyId(GuidFrom(200)),
            new KeyId(GuidFrom(201 + recipientSeed)),
            sentAt,
            null,
            Enumerable.Repeat(payload, ProtocolLimits.X25519PublicKeyBytes).Select(static value => (byte)value).ToArray(),
            Enumerable.Repeat(payload, ProtocolLimits.NonceBytes).Select(static value => (byte)value).ToArray(),
            [payload],
            Enumerable.Repeat(payload, ProtocolLimits.AuthenticationTagBytes).Select(static value => (byte)value).ToArray(),
            Enumerable.Repeat(payload, ProtocolLimits.SignatureBytes).Select(static value => (byte)value).ToArray());

    private static async Task<List<ChatFanOutPlan>> PendingAsync(SqliteChatOutboxStore store)
    {
        var plans = new List<ChatFanOutPlan>();
        await foreach (var plan in store.ReadPendingAsync())
        {
            plans.Add(plan);
        }

        return plans;
    }

    private static string TemporaryDatabasePath() => Path.Combine(Path.GetTempPath(), $"skopka-outbox-{Guid.NewGuid():N}.db");
    private static string Connection(string path) => $"Data Source={path};Pooling=False;Default Timeout=5";
    private static Guid GuidFrom(int value) => new($"00000000-0000-0000-0000-{value:X12}");

    private sealed class RecordingTransport : IChatTransport
    {
        internal List<EncryptedEnvelope> Sent { get; } = [];
        public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PublicDevice?>(null);
        public ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Sent.Add(envelope);
            return ValueTask.FromResult(TransportSendStatus.Accepted);
        }
        public ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(DeviceId recipientDeviceId, int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask AcknowledgeAsync(DeviceId recipientDeviceId, MessageId messageId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AsyncStore(SqliteChatOutboxStore store) : IAsyncDisposable
    {
        internal SqliteChatOutboxStore Store { get; } = store;
        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
