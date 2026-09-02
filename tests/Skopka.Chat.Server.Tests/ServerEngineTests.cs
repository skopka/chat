using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Server.Tests;

public sealed class ServerEngineTests
{
    [Fact]
    public void Server_project_does_not_reference_client_package()
    {
        var references = typeof(ChatServerEngine).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();

        Assert.DoesNotContain("Skopka.Chat.Client", references);
        Assert.DoesNotContain("NSec.Cryptography", references);
    }

    [Fact]
    public async Task Retry_with_same_message_id_and_bytes_is_idempotent()
    {
        var fixture = await ServerFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();

        var first = await fixture.Engine.SubmitAsync(envelope, fixture.Now);
        var second = await fixture.Engine.SubmitAsync(envelope, fixture.Now.AddSeconds(1));

        Assert.Equal(SubmitEnvelopeResult.Accepted, first);
        Assert.Equal(SubmitEnvelopeResult.Duplicate, second);
        Assert.Single(fixture.Store.SnapshotEnvelopes());
    }

    [Fact]
    public async Task Reused_message_id_with_different_bytes_is_rejected()
    {
        var fixture = await ServerFixture.CreateAsync();
        var envelope = fixture.CreateEnvelope();
        await fixture.Engine.SubmitAsync(envelope, fixture.Now);
        var changedCiphertext = envelope.Ciphertext.ToArray();
        changedCiphertext[0] ^= 0x01;

        await Assert.ThrowsAsync<ChatServerException>(async () =>
            await fixture.Engine.SubmitAsync(Clone(envelope, ciphertext: changedCiphertext), fixture.Now.AddSeconds(1)));
    }

    [Fact]
    public async Task Revoked_recipient_does_not_receive_new_messages()
    {
        var fixture = await ServerFixture.CreateAsync();
        Assert.True(await fixture.Engine.RevokeDeviceAsync(fixture.Bob.DeviceId, fixture.Now.AddMinutes(1)));

        await Assert.ThrowsAsync<ChatServerException>(async () =>
            await fixture.Engine.SubmitAsync(fixture.CreateEnvelope(), fixture.Now.AddMinutes(2)));
        await Assert.ThrowsAsync<ChatServerException>(async () =>
            await fixture.Engine.ReceiveAsync(fixture.Bob.DeviceId, 10, fixture.Now.AddMinutes(2)));
    }

    [Fact]
    public async Task Oversized_envelope_is_rejected_without_storage()
    {
        var fixture = await ServerFixture.CreateAsync();
        var oversized = Clone(fixture.CreateEnvelope(), ciphertext: new byte[ProtocolLimits.MaxCiphertextBytes + 1]);

        await Assert.ThrowsAsync<ProtocolValidationException>(async () =>
            await fixture.Engine.SubmitAsync(oversized, fixture.Now));
        Assert.Empty(fixture.Store.SnapshotEnvelopes());
    }

    [Fact]
    public async Task Pending_batch_uses_message_id_as_the_acceptance_time_tie_breaker()
    {
        var fixture = await ServerFixture.CreateAsync();
        var firstId = new MessageId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var secondId = new MessageId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var thirdId = new MessageId(Guid.Parse("00000000-0000-0000-0000-000000000003"));
        await fixture.Engine.SubmitAsync(fixture.CreateEnvelope(thirdId), fixture.Now);
        await fixture.Engine.SubmitAsync(fixture.CreateEnvelope(firstId), fixture.Now);
        await fixture.Engine.SubmitAsync(fixture.CreateEnvelope(secondId), fixture.Now);

        var batch = await fixture.Engine.ReceiveAsync(fixture.Bob.DeviceId, 2, fixture.Now);

        Assert.Equal([firstId, secondId], batch.Select(item => item.Envelope.MessageId));
    }

