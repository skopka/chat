using Microsoft.Data.Sqlite;
using Skopka.Chat.Bots.Sqlite;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots.Tests;

public sealed class BotSqliteTests
{
    [Fact]
    public async Task Independent_writers_atomically_deduplicate_delivery_and_logical_content()
    {
        using var f = await BotFixture.CreateAsync();
        var delivery = await f.AddAsync();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await f.Inbox.StoreAsync(delivery, f.Grant!.GrantId)));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(result => result == ChatBotStoreResult.Stored));
        Assert.Equal(7, results.Count(result => result == ChatBotStoreResult.Duplicate));
        var redelivery = new ReceivedChatContent(MessageId.New(), delivery.ConversationId, delivery.SenderUserId, DeviceId.New(),
            delivery.SentAt.AddSeconds(1), delivery.Content);
        Assert.Equal(ChatBotStoreResult.Duplicate, await f.Inbox.StoreAsync(redelivery, f.Grant!.GrantId));
        Assert.Single(await f.Inbox.ReadAsync(0, 20));
    }

    [Fact]
    public async Task Conflicting_delivery_or_logical_content_cannot_replace_plaintext()
    {
        using var f = await BotFixture.CreateAsync();
        var original = await f.AddAsync();
        await f.Inbox.StoreAsync(original, f.Grant!.GrantId);
        var changed = new ReceivedChatContent(original.DeliveryMessageId, original.ConversationId, original.SenderUserId,
            original.SenderDeviceId, original.SentAt, new ChatTextContent(original.Content.ContentId, "conflicting synthetic text"));
        Assert.Equal(ChatBotStoreResult.Conflict, await f.Inbox.StoreAsync(changed, f.Grant.GrantId));
        var logicalConflict = new ReceivedChatContent(MessageId.New(), changed.ConversationId, changed.SenderUserId, changed.SenderDeviceId,
            changed.SentAt, changed.Content);
        Assert.Equal(ChatBotStoreResult.Conflict, await f.Inbox.StoreAsync(logicalConflict, f.Grant.GrantId));
        Assert.Equal("synthetic message", Assert.Single(await f.Inbox.ReadAsync(0, 20)).Text);
    }

    [Fact]
    public async Task Request_reservation_is_atomic_and_bound_to_conversation_grant_and_payload()
    {
        using var f = await BotFixture.CreateAsync();
        var content = new ChatTextContent(ChatContentId.New(), "synthetic send");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await f.Inbox.ReserveSendAsync(f.Conversation, f.Grant!.GrantId, content))));
        Assert.Equal(1, results.Count(r => r == ChatBotStoreResult.Stored));
        Assert.Equal(7, results.Count(r => r == ChatBotStoreResult.Duplicate));
        Assert.Equal(ChatBotStoreResult.Conflict, await f.Inbox.ReserveSendAsync(ConversationId.New(), f.Grant!.GrantId, content));
        Assert.Equal(ChatBotStoreResult.Conflict, await f.Inbox.ReserveSendAsync(f.Conversation, Guid.NewGuid(), content));
        Assert.Equal(ChatBotStoreResult.Conflict, await f.Inbox.ReserveSendAsync(f.Conversation, f.Grant.GrantId, new(content.ContentId, "synthetic changed")));
    }

    [Fact]
    public async Task Namespace_and_schema_mismatch_are_generic_failures()
    {
        using var f = await BotFixture.CreateAsync();
        _ = await f.Inbox.ReadAsync(0, 1);
        var other = new SqliteChatBotInbox($"Data Source={Path.Combine(f.DirectoryPath, "inbox.db")};Pooling=False",
            new(f.Bot.UserId, f.Profile.Name, "another-operator", "another operator", ChatBotHosting.FirstParty, Guid.NewGuid()), f.Bot.DeviceId);
        var error = await Assert.ThrowsAsync<ChatBotException>(() => other.ReadAsync(0, 1).AsTask());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(f.DirectoryPath, error.ToString(), StringComparison.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(f.DirectoryPath, "inbox.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = 999;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<ChatBotException>(() => f.Inbox.ReadAsync(0, 1).AsTask());
    }

    [Fact]
    public async Task Pending_reads_are_bounded_ordered_and_ack_erases_active_text_without_removing_tombstone()
    {
        using var f = await BotFixture.CreateAsync();
        for (var i = 0; i < 25; i++) { await f.Inbox.StoreAsync(await f.AddAsync($"synthetic {i}"), f.Grant!.GrantId); }
        var first = await f.Inbox.ReadAsync(0, 20);
        Assert.Equal(20, first.Count);
        Assert.Equal(5, (await f.Inbox.ReadAsync(first[^1].UpdateId, 20)).Count);
        await f.Inbox.AcknowledgeAsync(first[0].UpdateId);
        Assert.Equal(first[1].UpdateId, (await f.Inbox.ReadAsync(0, 20))[0].UpdateId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Inbox.ReadAsync(0, 21).AsTask());
        Assert.Throws<ArgumentException>(() => new SqliteChatBotInbox("Data Source=:memory:", f.Profile, f.Bot.DeviceId));
    }
}
