using Skopka.Chat.Attachments;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.UI;

await Skopka.Chat.Sample.PersistentIdentityExample.RunAsync();

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
var aliceUi = new ChatViewModel(
    conversationId,
    alice.UserId,
    new SingleRecipientContentSender(aliceCrypto, alice, bob, transport, TimeProvider.System));
aliceUi.SetDraftText(originalText);
if (!await aliceUi.TrySendDraftAsync())
{
    throw new InvalidOperationException("The sample message could not be sent.");
}

var serverRecord = serverStore.SnapshotEnvelopes().Single();
Console.WriteLine($"Server stored ciphertext bytes: {serverRecord.Envelope.Ciphertext.Length}");
Console.WriteLine($"Server ciphertext preview: {Convert.ToBase64String(serverRecord.Envelope.Ciphertext.Span)[..16]}…");

var delivery = (await transport.ReceiveAsync(bob.DeviceId, 10)).Single();
var sender = await transport.GetDeviceAsync(delivery.Envelope.SenderDeviceId) ??
    throw new InvalidOperationException("Sender directory entry is missing.");
var bobLocalStore = new InMemoryReceivedMessageStore();
var receiver = new ChatReceiver(new ChatCryptoService(bobKeyStore), bobLocalStore);
var received = await receiver.ReceiveContentAsync(delivery.Envelope, sender);
var bobUi = new ChatViewModel(conversationId, bob.UserId, new DisabledContentSender());
var roundTripMatches = received.Delivery is not null &&
    bobUi.Apply(received.Delivery) == ChatProjectionApplyResult.Applied &&
    bobUi.Messages.Single().Text == originalText;
await transport.AcknowledgeAsync(bob.DeviceId, delivery.Envelope.MessageId, DateTimeOffset.UtcNow);

Console.WriteLine($"Bob authenticated and decrypted the original text: {roundTripMatches}");
Console.WriteLine($"Out-of-band security code: {SecurityCodes.Between(alice, bob)}");

var mediaPlaintext = "sample media bytes that never enter attachment storage as plaintext"u8.ToArray();
await using var mediaInput = new MemoryStream(mediaPlaintext, writable: false);
await using var mediaCiphertext = new MemoryStream();
var mediaManifest = await ChatAttachmentCryptoService.EncryptAsync(
    mediaInput,
    mediaPlaintext.Length,
    mediaCiphertext,
    AttachmentId.New(),
    ChatContentId.New(),
    "sample.bin",
    "application/octet-stream",
    "Encrypted sample attachment");
if (!await aliceUi.SendAttachmentAsync(mediaManifest))
{
    throw new InvalidOperationException("The sample attachment manifest could not be sent.");
}

var mediaEnvelope = AssertSingle(await transport.ReceiveAsync(bob.DeviceId, 10));
var receivedMedia = await receiver.ReceiveContentAsync(mediaEnvelope.Envelope, alice);
var projectedMedia = receivedMedia.Delivery is not null &&
    bobUi.Apply(receivedMedia.Delivery) == ChatProjectionApplyResult.Applied
    ? bobUi.Timeline.OfType<ProjectedChatAttachment>().Single()
    : throw new InvalidOperationException("The sample attachment manifest could not be projected.");
mediaCiphertext.Position = 0;
await using var mediaOutput = new MemoryStream();
await ChatAttachmentCryptoService.DecryptAsync(projectedMedia.Manifest, mediaCiphertext, mediaOutput);
await transport.AcknowledgeAsync(bob.DeviceId, mediaEnvelope.Envelope.MessageId, DateTimeOffset.UtcNow);

Console.WriteLine($"Separate encrypted attachment bytes: {mediaManifest.CiphertextLength}");
Console.WriteLine($"Bob authenticated and decrypted the attachment: {mediaPlaintext.SequenceEqual(mediaOutput.ToArray())}");

static T AssertSingle<T>(IReadOnlyList<T> items) => items.Count == 1
    ? items[0]
    : throw new InvalidOperationException("The sample expected exactly one delivery.");

internal sealed class SingleRecipientContentSender : IChatContentSender
{
    private readonly ChatCryptoService _crypto;
    private readonly PublicDevice _sender;
    private readonly PublicDevice _recipient;
    private readonly IChatTransport _transport;
    private readonly TimeProvider _timeProvider;

    public SingleRecipientContentSender(
        ChatCryptoService crypto,
        PublicDevice sender,
        PublicDevice recipient,
        IChatTransport transport,
        TimeProvider timeProvider)
    {
        _crypto = crypto;
        _sender = sender;
        _recipient = recipient;
        _transport = transport;
        _timeProvider = timeProvider;
    }

    public async ValueTask<ChatContentSendResult> SendAsync(
        ConversationId conversationId,
        ChatContent content,
        CancellationToken cancellationToken = default)
    {
        var sentAt = _timeProvider.GetUtcNow();
        var messageId = MessageId.New();
        var envelope = await _crypto.EncryptContentAsync(
            content,
            conversationId,
            messageId,
            _sender.DeviceId,
            _recipient,
            sentAt,
            cancellationToken: cancellationToken);
        await _transport.SendAsync(envelope, cancellationToken);
        return ChatContentSendResult.Success(new ReceivedChatContent(
            messageId,
            conversationId,
            _sender.UserId,
            _sender.DeviceId,
            sentAt,
            content));
    }
}

internal sealed class DisabledContentSender : IChatContentSender
{
    public ValueTask<ChatContentSendResult> SendAsync(
        ConversationId conversationId,
        ChatContent content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ChatContentSendResult.Failed);
    }
}

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
