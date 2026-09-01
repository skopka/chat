using System.Runtime.CompilerServices;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage.Tests;

public sealed class ClientStorageTests
{
    [Fact]
    public void Storage_packages_preserve_client_only_dependency_direction()
    {
        var coreReferences = typeof(ChatSyncCoordinator).Assembly
            .GetReferencedAssemblies()
            .Select(static item => item.Name)
            .ToArray();
        var sqliteReferences = typeof(SqliteChatEventStore).Assembly
            .GetReferencedAssemblies()
            .Select(static item => item.Name)
            .ToArray();

        Assert.Contains("Skopka.Chat.Client", coreReferences);
        Assert.DoesNotContain("Skopka.Chat.Client.Http", coreReferences);
        Assert.DoesNotContain("Skopka.Chat.Server", coreReferences);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", coreReferences);
        Assert.Contains("Skopka.Chat.Client.Storage", sqliteReferences);
        Assert.Contains("Microsoft.Data.Sqlite", sqliteReferences);
        Assert.DoesNotContain("Skopka.Chat.Server", sqliteReferences);
        Assert.DoesNotContain("Skopka.Chat.Persistence.PostgreSql", sqliteReferences);
    }

    [Fact]
    public void Sqlite_store_rejects_non_durable_memory_database()
    {
        Assert.Throws<ArgumentException>(() => new SqliteChatEventStore("Data Source=:memory:"));
        Assert.Throws<ArgumentException>(() => new SqliteChatEventStore("Data Source=chat;Mode=Memory;Cache=Shared"));
    }

