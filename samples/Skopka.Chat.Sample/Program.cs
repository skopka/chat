using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

var now = DateTimeOffset.UtcNow;
var aliceKeyStore = new InMemoryDeviceKeyStore();
var bobKeyStore = new InMemoryDeviceKeyStore();
var alice = await new DeviceIdentityService(aliceKeyStore).CreateAsync(UserId.New(), DeviceId.New(), now);
var bob = await new DeviceIdentityService(bobKeyStore).CreateAsync(UserId.New(), DeviceId.New(), now);

var serverStore = new InMemoryServerStore();
var server = new ChatServerEngine(serverStore, serverStore, serverStore);
await server.RegisterDeviceAsync(alice);
await server.RegisterDeviceAsync(bob);
var conversationId = ConversationId.New();
await server.CreateConversationAsync(alice.UserId, bob.UserId, conversationId, now);
var transport = new InProcessChatTransport(server, serverStore);

var originalText = "Hello Bob — this plaintext never enters the server API.";
var aliceCrypto = new ChatCryptoService(aliceKeyStore);
var originalContent = new ChatTextContent(ChatContentId.New(), originalText);
var envelope = await aliceCrypto.EncryptContentAsync(
    originalContent,
    conversationId,
    MessageId.New(),
    alice.DeviceId,
    bob,
    now);
await transport.SendAsync(envelope);

var serverRecord = serverStore.SnapshotEnvelopes().Single();
Console.WriteLine($"Server stored ciphertext bytes: {serverRecord.Envelope.Ciphertext.Length}");
Console.WriteLine($"Server ciphertext preview: {Convert.ToBase64String(serverRecord.Envelope.Ciphertext.Span)[..16]}…");

var delivery = (await transport.ReceiveAsync(bob.DeviceId, 10)).Single();
var sender = await transport.GetDeviceAsync(delivery.Envelope.SenderDeviceId) ??
    throw new InvalidOperationException("Sender directory entry is missing.");
var bobLocalStore = new InMemoryReceivedMessageStore();
var receiver = new ChatReceiver(new ChatCryptoService(bobKeyStore), bobLocalStore);
var received = await receiver.ReceiveContentAsync(delivery.Envelope, sender);
var projection = new ChatConversationProjection(conversationId);
var roundTripMatches = received.Delivery is not null &&
    projection.Apply(received.Delivery) == ChatProjectionApplyResult.Applied &&
    projection.Snapshot().Single().Text == originalText;
await transport.AcknowledgeAsync(bob.DeviceId, delivery.Envelope.MessageId, DateTimeOffset.UtcNow);

Console.WriteLine($"Bob authenticated and decrypted the original text: {roundTripMatches}");
Console.WriteLine($"Out-of-band security code: {SecurityCodes.Between(alice, bob)}");

internal sealed class InProcessChatTransport : IChatTransport
{
    private readonly ChatServerEngine _server;
    private readonly IDeviceRepository _devices;

    public InProcessChatTransport(ChatServerEngine server, IDeviceRepository devices)
    {
        _server = server;
        _devices = devices;
    }

    public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
        _devices.GetAsync(deviceId, cancellationToken);

    public async ValueTask<TransportSendStatus> SendAsync(
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        await _server.SubmitAsync(envelope, DateTimeOffset.UtcNow, cancellationToken) == SubmitEnvelopeResult.Accepted
            ? TransportSendStatus.Accepted
            : TransportSendStatus.Duplicate;

    public async ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(
        DeviceId recipientDeviceId,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        (await _server.ReceiveAsync(recipientDeviceId, maximumCount, DateTimeOffset.UtcNow, cancellationToken))
        .Select(item => new TransportDelivery(item.Envelope, item.AcceptedAt))
        .ToArray();

    public ValueTask AcknowledgeAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default) =>
        AcknowledgeCoreAsync(recipientDeviceId, messageId, acknowledgedAt, cancellationToken);

    private async ValueTask AcknowledgeCoreAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        await _server.AcknowledgeAsync(recipientDeviceId, messageId, acknowledgedAt, cancellationToken);
    }
}