    [Fact]
    public async Task Concurrent_get_or_create_returns_one_canonical_personal_conversation()
    {
        var store = new InMemoryServerStore();
        var engine = new ChatServerEngine(store, store, store);
        var first = UserId.New();
        var second = UserId.New();
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        var conversations = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
            index % 2 == 0
                ? await engine.GetOrCreateConversationAsync(first, second, now)
                : await engine.GetOrCreateConversationAsync(second, first, now))));

        Assert.Single(conversations.Select(static item => item.ConversationId).Distinct());
        Assert.All(conversations, conversation =>
        {
            Assert.True(conversation.Contains(first));
            Assert.True(conversation.Contains(second));
        });
    }

    [Fact]
    public async Task Device_directory_requires_membership_and_excludes_revoked_devices()
    {
        var fixture = await ServerFixture.CreateAsync();
        var stranger = UserId.New();

        await Assert.ThrowsAsync<ChatServerException>(async () =>
            await fixture.Engine.ListConversationDevicesAsync(stranger, fixture.ConversationId, null, 10));
        Assert.True(await fixture.Engine.RevokeDeviceAsync(fixture.Bob.DeviceId, fixture.Now.AddMinutes(1)));

        var page = await fixture.Engine.ListConversationDevicesAsync(
            fixture.Alice.UserId,
            fixture.ConversationId,
            null,
            10);

        Assert.Equal(fixture.Alice.DeviceId, Assert.Single(page.Items).DeviceId);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Envelope_to_active_sibling_device_is_allowed_for_multi_device_sync()
    {
        var fixture = await ServerFixture.CreateAsync();
        var sibling = ServerFixture.Device(
            fixture.Alice.UserId,
            DeviceId.New(),
            KeyId.New(),
            120,
            fixture.Now);
        await fixture.Engine.RegisterDeviceAsync(sibling);
        var source = fixture.CreateEnvelope();
        var envelope = new EncryptedEnvelope(
            source.ProtocolVersion,
            MessageId.New(),
            source.ConversationId,
            source.SenderDeviceId,
            sibling.DeviceId,
            source.SenderSigningKeyId,
            sibling.KeyId,
            source.SentAt,
            source.ExpiresAt,
            source.EphemeralPublicKey.Span,
            source.Nonce.Span,
            source.Ciphertext.Span,
            source.AuthenticationTag.Span,
            source.Signature.Span);

        Assert.Equal(SubmitEnvelopeResult.Accepted, await fixture.Engine.SubmitAsync(envelope, fixture.Now));
    }

    private static EncryptedEnvelope Clone(EncryptedEnvelope source, byte[] ciphertext) => new(
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
        ciphertext,
        source.AuthenticationTag.Span,
        source.Signature.Span);

    private sealed class ServerFixture
    {
        private ServerFixture(InMemoryServerStore store, ChatServerEngine engine, PublicDevice alice, PublicDevice bob)
        {
            Store = store;
            Engine = engine;
            Alice = alice;
            Bob = bob;
        }

        public DateTimeOffset Now { get; } = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        public ConversationId ConversationId { get; } = ConversationId.New();
        public InMemoryServerStore Store { get; }
        public ChatServerEngine Engine { get; }
        public PublicDevice Alice { get; }
        public PublicDevice Bob { get; }

        public static async Task<ServerFixture> CreateAsync()
        {
            var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
            var alice = Device(UserId.New(), DeviceId.New(), KeyId.New(), 1, now);
            var bob = Device(UserId.New(), DeviceId.New(), KeyId.New(), 65, now);
            var store = new InMemoryServerStore();
            var engine = new ChatServerEngine(store, store, store);
            var fixture = new ServerFixture(store, engine, alice, bob);
            await engine.RegisterDeviceAsync(alice);
            await engine.RegisterDeviceAsync(bob);
            await engine.CreateConversationAsync(alice.UserId, bob.UserId, fixture.ConversationId, now);
            return fixture;
        }

        public EncryptedEnvelope CreateEnvelope(MessageId? messageId = null) => new(
            ProtocolVersions.V1,
            messageId ?? MessageId.New(),
            ConversationId,
            Alice.DeviceId,
            Bob.DeviceId,
            Alice.KeyId,
            Bob.KeyId,
            Now,
            Now.AddDays(1),
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            Enumerable.Range(32, 24).Select(value => (byte)value).ToArray(),
            [1, 2, 3, 4],
            Enumerable.Repeat((byte)7, 16).ToArray(),
            Enumerable.Repeat((byte)9, 64).ToArray());

        internal static PublicDevice Device(
            UserId userId,
            DeviceId deviceId,
            KeyId keyId,
            int seed,
            DateTimeOffset registeredAt) => new(
            userId,
            deviceId,
            keyId,
            Enumerable.Range(seed, 32).Select(value => (byte)value).ToArray(),
            Enumerable.Range(seed + 32, 32).Select(value => (byte)value).ToArray(),
            registeredAt);
    }
}
