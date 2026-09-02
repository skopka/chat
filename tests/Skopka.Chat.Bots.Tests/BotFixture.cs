using Skopka.Chat.Bots.Sqlite;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots.Tests;

internal sealed class BotFixture : IDisposable, IChatTransport, IRecipientDeviceDirectory, IChatBotConsentProvider
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    internal string DirectoryPath { get; } = Directory.CreateTempSubdirectory("skopka-bot-test-").FullName;
    internal InMemoryDeviceKeyStore Keys { get; } = new();
    internal PublicDevice Bot { get; private set; } = null!;
    internal PublicDevice Peer { get; private set; } = null!;
    internal ChatBotProfile Profile { get; private set; } = null!;
    internal ConversationId Conversation { get; } = ConversationId.New();
    internal ChatBotConsent? Grant { get; set; }
    internal SqliteChatBotInbox Inbox => new($"Data Source={Path.Combine(DirectoryPath, "inbox.db")};Pooling=False", Profile, Bot.DeviceId);
    internal List<TransportDelivery> Pending { get; } = [];
    internal List<EncryptedEnvelope> Sent { get; } = [];
    internal int Acknowledged { get; private set; }
    internal bool FailAcknowledgement { get; set; }
    internal bool FailSendOnce { get; set; }
    internal bool FailConsent { get; set; }
    internal SqliteChatOutboxStore Outbox { get; private set; } = null!;

    internal static async Task<BotFixture> CreateAsync()
    {
        var fixture = new BotFixture();
        var identities = new DeviceIdentityService(fixture.Keys);
        fixture.Bot = await identities.CreateAsync(UserId.New(), DeviceId.New(), Now);
        fixture.Peer = await identities.CreateAsync(UserId.New(), DeviceId.New(), Now);
        fixture.Profile = new(fixture.Bot.UserId, "Synthetic bot", "synthetic-owner", "Synthetic operator", ChatBotHosting.OwnerHosted, Guid.NewGuid());
        fixture.Grant = new(Guid.NewGuid(), fixture.Conversation, fixture.Peer.UserId, fixture.Bot.UserId, fixture.Profile.Revision, Now.AddHours(1));
        fixture.Outbox = new($"Data Source={Path.Combine(fixture.DirectoryPath, "outbox.db")};Pooling=False");
        return fixture;
    }

    internal ChatBotRuntime Runtime(IChatBotInbox? inbox = null) => new(Profile, Bot.DeviceId, this, new(Keys), this, Outbox, inbox ?? Inbox, this, new TestTime());

    internal async Task<ReceivedChatContent> AddAsync(string text = "synthetic message", ChatContentId? contentId = null)
    {
        var content = new ChatTextContent(contentId ?? ChatContentId.New(), text);
        var envelope = await new ChatCryptoService(Keys).EncryptContentAsync(content, Conversation, MessageId.New(), Peer.DeviceId, Bot, Now);
        Pending.Add(new(envelope, Now));
        return new(envelope.MessageId, Conversation, Peer.UserId, Peer.DeviceId, Now, content);
    }

    public ValueTask<ChatBotConsent?> GetConsentAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailConsent) { throw new ChatBotException(); }
        return ValueTask.FromResult(conversationId == Conversation ? Grant : null);
    }
    public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(deviceId == Bot.DeviceId ? Bot : deviceId == Peer.DeviceId ? Peer : null);
    public ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Sent.Add(envelope);
        if (FailSendOnce) { FailSendOnce = false; throw new HttpRequestException("synthetic network failure"); }
        return ValueTask.FromResult(TransportSendStatus.Accepted);
    }
    public ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(DeviceId recipientDeviceId, int maximumCount, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<TransportDelivery>>(Pending.Take(maximumCount).ToArray());
    public ValueTask AcknowledgeAsync(DeviceId recipientDeviceId, MessageId messageId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default)
    {
        if (FailAcknowledgement) { throw new HttpRequestException("synthetic acknowledgement failure"); }
        Acknowledged++;
        return ValueTask.CompletedTask; // Deliberately retain transport rows to exercise exact redelivery.
    }
    public ValueTask<ChatDevicePage> ListConversationDevicesAsync(ConversationId conversationId, string? cursor = null, int maximumCount = 50, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ChatDevicePage([Bot, Peer], null));

    public void Dispose()
    {
        Outbox?.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
    private sealed class TestTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
