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

        public EncryptedEnvelope CreateEnvelope() => new(
            ProtocolVersions.V1,
            MessageId.New(),
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

        private static PublicDevice Device(
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
