using System.Text;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Tests;

public sealed class ChatContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConversationId Conversation = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly UserId AliceUser = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly UserId BobUser = new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    private static readonly DeviceId AliceDevice = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));
    private static readonly DeviceId BobDevice = new(Guid.Parse("30000000-0000-0000-0000-000000000002"));

    [Fact]
    public void Reply_and_forward_encoding_is_deterministic_and_round_trips()
    {
        var contentId = new ChatContentId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var replyTo = new ChatContentId(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"));
        var content = new ChatTextContent(contentId, "hello", replyTo, isForwarded: true);

        var encoded = ChatContentEncoding.Encode(content);
        var decoded = Assert.IsType<ChatTextContent>(ChatContentEncoding.Decode(encoded));

        Assert.Equal(
            "736B6F706B612E636861742E636F6E74656E74315400112233445566778899AABBCCDDEEFF33FFEEDDCCBBAA9988776655443322110068656C6C6F",
            Convert.ToHexString(encoded));
        Assert.Equal(contentId, decoded.ContentId);
        Assert.Equal(replyTo, decoded.ReplyToContentId);
        Assert.Equal("hello", decoded.Text);
        Assert.True(decoded.IsForwarded);
        Assert.DoesNotContain("hello", decoded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reaction_encoding_round_trips_without_exposing_the_token_in_ToString()
    {
        var reaction = new ChatReactionContent(
            Id(11),
            Id(10),
            "👍🏽",
            ChatReactionOperation.Add);

        var decoded = Assert.IsType<ChatReactionContent>(
            ChatContentEncoding.Decode(ChatContentEncoding.Encode(reaction)));

        Assert.Equal(reaction.ContentId, decoded.ContentId);
        Assert.Equal(reaction.TargetContentId, decoded.TargetContentId);
        Assert.Equal(reaction.Reaction, decoded.Reaction);
        Assert.Equal(ChatReactionOperation.Add, decoded.Operation);
        Assert.DoesNotContain(reaction.Reaction, decoded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mentioned_text_uses_content_v4_and_round_trips_structured_targets()
    {
        var contentId = new ChatContentId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var mentionedUser = new UserId(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"));
        var content = new ChatTextContent(
            contentId,
            "@bob @all",
            mentions: [ChatMention.Everyone, new ChatMention(mentionedUser)]);

        var encoded = ChatContentEncoding.Encode(content);
        var decoded = Assert.IsType<ChatTextContent>(ChatContentEncoding.Decode(encoded));

        Assert.Equal(
            "736B6F706B612E636861742E636F6E74656E74345400112233445566778899AABBCCDDEEFF30000255102132435465768798A9BACBDCEDFE0F2A40626F622040616C6C",
            Convert.ToHexString(encoded));
        Assert.Equal([new ChatMention(mentionedUser), ChatMention.Everyone], decoded.Mentions);
        Assert.True(decoded.MentionsUser(mentionedUser));
        Assert.True(decoded.MentionsEveryone);
        Assert.DoesNotContain("@bob", decoded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mention_validation_rejects_empty_duplicate_and_excessive_targets()
    {
        Assert.Throws<ArgumentException>(() => new ChatMention(default));
        Assert.Throws<ArgumentException>(() => new ChatTextContent(
            Id(1),
            "duplicate",
            mentions: [ChatMention.Everyone, ChatMention.Everyone]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatTextContent(
            Id(1),
            "too many",
            mentions: Enumerable.Range(1, ChatContentLimits.MaxMentions + 1)
                .Select(value => new ChatMention(new UserId(Id(value).Value)))
                .ToArray()));

        var canonical = ChatContentEncoding.Encode(new ChatTextContent(
            Id(2),
            "order",
            mentions: [new ChatMention(new UserId(Id(3).Value)), ChatMention.Everyone]));
        var nonCanonical = canonical[..40]
            .Concat([(byte)'*'])
            .Concat(canonical[40..57])
            .Concat(canonical[58..])
            .ToArray();
        Assert.Throws<ChatContentFormatException>(() => ChatContentEncoding.Decode(nonCanonical));

        var duplicateUsers = ChatContentEncoding.Encode(new ChatTextContent(
            Id(2),
            "duplicate wire",
            mentions: [new ChatMention(new UserId(Id(3).Value)), new ChatMention(new UserId(Id(4).Value))]));
        duplicateUsers.AsSpan(41, 16).CopyTo(duplicateUsers.AsSpan(58, 16));
        Assert.Throws<ChatContentFormatException>(() => ChatContentEncoding.Decode(duplicateUsers));
    }

    [Fact]
    public void Edit_encoding_is_deterministic_and_round_trips_without_exposing_plaintext()
    {
        var contentId = new ChatContentId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var targetId = new ChatContentId(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"));
        var edit = new ChatEditContent(contentId, targetId, ChatEditField.Text, "edited");

        var encoded = ChatContentEncoding.Encode(edit);
        var decoded = Assert.IsType<ChatEditContent>(ChatContentEncoding.Decode(encoded));

        Assert.Equal(
            "736B6F706B612E636861742E636F6E74656E74334500112233445566778899AABBCCDDEEFFFFEEDDCCBBAA998877665544332211005431656469746564",
            Convert.ToHexString(encoded));
        Assert.Equal(contentId, decoded.ContentId);
        Assert.Equal(targetId, decoded.TargetContentId);
        Assert.Equal(ChatEditField.Text, decoded.Field);
        Assert.Equal("edited", decoded.NewValue);
        Assert.DoesNotContain("edited", decoded.ToString(), StringComparison.Ordinal);

        var clearCaption = new ChatEditContent(Id(14), Id(13), ChatEditField.AttachmentCaption, null);
        var decodedClear = Assert.IsType<ChatEditContent>(
            ChatContentEncoding.Decode(ChatContentEncoding.Encode(clearCaption)));
        Assert.Equal(ChatEditField.AttachmentCaption, decodedClear.Field);
        Assert.Null(decodedClear.NewValue);
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public void Decoder_rejects_malformed_or_unsupported_content_without_reflecting_it(byte[] payload)
    {
        var exception = Assert.Throws<ChatContentFormatException>(() => ChatContentEncoding.Decode(payload));

        Assert.Equal("Encrypted chat content is invalid or unsupported.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Content_boundaries_reject_invalid_ids_unicode_sizes_and_reaction_operations()
    {
        var maximum = new ChatTextContent(
            Id(3),
            new string('a', ChatContentLimits.MaxTextUtf8Bytes),
            Id(4),
            isForwarded: true);

        Assert.Equal(ProtocolLimits.MaxPlaintextBytes, ChatContentEncoding.Encode(maximum).Length);
        Assert.Throws<ArgumentException>(() => new ChatTextContent(default, "text"));
        Assert.Throws<ArgumentException>(() => new ChatTextContent(Id(1), "\ud800"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatTextContent(Id(1), new string('a', ChatContentLimits.MaxTextUtf8Bytes + 1)));
        Assert.Throws<ArgumentException>(() => new ChatTextContent(Id(1), "text", Id(1)));
        Assert.Throws<ArgumentException>(() =>
            new ChatReactionContent(Id(2), Id(1), "\r", ChatReactionOperation.Add));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatReactionContent(Id(2), Id(1), "x", (ChatReactionOperation)99));

        var maximumEdit = new ChatEditContent(
            Id(6),
            Id(5),
            ChatEditField.Text,
            new string('a', ChatContentLimits.MaxEditTextUtf8Bytes));
        Assert.Equal(ProtocolLimits.MaxPlaintextBytes, ChatContentEncoding.Encode(maximumEdit).Length);
        Assert.Throws<ArgumentException>(() =>
            new ChatEditContent(Id(2), default, ChatEditField.Text, "edit"));
        Assert.Throws<ArgumentException>(() =>
            new ChatEditContent(Id(2), Id(2), ChatEditField.Text, "edit"));
        Assert.Throws<ArgumentNullException>(() =>
            new ChatEditContent(Id(2), Id(1), ChatEditField.Text, null));
        Assert.Throws<ArgumentException>(() =>
            new ChatEditContent(Id(2), Id(1), ChatEditField.Text, "   "));
        Assert.Throws<ArgumentException>(() =>
            new ChatEditContent(Id(2), Id(1), ChatEditField.AttachmentCaption, string.Empty));
        Assert.Throws<ArgumentException>(() =>
            new ChatEditContent(Id(2), Id(1), ChatEditField.Text, "\ud800"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatEditContent(
                Id(2),
                Id(1),
                ChatEditField.Text,
                new string('a', ChatContentLimits.MaxEditTextUtf8Bytes + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatEditContent(Id(2), Id(1), (ChatEditField)99, "edit"));
    }

    [Fact]
    public void Forward_copies_text_but_drops_reply_and_original_attribution()
    {
        var original = new ChatTextContent(
            Id(1),
            "copy me @all",
            Id(2),
            mentions: [ChatMention.Everyone]);

        var forwarded = original.Forward(Id(3));

        Assert.Equal(Id(3), forwarded.ContentId);
        Assert.Equal(original.Text, forwarded.Text);
        Assert.Null(forwarded.ReplyToContentId);
        Assert.True(forwarded.IsForwarded);
        Assert.Empty(forwarded.Mentions);
    }

    [Fact]
    public async Task Typed_content_is_encrypted_and_keeps_one_content_id_across_device_fan_out()
    {
        var fixture = await ContentCryptoFixture.CreateAsync();
        var content = new ChatTextContent(Id(20), "hello devices", Id(19));
        var bobEnvelope = await fixture.AliceCrypto.EncryptContentAsync(
            content,
            fixture.ConversationId,
            MessageId.New(),
            fixture.Alice.DeviceId,
            fixture.Bob,
            fixture.Now);
        var charlieEnvelope = await fixture.AliceCrypto.EncryptContentAsync(
            content,
            fixture.ConversationId,
            MessageId.New(),
            fixture.Alice.DeviceId,
            fixture.Charlie,
            fixture.Now);

        var forBob = Assert.IsType<ChatTextContent>(
            await fixture.BobCrypto.DecryptContentAsync(bobEnvelope, fixture.Alice));
        var forCharlie = Assert.IsType<ChatTextContent>(
            await fixture.CharlieCrypto.DecryptContentAsync(charlieEnvelope, fixture.Alice));

        Assert.NotEqual(bobEnvelope.MessageId, charlieEnvelope.MessageId);
        Assert.Equal(content.ContentId, forBob.ContentId);
        Assert.Equal(content.ContentId, forCharlie.ContentId);
        Assert.Equal(content.Text, forBob.Text);
        Assert.Equal(content.ReplyToContentId, forCharlie.ReplyToContentId);
    }

    [Fact]
    public async Task Legacy_raw_text_remains_decryptable_but_is_not_silently_typed()
    {
        var fixture = await ContentCryptoFixture.CreateAsync();
        var envelope = await fixture.AliceCrypto.EncryptTextAsync(
            "legacy text",
            fixture.ConversationId,
            MessageId.New(),
            fixture.Alice.DeviceId,
            fixture.Bob,
            fixture.Now);

        var raw = await fixture.BobCrypto.DecryptAsync(envelope, fixture.Alice);
        var error = await Assert.ThrowsAsync<ChatContentFormatException>(async () =>
            await fixture.BobCrypto.DecryptContentAsync(envelope, fixture.Alice));

        Assert.Equal("legacy text", Encoding.UTF8.GetString(raw));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Malformed_authenticated_content_is_rejected_before_local_commit()
    {
        var fixture = await ContentCryptoFixture.CreateAsync();
        var envelope = await fixture.AliceCrypto.EncryptAsync(
            "not typed content"u8.ToArray(),
            fixture.ConversationId,
            MessageId.New(),
            fixture.Alice.DeviceId,
            fixture.Bob,
            fixture.Now);
        var local = new InMemoryReceivedMessageStore();
        var receiver = new ChatReceiver(fixture.BobCrypto, local);

        await Assert.ThrowsAsync<ChatContentFormatException>(async () =>
            await receiver.ReceiveContentAsync(envelope, fixture.Alice));

        Assert.Equal(0, local.Count);
    }

    [Fact]
    public async Task Typed_receiver_commits_a_retried_envelope_once()
    {
        var fixture = await ContentCryptoFixture.CreateAsync();
        var envelope = await fixture.AliceCrypto.EncryptContentAsync(
            new ChatTextContent(Id(25), "deliver once"),
            fixture.ConversationId,
            MessageId.New(),
            fixture.Alice.DeviceId,
            fixture.Bob,
            fixture.Now);
        var local = new InMemoryReceivedMessageStore();
        var receiver = new ChatReceiver(fixture.BobCrypto, local);

        var first = await receiver.ReceiveContentAsync(envelope, fixture.Alice);
        var duplicate = await receiver.ReceiveContentAsync(envelope, fixture.Alice);

        Assert.True(first.Added);
        Assert.NotNull(first.Delivery);
        Assert.False(duplicate.Added);
        Assert.Null(duplicate.Delivery);
        Assert.Equal(1, local.Count);
    }

    [Fact]
    public void Projection_folds_out_of_order_reactions_per_user_and_accepts_missing_reply_targets()
    {
        var targetId = Id(30);
        var missingReplyId = Id(29);
        var projection = new ChatConversationProjection(Conversation);
        var add = Delivery(
            new ChatReactionContent(Id(31), targetId, "❤", ChatReactionOperation.Add),
            BobUser,
            BobDevice,
            Now.AddSeconds(2));
        var remove = Delivery(
            new ChatReactionContent(Id(32), targetId, "❤", ChatReactionOperation.Remove),
            BobUser,
            new DeviceId(Guid.Parse("30000000-0000-0000-0000-000000000003")),
            Now.AddSeconds(3));
        var text = Delivery(
            new ChatTextContent(targetId, "reply body", missingReplyId),
            AliceUser,
            AliceDevice,
            Now);

        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(remove));
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(add));
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(text));

        var message = Assert.Single(projection.Snapshot());
        Assert.Equal(missingReplyId, message.ReplyToContentId);
        Assert.Empty(message.Reactions);

        var laterAdd = Delivery(
            new ChatReactionContent(Id(33), targetId, "❤", ChatReactionOperation.Add),
            BobUser,
            BobDevice,
            Now.AddSeconds(4));
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(laterAdd));

        var reaction = Assert.Single(Assert.Single(projection.Snapshot()).Reactions);
        Assert.Equal("❤", reaction.Reaction);
        Assert.Equal(1, reaction.Count);
        Assert.Equal(BobUser, Assert.Single(reaction.SenderUserIds));
    }

    [Fact]
    public void Projection_deduplicates_fan_out_and_excludes_conflicting_content_ids()
    {
        var projection = new ChatConversationProjection(Conversation);
        var original = Delivery(new ChatTextContent(Id(40), "one"), AliceUser, AliceDevice, Now);
        var fanOutCopy = new ReceivedChatContent(
            MessageId.New(),
            original.ConversationId,
            original.SenderUserId,
            original.SenderDeviceId,
            original.SentAt,
            new ChatTextContent(Id(40), "one"));
        var conflict = Delivery(new ChatTextContent(Id(40), "two"), AliceUser, AliceDevice, Now);

        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(original));
        Assert.Equal(ChatProjectionApplyResult.Duplicate, projection.Apply(fanOutCopy));
        Assert.Equal(ChatProjectionApplyResult.Conflict, projection.Apply(conflict));

        Assert.Empty(projection.Snapshot());
        Assert.Equal(Id(40), Assert.Single(projection.ConflictedContentIds()));
        Assert.DoesNotContain("two", conflict.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_orders_author_edits_by_timestamp_then_content_id_even_before_target()
    {
        var targetId = Id(60);
        var projection = new ChatConversationProjection(Conversation);
        var laterEdit = Delivery(
            new ChatEditContent(Id(63), targetId, ChatEditField.Text, "latest"),
            AliceUser,
            new DeviceId(Guid.Parse("30000000-0000-0000-0000-000000000004")),
            Now.AddSeconds(3));
        var earlierEdit = Delivery(
            new ChatEditContent(Id(62), targetId, ChatEditField.Text, "earlier"),
            AliceUser,
            AliceDevice,
            Now.AddSeconds(3));
        var unauthorizedEdit = Delivery(
            new ChatEditContent(Id(64), targetId, ChatEditField.Text, "forged"),
            BobUser,
            BobDevice,
            Now.AddSeconds(4));
        var original = Delivery(new ChatTextContent(targetId, "original"), AliceUser, AliceDevice, Now);

        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(laterEdit));
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(unauthorizedEdit));
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(earlierEdit));
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(original));

        var message = Assert.Single(projection.Snapshot());
        Assert.Equal("latest", message.Text);
        Assert.True(message.IsEdited);
        Assert.Equal(laterEdit.SentAt, message.EditedAt);
        Assert.DoesNotContain("forged", message.ToString(), StringComparison.Ordinal);

        var newestEdit = Delivery(
            new ChatEditContent(Id(61), targetId, ChatEditField.Text, "newest"),
            AliceUser,
            AliceDevice,
            Now.AddSeconds(5));
        projection.Apply(newestEdit);
        message = Assert.Single(projection.Snapshot());
        Assert.Equal("newest", message.Text);
        Assert.Equal(newestEdit.SentAt, message.EditedAt);
    }

    [Fact]
    public void Projection_ignores_edit_field_that_does_not_match_the_target_type()
    {
        var targetId = Id(70);
        var projection = new ChatConversationProjection(Conversation);

        projection.Apply(Delivery(new ChatTextContent(targetId, "original"), AliceUser, AliceDevice, Now));
        projection.Apply(Delivery(
            new ChatEditContent(Id(71), targetId, ChatEditField.AttachmentCaption, "wrong field"),
            AliceUser,
            AliceDevice,
            Now.AddSeconds(1)));

        var message = Assert.Single(projection.Snapshot());
        Assert.Equal("original", message.Text);
        Assert.False(message.IsEdited);
        Assert.Null(message.EditedAt);
    }

    [Fact]
    public void Projection_deduplicates_edit_fan_out_and_removes_a_conflicting_edit()
    {
        var targetId = Id(80);
        var editId = Id(81);
        var projection = new ChatConversationProjection(Conversation);
        var original = Delivery(new ChatTextContent(targetId, "original"), AliceUser, AliceDevice, Now);
        var edit = Delivery(
            new ChatEditContent(editId, targetId, ChatEditField.Text, "edited"),
            AliceUser,
            AliceDevice,
            Now.AddSeconds(1));
        var fanOutCopy = new ReceivedChatContent(
            MessageId.New(),
            edit.ConversationId,
            edit.SenderUserId,
            edit.SenderDeviceId,
            edit.SentAt,
            new ChatEditContent(editId, targetId, ChatEditField.Text, "edited"));
        var conflict = Delivery(
            new ChatEditContent(editId, targetId, ChatEditField.Text, "conflict"),
            AliceUser,
            AliceDevice,
            Now.AddSeconds(1));

        projection.Apply(original);
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(edit));
        Assert.Equal(ChatProjectionApplyResult.Duplicate, projection.Apply(fanOutCopy));
        Assert.Equal(ChatProjectionApplyResult.Conflict, projection.Apply(conflict));

        var message = Assert.Single(projection.Snapshot());
        Assert.Equal("original", message.Text);
        Assert.False(message.IsEdited);
        Assert.Contains(editId, projection.ConflictedContentIds());
    }

    public static IEnumerable<object[]> MalformedPayloads()
    {
        var valid = ChatContentEncoding.Encode(new ChatTextContent(Id(50), "safe"));
        yield return [Array.Empty<byte>()];
        yield return ["skopka.chat.content"u8.ToArray()];
        yield return [Mutate(valid, 19, (byte)'2')];
        yield return [Mutate(valid, 20, (byte)'X')];

        var emptyId = valid.ToArray();
        emptyId.AsSpan(21, 16).Clear();
        yield return [emptyId];
        yield return [Mutate(valid, 37, (byte)'4')];
        yield return [Mutate(valid, ^1, 0xff)];
        yield return [new byte[ProtocolLimits.MaxPlaintextBytes + 1]];

        var emptyReaction = ChatContentEncoding.Encode(
            new ChatReactionContent(Id(52), Id(51), "x", ChatReactionOperation.Add));
        yield return [emptyReaction[..^1]];

        var validEdit = ChatContentEncoding.Encode(
            new ChatEditContent(Id(54), Id(53), ChatEditField.Text, "edit"));
        yield return [Mutate(validEdit, 53, (byte)'X')];
        yield return [Mutate(validEdit, 54, (byte)'2')];
        yield return [Mutate(validEdit, 54, (byte)'0')];
        yield return [validEdit[..55]];
        var selfEdit = validEdit.ToArray();
        selfEdit.AsSpan(21, 16).CopyTo(selfEdit.AsSpan(37, 16));
        yield return [selfEdit];
    }

    private static byte[] Mutate(byte[] source, Index index, byte value)
    {
        var copy = source.ToArray();
        copy[index] = value;
        return copy;
    }

    private static ChatContentId Id(int value) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{value:x12}"));

    private static ReceivedChatContent Delivery(
        ChatContent content,
        UserId senderUserId,
        DeviceId senderDeviceId,
        DateTimeOffset sentAt) =>
        new(MessageId.New(), Conversation, senderUserId, senderDeviceId, sentAt, content);

    private sealed class ContentCryptoFixture
    {
        private ContentCryptoFixture(
            PublicDevice alice,
            PublicDevice bob,
            PublicDevice charlie,
            ChatCryptoService aliceCrypto,
            ChatCryptoService bobCrypto,
            ChatCryptoService charlieCrypto)
        {
            Alice = alice;
            Bob = bob;
            Charlie = charlie;
            AliceCrypto = aliceCrypto;
            BobCrypto = bobCrypto;
            CharlieCrypto = charlieCrypto;
        }

        public DateTimeOffset Now { get; } = ChatContentTests.Now;
        public ConversationId ConversationId { get; } = ConversationId.New();
        public PublicDevice Alice { get; }
        public PublicDevice Bob { get; }
        public PublicDevice Charlie { get; }
        public ChatCryptoService AliceCrypto { get; }
        public ChatCryptoService BobCrypto { get; }
        public ChatCryptoService CharlieCrypto { get; }

        public static async Task<ContentCryptoFixture> CreateAsync()
        {
            var aliceStore = new InMemoryDeviceKeyStore();
            var bobStore = new InMemoryDeviceKeyStore();
            var charlieStore = new InMemoryDeviceKeyStore();
            var alice = await new DeviceIdentityService(aliceStore).CreateAsync(
                UserId.New(), DeviceId.New(), ChatContentTests.Now);
            var bob = await new DeviceIdentityService(bobStore).CreateAsync(
                UserId.New(), DeviceId.New(), ChatContentTests.Now);
            var charlie = await new DeviceIdentityService(charlieStore).CreateAsync(
                UserId.New(), DeviceId.New(), ChatContentTests.Now);
            return new ContentCryptoFixture(
                alice,
                bob,
                charlie,
                new ChatCryptoService(aliceStore),
                new ChatCryptoService(bobStore),
                new ChatCryptoService(charlieStore));
        }
    }
}
