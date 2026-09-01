using System.Text;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.IntegrationTests;

public sealed class EncryptedRoundTripTests
{
    [Fact]
    public async Task Alice_server_Bob_round_trip_never_stores_plaintext()
    {
        var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var aliceStore = new InMemoryDeviceKeyStore();
        var bobStore = new InMemoryDeviceKeyStore();
        var alice = await new DeviceIdentityService(aliceStore).CreateAsync(UserId.New(), DeviceId.New(), now);
        var bob = await new DeviceIdentityService(bobStore).CreateAsync(UserId.New(), DeviceId.New(), now);
        var serverStore = new InMemoryServerStore();
        var server = new ChatServerEngine(serverStore, serverStore, serverStore);
        await server.RegisterDeviceAsync(alice);
        await server.RegisterDeviceAsync(bob);
        var conversationId = ConversationId.New();
        await server.CreateConversationAsync(alice.UserId, bob.UserId, conversationId, now);
        const string plaintext = "The server must never see this plaintext marker: 7C8B17D6.";
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var envelope = await new ChatCryptoService(aliceStore).EncryptTextAsync(
            plaintext,
            conversationId,
            MessageId.New(),
            alice.DeviceId,
            bob,
            now);

        Assert.Equal(SubmitEnvelopeResult.Accepted, await server.SubmitAsync(envelope, now.AddSeconds(1)));
        var stored = Assert.Single(serverStore.SnapshotEnvelopes());
        Assert.True(stored.Envelope.Ciphertext.Span.IndexOf(plaintextBytes) < 0);
        Assert.NotEqual(plaintext, Encoding.UTF8.GetString(stored.Envelope.Ciphertext.Span));

        var delivery = Assert.Single(await server.ReceiveAsync(bob.DeviceId, 10, now.AddSeconds(2)));
        var localStore = new InMemoryReceivedMessageStore();
        var receiver = new ChatReceiver(new ChatCryptoService(bobStore), localStore);
        var first = await receiver.ReceiveAsync(delivery.Envelope, alice);
        var duplicate = await receiver.ReceiveAsync(delivery.Envelope, alice);

        Assert.True(first.Added);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(first.Message!.ExportPlaintext()));
        Assert.False(duplicate.Added);
        Assert.Equal(1, localStore.Count);
        Assert.True(await server.AcknowledgeAsync(bob.DeviceId, envelope.MessageId, now.AddSeconds(3)));
        Assert.Empty(await server.ReceiveAsync(bob.DeviceId, 10, now.AddSeconds(4)));
    }

    [Fact]
    public async Task Typed_reply_forward_and_reaction_round_trip_as_opaque_ciphertext()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var aliceStore = new InMemoryDeviceKeyStore();
        var bobStore = new InMemoryDeviceKeyStore();
        var alice = await new DeviceIdentityService(aliceStore).CreateAsync(UserId.New(), DeviceId.New(), now);
        var bob = await new DeviceIdentityService(bobStore).CreateAsync(UserId.New(), DeviceId.New(), now);
        var serverStore = new InMemoryServerStore();
        var server = new ChatServerEngine(serverStore, serverStore, serverStore);
        await server.RegisterDeviceAsync(alice);
        await server.RegisterDeviceAsync(bob);
        var conversationId = ConversationId.New();
        await server.CreateConversationAsync(alice.UserId, bob.UserId, conversationId, now);
        var firstId = ChatContentId.New();
        const string secretMarker = "typed secret marker 4D84358A";
        var first = new ChatTextContent(firstId, secretMarker);
        ChatContent[] content =
        [
            first,
            new ChatTextContent(ChatContentId.New(), "reply", firstId),
            first.Forward(ChatContentId.New()),
            new ChatReactionContent(ChatContentId.New(), firstId, "👍", ChatReactionOperation.Add),
        ];
        var aliceCrypto = new ChatCryptoService(aliceStore);
        var envelopeIds = new List<MessageId>();
        for (var index = 0; index < content.Length; index++)
        {
            var messageId = MessageId.New();
            envelopeIds.Add(messageId);
            var envelope = await aliceCrypto.EncryptContentAsync(
                content[index],
                conversationId,
                messageId,
                alice.DeviceId,
                bob,
                now.AddSeconds(index));
            Assert.Equal(SubmitEnvelopeResult.Accepted, await server.SubmitAsync(envelope, now.AddSeconds(index + 1)));
        }

        var markerBytes = Encoding.UTF8.GetBytes(secretMarker);
        Assert.All(serverStore.SnapshotEnvelopes(), stored =>
            Assert.True(stored.Envelope.Ciphertext.Span.IndexOf(markerBytes) < 0));

        var receiver = new ChatReceiver(new ChatCryptoService(bobStore), new InMemoryReceivedMessageStore());
        var projection = new ChatConversationProjection(conversationId);
        foreach (var pending in await server.ReceiveAsync(bob.DeviceId, 10, now.AddMinutes(1)))
        {
            var received = await receiver.ReceiveContentAsync(pending.Envelope, alice);
            Assert.True(received.Added);
            Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(received.Delivery!));
            Assert.True(await server.AcknowledgeAsync(bob.DeviceId, pending.Envelope.MessageId, now.AddMinutes(2)));
        }

        var messages = projection.Snapshot();
        Assert.Equal(3, messages.Count);
        Assert.Equal(secretMarker, messages[0].Text);
        Assert.Equal(firstId, messages[1].ReplyToContentId);
        Assert.True(messages[2].IsForwarded);
        Assert.Equal(secretMarker, messages[2].Text);
        Assert.Equal("👍", Assert.Single(messages[0].Reactions).Reaction);
        Assert.Empty(await server.ReceiveAsync(bob.DeviceId, 10, now.AddMinutes(3)));
        Assert.Equal(4, envelopeIds.Distinct().Count());
    }
}
