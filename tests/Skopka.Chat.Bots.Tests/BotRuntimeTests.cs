using Skopka.Chat.Bots.AspNetCore;
using Skopka.Chat.Bots.Sqlite;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots.Tests;

public sealed class BotRuntimeTests
{
    [Fact]
    public void Bot_packages_have_no_server_or_persistence_dependency()
    {
        foreach (var type in new[] { typeof(ChatBotRuntime), typeof(SqliteChatBotInbox), typeof(BotEndpointExtensions) })
        {
            Assert.DoesNotContain(type.Assembly.GetReferencedAssemblies(), assembly =>
                assembly.Name!.StartsWith("Skopka.Chat.Server", StringComparison.Ordinal) ||
                assembly.Name.StartsWith("Skopka.Chat.Persistence", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(typeof(ChatBotRuntime).Assembly.GetReferencedAssemblies(), assembly =>
            assembly.Name == "Microsoft.Data.Sqlite" || assembly.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authenticated_updates_survive_restart_and_processing_ack_does_not_replay()
    {
        using var f = await BotFixture.CreateAsync();
        await f.AddAsync();
        using (var runtime = f.Runtime()) { Assert.Equal(1, await runtime.SynchronizeAsync()); }
        using var restarted = f.Runtime();
        var update = Assert.Single(await restarted.GetUpdatesAsync());
        Assert.Equal("synthetic message", update.Text);
        Assert.DoesNotContain(update.Text, update.ToString(), StringComparison.Ordinal);
        Assert.Single(await restarted.GetUpdatesAsync());
        await restarted.AcknowledgeUpdateAsync(update.UpdateId);
        await restarted.AcknowledgeUpdateAsync(update.UpdateId);
        await restarted.SynchronizeAsync();
        Assert.Empty(await restarted.GetUpdatesAsync());
        Assert.Equal(2, f.Acknowledged);
    }

    [Fact]
    public async Task Ack_failure_preserves_one_durable_update()
    {
        using var f = await BotFixture.CreateAsync();
        await f.AddAsync();
        using var runtime = f.Runtime();
        f.FailAcknowledgement = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => runtime.SynchronizeAsync().AsTask());
        Assert.Single(await runtime.GetUpdatesAsync());
        f.FailAcknowledgement = false;
        await runtime.SynchronizeAsync();
        Assert.Single(await runtime.GetUpdatesAsync());
    }

    [Fact]
    public async Task Denial_and_operator_revision_or_expiry_fail_closed_before_send()
    {
        using var f = await BotFixture.CreateAsync();
        using var runtime = f.Runtime();
        var original = f.Grant!;
        ChatBotConsent?[] denied = [null, original with { ProfileRevision = Guid.NewGuid() },
            original with { BotUserId = UserId.New() }, original with { ConversationId = ConversationId.New() },
            original with { UserId = f.Bot.UserId }, original with { ExpiresAt = BotFixture.Now }, original with { GrantId = Guid.Empty }];
        foreach (var grant in denied)
        {
            f.Grant = grant;
            await Assert.ThrowsAsync<ChatBotException>(() => runtime.SendMessageAsync(f.Conversation, Guid.NewGuid(), "synthetic answer").AsTask());
        }
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task Missing_consent_is_permanently_suppressed_and_block_drops_queued_updates()
    {
        using var f = await BotFixture.CreateAsync();
        var grant = f.Grant!;
        f.Grant = null;
        await f.AddAsync();
        using var runtime = f.Runtime();
        await runtime.SynchronizeAsync();
        f.Grant = grant;
        await runtime.SynchronizeAsync();
        Assert.Empty(await runtime.GetUpdatesAsync());
        await f.AddAsync("synthetic later message");
        await runtime.SynchronizeAsync();
        Assert.Single(await runtime.GetUpdatesAsync());
        f.Grant = grant with { GrantId = Guid.NewGuid() };
        Assert.Empty(await runtime.GetUpdatesAsync());
        f.Grant = grant;
        Assert.Empty(await runtime.GetUpdatesAsync());
    }

    [Fact]
    public async Task Consent_service_failure_neither_stores_nor_acknowledges()
    {
        using var f = await BotFixture.CreateAsync();
        await f.AddAsync();
        f.FailConsent = true;
        using var runtime = f.Runtime();
        await Assert.ThrowsAsync<ChatBotException>(() => runtime.SynchronizeAsync().AsTask());
        Assert.Empty(await f.Inbox.ReadAsync(0, 20));
        Assert.Equal(0, f.Acknowledged);
    }

    [Fact]
    public async Task Authentication_precedes_durable_storage_and_acknowledgement()
    {
        using var f = await BotFixture.CreateAsync();
        await f.AddAsync();
        // Missing recipient private keys must fail before plaintext storage or acknowledgement.
        await f.Keys.DeleteAsync(f.Bot.DeviceId);
        using var runtime = f.Runtime();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SynchronizeAsync().AsTask());
        Assert.Empty(await f.Inbox.ReadAsync(0, 20));
        Assert.Equal(0, f.Acknowledged);
    }

    [Fact]
    public async Task Tampered_signature_never_reaches_storage_or_acknowledgement()
    {
        using var f = await BotFixture.CreateAsync();
        await f.AddAsync();
        var original = f.Pending[0].Envelope;
        var signature = original.Signature.ToArray();
        signature[0] ^= 1;
        f.Pending[0] = new(new EncryptedEnvelope(original.ProtocolVersion, original.MessageId, original.ConversationId,
            original.SenderDeviceId, original.RecipientDeviceId, original.SenderSigningKeyId, original.RecipientEncryptionKeyId,
            original.SentAt, original.ExpiresAt, original.EphemeralPublicKey.Span, original.Nonce.Span, original.Ciphertext.Span,
            original.AuthenticationTag.Span, signature), BotFixture.Now);
        using var runtime = f.Runtime();
        await Assert.ThrowsAsync<ChatCryptographicException>(() => runtime.SynchronizeAsync().AsTask());
        Assert.Empty(await f.Inbox.ReadAsync(0, 20));
        Assert.Equal(0, f.Acknowledged);
    }

    [Fact]
    public async Task Durable_storage_conflict_prevents_transport_acknowledgement()
    {
        using var f = await BotFixture.CreateAsync();
        var original = await f.AddAsync();
        await f.Inbox.StoreAsync(new ReceivedChatContent(original.DeliveryMessageId, original.ConversationId, original.SenderUserId,
            original.SenderDeviceId, original.SentAt, new ChatTextContent(original.Content.ContentId, "synthetic conflict")), f.Grant!.GrantId);
        using var runtime = f.Runtime();
        await Assert.ThrowsAsync<ChatBotException>(() => runtime.SynchronizeAsync().AsTask());
        Assert.Equal(0, f.Acknowledged);
        Assert.Equal("synthetic conflict", Assert.Single(await f.Inbox.ReadAsync(0, 20)).Text);
    }

    [Fact]
    public async Task Incomplete_send_retries_exact_ciphertext_after_restart_and_conflicting_request_is_rejected()
    {
        using var f = await BotFixture.CreateAsync();
        var request = Guid.NewGuid();
        f.FailSendOnce = true;
        using (var first = f.Runtime())
        {
            Assert.False((await first.SendMessageAsync(f.Conversation, request, "synthetic answer")).Succeeded);
        }
        using var second = f.Runtime();
        Assert.True((await second.SendMessageAsync(f.Conversation, request, "synthetic answer")).Succeeded);
        Assert.Equal(2, f.Sent.Count);
        Assert.Equal(CanonicalEnvelopeEncoding.EncodeEnvelope(f.Sent[0]), CanonicalEnvelopeEncoding.EncodeEnvelope(f.Sent[1]));
        var decrypted = await new ChatCryptoService(f.Keys).DecryptContentAsync(f.Sent[1], f.Bot);
        Assert.Equal("synthetic answer", Assert.IsType<ChatTextContent>(decrypted).Text);
        await Assert.ThrowsAsync<ChatBotException>(() => second.SendMessageAsync(f.Conversation, request, "different synthetic answer").AsTask());
        f.Grant = f.Grant! with { GrantId = Guid.NewGuid() };
        await Assert.ThrowsAsync<ChatBotException>(() => second.SendMessageAsync(f.Conversation, request, "synthetic answer").AsTask());
        Assert.Equal(2, f.Sent.Count);
    }

    [Fact]
    public async Task Unrelated_conversations_and_oversized_text_never_reach_transport()
    {
        using var f = await BotFixture.CreateAsync();
        using var runtime = f.Runtime();
        await Assert.ThrowsAsync<ChatBotException>(() => runtime.SendMessageAsync(ConversationId.New(), Guid.NewGuid(), "synthetic").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.SendMessageAsync(f.Conversation, Guid.NewGuid(), new string('x', ChatBotLimits.MaxTextUtf8Bytes + 1)).AsTask());
        Assert.Empty(f.Sent);
        await f.AddAsync(new string('x', ChatBotLimits.MaxTextUtf8Bytes + 1));
        await runtime.SynchronizeAsync();
        Assert.Empty(await runtime.GetUpdatesAsync());
        Assert.Equal(1, f.Acknowledged);
    }
}