    [Fact]
    public async Task Sqlite_store_roundtrips_and_distinguishes_duplicate_from_conflict()
    {
        var path = TemporaryDatabasePath();
        try
        {
            var store = CreateStore(path);
            var delivery = Delivery(1, 2, "first");
            var conflict = Delivery(1, 3, "different");

            Assert.Equal(ChatEventStoreResult.Stored, await store.StoreAsync(delivery));
            Assert.Equal(ChatEventStoreResult.Duplicate, await store.StoreAsync(delivery));
            Assert.Equal(ChatEventStoreResult.Conflict, await store.StoreAsync(conflict));

            var restored = await ReadAllAsync(store);
            var item = Assert.Single(restored);
            Assert.Equal(delivery.DeliveryMessageId, item.DeliveryMessageId);
            Assert.Equal(delivery.ConversationId, item.ConversationId);
            Assert.Equal(delivery.SenderUserId, item.SenderUserId);
            Assert.Equal(delivery.SenderDeviceId, item.SenderDeviceId);
            Assert.Equal(delivery.SentAt, item.SentAt);
            Assert.Equal("first", Assert.IsType<ChatTextContent>(item.Content).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Independent_sqlite_writers_atomically_commit_one_delivery()
    {
        var path = TemporaryDatabasePath();
        try
        {
            var stores = Enumerable.Range(0, 8).Select(_ => CreateStore(path)).ToArray();
            var delivery = Delivery(10, 11, "concurrent");

            var results = await Task.WhenAll(stores.Select(store => store.StoreAsync(delivery).AsTask()));

            Assert.Equal(1, results.Count(result => result == ChatEventStoreResult.Stored));
            Assert.Equal(7, results.Count(result => result == ChatEventStoreResult.Duplicate));
            Assert.Single(await ReadAllAsync(stores[0]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sqlite_conversation_read_is_filtered_ordered_and_paged()
    {
        var path = TemporaryDatabasePath();
        try
        {
            var store = CreateStore(path);
            var conversationId = new ConversationId(GuidFrom(300));
            for (var index = 0; index < 260; index++)
            {
                var delivery = new ReceivedChatContent(
                    new MessageId(GuidFrom(1_000 + index)),
                    conversationId,
                    new UserId(GuidFrom(301)),
                    new DeviceId(GuidFrom(302)),
                    new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(index),
                    new ChatTextContent(Id(2_000 + index), $"item-{index}"));
                Assert.Equal(ChatEventStoreResult.Stored, await store.StoreAsync(delivery));
            }

            Assert.Equal(ChatEventStoreResult.Stored, await store.StoreAsync(Delivery(4_000, 4_001, "other")));

            var restored = new List<ReceivedChatContent>();
            await foreach (var delivery in store.ReadConversationAsync(conversationId))
            {
                restored.Add(delivery);
            }

            Assert.Equal(260, restored.Count);
            Assert.Equal("item-0", Assert.IsType<ChatTextContent>(restored[0].Content).Text);
            Assert.Equal("item-259", Assert.IsType<ChatTextContent>(restored[^1].Content).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Coordinator_orders_store_apply_ack_and_restores_before_polling()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var content = new ChatTextContent(Id(20), "durable first");
        var envelope = await fixture.CreateEnvelopeAsync(content, 21);
        var order = new List<string>();
        var transport = new FakeTransport(fixture.Alice, envelope, order);
        var store = new RecordingStore(new InMemoryChatEventStore(), order);
        var applier = new RecordingApplier(order);
        var coordinator = new ChatSyncCoordinator(
            transport,
            fixture.BobCrypto,
            store,
            applier,
            fixture.Bob.DeviceId,
            new FixedTimeProvider(fixture.Now.AddMinutes(1)));

        var result = await coordinator.SynchronizeAsync();

        Assert.Equal(new ChatSyncBatchResult(1, 1, 0, 1), result);
        Assert.Equal(["store", "apply", "ack"], order);
        Assert.Equal(fixture.Now.AddMinutes(1), transport.AcknowledgedAt);
        Assert.Equal("durable first", Assert.IsType<ChatTextContent>(Assert.Single(applier.Deliveries).Content).Text);
    }

    [Fact]
    public async Task Local_echo_is_durably_applied_without_polling_or_acknowledgement()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var transport = new FakeTransport(fixture.Alice);
        var store = new InMemoryChatEventStore();
        var projections = new ChatConversationProjectionRegistry();
        var coordinator = new ChatSyncCoordinator(
            transport,
            fixture.BobCrypto,
            store,
            projections,
            fixture.Bob.DeviceId);
        var echo = new ReceivedChatContent(
            new MessageId(GuidFrom(25)),
            fixture.ConversationId,
            fixture.Bob.UserId,
            fixture.Bob.DeviceId,
            fixture.Now,
            new ChatTextContent(Id(26), "outgoing"));

        Assert.Equal(ChatEventStoreResult.Stored, await coordinator.CommitLocalEchoAsync(echo));
        Assert.Equal(ChatEventStoreResult.Duplicate, await coordinator.CommitLocalEchoAsync(echo));

        Assert.Equal(1, store.Count);
        Assert.Equal("outgoing", Assert.Single(projections.GetOrCreate(fixture.ConversationId).Snapshot()).Text);
        Assert.Equal(0, transport.ReceiveAttempts);
        Assert.Equal(0, transport.AcknowledgementAttempts);
    }

    [Fact]
    public async Task Acknowledgement_failure_retries_exact_event_without_duplicate_projection()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync(new ChatTextContent(Id(30), "retry"), 31);
        var transport = new FakeTransport(fixture.Alice, envelope) { AcknowledgementFailures = 1 };
        var store = new InMemoryChatEventStore();
        var projections = new ChatConversationProjectionRegistry();
        var coordinator = new ChatSyncCoordinator(
            transport,
            fixture.BobCrypto,
            store,
            projections,
            fixture.Bob.DeviceId);

        await Assert.ThrowsAsync<IOException>(async () => await coordinator.SynchronizeAsync());
        var retry = await coordinator.SynchronizeAsync();

        Assert.Equal(new ChatSyncBatchResult(1, 0, 1, 1), retry);
        Assert.Equal(1, store.Count);
        Assert.Single(projections.GetOrCreate(fixture.ConversationId).Snapshot());
        Assert.Equal(2, transport.AcknowledgementAttempts);
    }

    [Fact]
    public async Task Store_or_applier_failure_never_acknowledges_delivery()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync(new ChatTextContent(Id(40), "failure"), 41);
        var storeTransport = new FakeTransport(fixture.Alice, envelope);
        var storeCoordinator = new ChatSyncCoordinator(
            storeTransport,
            fixture.BobCrypto,
            new FailingStore(),
            new RecordingApplier(),
            fixture.Bob.DeviceId);

        await Assert.ThrowsAsync<ChatEventStorageException>(async () => await storeCoordinator.SynchronizeAsync());
        Assert.Equal(0, storeTransport.AcknowledgementAttempts);

        var applyTransport = new FakeTransport(fixture.Alice, envelope);
        var applyCoordinator = new ChatSyncCoordinator(
            applyTransport,
            fixture.BobCrypto,
            new InMemoryChatEventStore(),
            new RecordingApplier(fail: true),
            fixture.Bob.DeviceId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await applyCoordinator.SynchronizeAsync());
        Assert.Equal(0, applyTransport.AcknowledgementAttempts);
    }

    [Fact]
    public async Task Authentication_failure_is_not_stored_applied_or_acknowledged()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync(new ChatTextContent(Id(50), "tamper"), 51);
        var signature = envelope.Signature.ToArray();
        signature[0] ^= 1;
        var tampered = Clone(envelope, signature);
        var transport = new FakeTransport(fixture.Alice, tampered);
        var store = new InMemoryChatEventStore();
        var applier = new RecordingApplier();
        var coordinator = new ChatSyncCoordinator(
            transport,
            fixture.BobCrypto,
            store,
            applier,
            fixture.Bob.DeviceId);

        await Assert.ThrowsAsync<ChatCryptographicException>(async () => await coordinator.SynchronizeAsync());

        Assert.Equal(0, store.Count);
        Assert.Empty(applier.Deliveries);
        Assert.Equal(0, transport.AcknowledgementAttempts);
    }

    [Fact]
    public async Task Conflicting_delivery_id_is_not_applied_or_acknowledged()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync(new ChatTextContent(Id(60), "remote"), 61);
        var store = new InMemoryChatEventStore();
        await store.StoreAsync(new ReceivedChatContent(
            envelope.MessageId,
            envelope.ConversationId,
            fixture.Alice.UserId,
            fixture.Alice.DeviceId,
            envelope.SentAt,
            new ChatTextContent(Id(62), "existing")));
        var transport = new FakeTransport(fixture.Alice, envelope);
        var applier = new RecordingApplier();
        var coordinator = new ChatSyncCoordinator(
            transport,
            fixture.BobCrypto,
            store,
            applier,
            fixture.Bob.DeviceId);

        await Assert.ThrowsAsync<ChatSynchronizationException>(async () => await coordinator.SynchronizeAsync());

        Assert.Equal("existing", Assert.IsType<ChatTextContent>(Assert.Single(applier.Deliveries).Content).Text);
        Assert.Equal(0, transport.AcknowledgementAttempts);
    }

    [Fact]
    public async Task Restart_restores_out_of_order_edit_before_polling()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var store = new InMemoryChatEventStore();
        var originalId = Id(70);
        await store.StoreAsync(new ReceivedChatContent(
            new MessageId(GuidFrom(71)),
            fixture.ConversationId,
            fixture.Alice.UserId,
            fixture.Alice.DeviceId,
            fixture.Now.AddMinutes(1),
            new ChatEditContent(Id(72), originalId, ChatEditField.Text, "edited")));
        await store.StoreAsync(new ReceivedChatContent(
            new MessageId(GuidFrom(73)),
            fixture.ConversationId,
            fixture.Alice.UserId,
            fixture.Alice.DeviceId,
            fixture.Now,
            new ChatTextContent(originalId, "original")));
        var projections = new ChatConversationProjectionRegistry();
        var transport = new FakeTransport(fixture.Alice);
        var coordinator = new ChatSyncCoordinator(
            transport,
            fixture.BobCrypto,
            store,
            projections,
            fixture.Bob.DeviceId);

        Assert.Equal(2, await coordinator.InitializeAsync());
        Assert.Equal(0, await coordinator.InitializeAsync());

        var message = Assert.Single(projections.GetOrCreate(fixture.ConversationId).Snapshot());
        Assert.Equal("edited", message.Text);
        Assert.True(message.IsEdited);
        Assert.Equal(0, transport.ReceiveAttempts);
    }

    private static SqliteChatEventStore CreateStore(string path) =>
        new($"Data Source={path};Pooling=False;Default Timeout=5");

    private static string TemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"skopka-chat-{Guid.NewGuid():N}.db");

    private static ReceivedChatContent Delivery(int messageSeed, int contentSeed, string text) => new(
        new MessageId(GuidFrom(messageSeed)),
        new ConversationId(GuidFrom(100)),
        new UserId(GuidFrom(101)),
        new DeviceId(GuidFrom(102)),
        new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        new ChatTextContent(Id(contentSeed), text));

    private static async Task<List<ReceivedChatContent>> ReadAllAsync(SqliteChatEventStore store)
    {
        var result = new List<ReceivedChatContent>();
        await foreach (var delivery in store.ReadAllAsync())
        {
            result.Add(delivery);
        }

        return result;
    }

    private static ChatContentId Id(int value) => new(GuidFrom(value));

    private static Guid GuidFrom(int value) => new($"00000000-0000-0000-0000-{value:X12}");

    private static EncryptedEnvelope Clone(EncryptedEnvelope source, byte[] signature) => new(
        source.ProtocolVersion,
        source.MessageId,
        source.ConversationId,
        source.SenderDeviceId,
        source.RecipientDeviceId,
        source.SenderSigningKeyId,
        source.RecipientEncryptionKeyId,
        source.SentAt,
        source.ExpiresAt,
        source.EphemeralPublicKey.Span,
        source.Nonce.Span,
        source.Ciphertext.Span,
        source.AuthenticationTag.Span,
        signature);

    private sealed class RecordingStore(IChatEventStore inner, List<string> order) : IChatEventStore
    {
        public ValueTask<ChatEventStoreResult> StoreAsync(
            ReceivedChatContent delivery,
            CancellationToken cancellationToken = default)
        {
            order.Add("store");
            return inner.StoreAsync(delivery, cancellationToken);
        }

        public IAsyncEnumerable<ReceivedChatContent> ReadAllAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAllAsync(cancellationToken);

        public IAsyncEnumerable<ReceivedChatContent> ReadConversationAsync(
            ConversationId conversationId,
            CancellationToken cancellationToken = default) =>
            inner.ReadConversationAsync(conversationId, cancellationToken);
    }

    private sealed class FailingStore : IChatEventStore
    {
        public ValueTask<ChatEventStoreResult> StoreAsync(
            ReceivedChatContent delivery,
            CancellationToken cancellationToken = default) =>
            throw new ChatEventStorageException("Synthetic storage failure.");

        public async IAsyncEnumerable<ReceivedChatContent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<ReceivedChatContent> ReadConversationAsync(
            ConversationId conversationId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingApplier(List<string>? order = null, bool fail = false) : IChatEventApplier
    {
        public List<ReceivedChatContent> Deliveries { get; } = [];

        public ValueTask ApplyAsync(ReceivedChatContent delivery, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order?.Add("apply");
            if (fail)
            {
                throw new InvalidOperationException("Synthetic projection failure.");
            }

            Deliveries.Add(delivery);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTransport : IChatTransport
    {
        private readonly PublicDevice _sender;
        private readonly EncryptedEnvelope? _pending;
        private readonly List<string>? _order;
        private bool _acknowledged;

        public FakeTransport(PublicDevice sender, EncryptedEnvelope? pending = null, List<string>? order = null)
        {
            _sender = sender;
            _pending = pending;
            _order = order;
        }

        public int AcknowledgementFailures { get; set; }
        public int AcknowledgementAttempts { get; private set; }
        public int ReceiveAttempts { get; private set; }
        public DateTimeOffset? AcknowledgedAt { get; private set; }

        public ValueTask<PublicDevice?> GetDeviceAsync(
            DeviceId deviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PublicDevice?>(deviceId == _sender.DeviceId ? _sender : null);
        }

        public ValueTask<TransportSendStatus> SendAsync(
            EncryptedEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(
            DeviceId recipientDeviceId,
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceiveAttempts++;
            IReadOnlyList<TransportDelivery> result = _pending is not null && !_acknowledged
                ? [new TransportDelivery(_pending, _pending.SentAt)]
                : [];
            return ValueTask.FromResult(result);
        }

        public ValueTask AcknowledgeAsync(
            DeviceId recipientDeviceId,
            MessageId messageId,
            DateTimeOffset acknowledgedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _order?.Add("ack");
            AcknowledgementAttempts++;
            if (AcknowledgementFailures-- > 0)
            {
                throw new IOException("Synthetic acknowledgement failure.");
            }

            _acknowledged = true;
            AcknowledgedAt = acknowledgedAt;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CryptoFixture
    {
        private CryptoFixture(PublicDevice alice, PublicDevice bob, ChatCryptoService aliceCrypto, ChatCryptoService bobCrypto)
        {
            Alice = alice;
            Bob = bob;
            AliceCrypto = aliceCrypto;
            BobCrypto = bobCrypto;
        }

        public DateTimeOffset Now { get; } = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        public ConversationId ConversationId { get; } = new(GuidFrom(200));
        public PublicDevice Alice { get; }
        public PublicDevice Bob { get; }
        public ChatCryptoService AliceCrypto { get; }
        public ChatCryptoService BobCrypto { get; }

        public static async Task<CryptoFixture> CreateAsync()
        {
            var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
            var aliceStore = new InMemoryDeviceKeyStore();
            var bobStore = new InMemoryDeviceKeyStore();
            var alice = await new DeviceIdentityService(aliceStore).CreateAsync(
                new UserId(GuidFrom(201)),
                new DeviceId(GuidFrom(202)),
                now);
            var bob = await new DeviceIdentityService(bobStore).CreateAsync(
                new UserId(GuidFrom(203)),
                new DeviceId(GuidFrom(204)),
                now);
            return new CryptoFixture(
                alice,
                bob,
                new ChatCryptoService(aliceStore),
                new ChatCryptoService(bobStore));
        }

        public ValueTask<EncryptedEnvelope> CreateEnvelopeAsync(ChatContent content, int messageSeed) =>
            AliceCrypto.EncryptContentAsync(
                content,
                ConversationId,
                new MessageId(GuidFrom(messageSeed)),
                Alice.DeviceId,
                Bob,
                Now);
    }
}
