using Skopka.Chat.Attachments;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.UI;

namespace Skopka.Chat.UI.Core.Tests;

public sealed class ChatViewModelTests
{
    private static readonly ConversationId ConversationId =
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly ConversationId OtherConversationId =
        new(Guid.Parse("10000000-0000-0000-0000-000000000002"));
    private static readonly UserId CurrentUserId =
        new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly UserId PeerUserId =
        new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    private static readonly DeviceId CurrentDeviceId =
        new(Guid.Parse("30000000-0000-0000-0000-000000000001"));
    private static readonly DeviceId PeerDeviceId =
        new(Guid.Parse("30000000-0000-0000-0000-000000000002"));

    [Fact]
    public void Core_assembly_depends_on_client_but_not_ui_framework_or_server()
    {
        var references = typeof(ChatViewModel).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();

        Assert.Contains("Skopka.Chat.Client", references);
        Assert.DoesNotContain("Skopka.Chat.UI.Blazor", references);
        Assert.DoesNotContain("Skopka.Chat.Server", references);
        Assert.DoesNotContain("Skopka.Chat.Persistence.PostgreSql", references);
        Assert.DoesNotContain(references, item => item?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Draft_reply_send_applies_local_echo_and_clears_composer()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId);
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var original = IncomingText(1, "original");
        model.Apply(original);
        model.BeginReply(original.Content.ContentId);
        model.SetDraftText("answer");

        var sent = await model.TrySendDraftAsync();

        Assert.True(sent);
        var content = Assert.IsType<ChatTextContent>(Assert.Single(sender.Sent).Content);
        Assert.Equal("answer", content.Text);
        Assert.Equal(original.Content.ContentId, content.ReplyToContentId);
        Assert.Equal(2, model.Messages.Count);
        Assert.Equal(CurrentUserId, model.Messages[1].SenderUserId);
        Assert.Equal(string.Empty, model.DraftText);
        Assert.Null(model.ReplyTarget);
        Assert.False(model.IsSendingDraft);
        Assert.False(model.HasCommandError);
    }

    [Fact]
    public async Task Expected_send_failure_preserves_draft_and_reply_without_error_text()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId) { FailExpectedly = true };
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var original = IncomingText(1, "private original");
        model.Apply(original);
        model.BeginReply(original.Content.ContentId);
        model.SetDraftText("private draft");

        var sent = await model.TrySendDraftAsync();

        Assert.False(sent);
        Assert.Equal("private draft", model.DraftText);
        Assert.NotNull(model.ReplyTarget);
        Assert.True(model.HasCommandError);
        Assert.DoesNotContain("private", model.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", ChatContentSendResult.Failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reaction_toggle_emits_add_then_remove_and_updates_projection()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId);
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var original = IncomingText(1, "message");
        model.Apply(original);

        Assert.True(await model.ToggleReactionAsync(original.Content.ContentId, "👍"));
        var added = Assert.IsType<ChatReactionContent>(sender.Sent[0].Content);
        Assert.Equal(ChatReactionOperation.Add, added.Operation);
        Assert.Equal(CurrentUserId, Assert.Single(Assert.Single(model.Messages).Reactions).SenderUserIds[0]);

        Assert.True(await model.ToggleReactionAsync(original.Content.ContentId, "👍"));
        var removed = Assert.IsType<ChatReactionContent>(sender.Sent[1].Content);
        Assert.Equal(ChatReactionOperation.Remove, removed.Operation);
        Assert.Empty(Assert.Single(model.Messages).Reactions);
    }

    [Fact]
    public async Task Attachment_send_reply_and_reaction_use_the_shared_timeline()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId);
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var attachment = CreateAttachment(8);

        Assert.True(await model.SendAttachmentAsync(attachment));
        var projected = Assert.IsType<ProjectedChatAttachment>(Assert.Single(model.Timeline));
        Assert.Equal("photo.jpg", projected.FileName);
        Assert.Empty(model.Messages);

        model.BeginReply(projected.ContentId);
        Assert.Same(projected, model.ReplyTargetItem);
        Assert.True(await model.ToggleReactionAsync(projected.ContentId, "👍"));
        Assert.Single(Assert.IsType<ProjectedChatAttachment>(Assert.Single(model.Timeline)).Reactions);
    }

    [Fact]
    public async Task Forward_to_another_conversation_copies_only_text_and_forward_marker()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId);
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var source = IncomingText(1, "copy me", new ChatContentId(Guid.Parse("40000000-0000-0000-0000-000000000099")));
        model.Apply(source);

        Assert.True(await model.ForwardAsync(source.Content.ContentId, OtherConversationId));

        var sent = Assert.Single(sender.Sent);
        Assert.Equal(OtherConversationId, sent.ConversationId);
        var forwarded = Assert.IsType<ChatTextContent>(sent.Content);
        Assert.Equal("copy me", forwarded.Text);
        Assert.True(forwarded.IsForwarded);
        Assert.Null(forwarded.ReplyToContentId);
        Assert.NotEqual(source.Content.ContentId, forwarded.ContentId);
        Assert.Single(model.Messages);
    }

    [Fact]
    public async Task Edit_own_text_emits_event_applies_echo_and_restores_previous_composer()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId);
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var replyTarget = IncomingText(1, "peer message");
        var own = OwnText(2, "own original");
        model.Apply(replyTarget);
        model.Apply(own);
        model.SetDraftText("unsent draft");
        model.BeginReply(replyTarget.Content.ContentId);

        model.BeginEdit(own.Content.ContentId);
        Assert.True(model.IsEditing);
        Assert.Equal("own original", model.DraftText);
        Assert.Equal(own.Content.ContentId, model.EditTarget?.ContentId);
        Assert.Null(model.ReplyTargetItem);
        Assert.False(model.CanSendDraft);
        model.SetDraftText("own edited");

        Assert.True(await model.TrySendDraftAsync());

        var edit = Assert.IsType<ChatEditContent>(Assert.Single(sender.Sent).Content);
        Assert.Equal(own.Content.ContentId, edit.TargetContentId);
        Assert.Equal(ChatEditField.Text, edit.Field);
        Assert.Equal("own edited", edit.NewValue);
        var projected = Assert.Single(model.Messages, item => item.ContentId == own.Content.ContentId);
        Assert.Equal("own edited", projected.Text);
        Assert.True(projected.IsEdited);
        Assert.False(model.IsEditing);
        Assert.Equal("unsent draft", model.DraftText);
        Assert.Equal(replyTarget.Content.ContentId, model.ReplyTarget?.ContentId);
    }

    [Fact]
    public async Task Edit_expected_failure_preserves_mode_and_peer_content_cannot_be_edited()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId) { FailExpectedly = true };
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var peer = IncomingText(1, "peer");
        var own = OwnText(2, "own");
        model.Apply(peer);
        model.Apply(own);

        Assert.Throws<ArgumentException>(() => model.BeginEdit(peer.Content.ContentId));
        model.BeginEdit(own.Content.ContentId);
        model.SetDraftText("changed");

        Assert.False(await model.TrySendDraftAsync());
        Assert.True(model.IsEditing);
        Assert.Equal("changed", model.DraftText);
        Assert.True(model.HasCommandError);

        model.CancelEdit();
        Assert.False(model.IsEditing);
        Assert.Equal(string.Empty, model.DraftText);
    }

    [Fact]
    public async Task Attachment_caption_edit_can_clear_the_caption()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId);
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        var attachment = CreateAttachment(8);
        Assert.True(await model.SendAttachmentAsync(attachment));

        model.BeginEdit(attachment.ContentId);
        Assert.Equal("caption", model.DraftText);
        model.SetDraftText(string.Empty);
        Assert.True(model.CanSendDraft);
        Assert.True(await model.TrySendDraftAsync());

        var edit = Assert.IsType<ChatEditContent>(sender.Sent[1].Content);
        Assert.Equal(ChatEditField.AttachmentCaption, edit.Field);
        Assert.Null(edit.NewValue);
        var projected = Assert.IsType<ProjectedChatAttachment>(Assert.Single(model.Timeline));
        Assert.Null(projected.Caption);
        Assert.True(projected.IsEdited);
    }

    [Fact]
    public async Task Invalid_success_echo_is_rejected_and_draft_is_not_lost()
    {
        var sender = new RecordingSender(CurrentUserId, CurrentDeviceId)
        {
            EchoUserId = PeerUserId,
        };
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        model.SetDraftText("keep me");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await model.TrySendDraftAsync());

        Assert.Equal("keep me", model.DraftText);
        Assert.False(model.IsSendingDraft);
        Assert.False(model.HasCommandError);
        Assert.Empty(model.Messages);
    }

    [Fact]
    public async Task Draft_edited_during_send_is_not_cleared_by_older_success()
    {
        var sender = new DeferredSender();
        var model = new ChatViewModel(ConversationId, CurrentUserId, sender);
        model.SetDraftText("first");

        var send = model.TrySendDraftAsync().AsTask();
        Assert.True(model.IsSendingDraft);
        model.SetDraftText("second");
        var sentContent = Assert.IsType<ChatTextContent>(sender.Content);
        sender.Completion.SetResult(ChatContentSendResult.Success(new ReceivedChatContent(
            new MessageId(Guid.Parse("70000000-0000-0000-0000-000000000099")),
            ConversationId,
            CurrentUserId,
            CurrentDeviceId,
            DateTimeOffset.Parse("2026-09-01T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            sentContent)));

        Assert.True(await send);
        Assert.Equal("second", model.DraftText);
        Assert.Equal("first", Assert.Single(model.Messages).Text);
        Assert.False(model.IsSendingDraft);
    }

    [Fact]
    public void Apply_rejects_other_conversation_and_duplicate_does_not_notify()
    {
        var model = new ChatViewModel(ConversationId, CurrentUserId, new RecordingSender(CurrentUserId, CurrentDeviceId));
        var incoming = IncomingText(1, "message");
        var changes = 0;
        model.StateChanged += (_, _) => changes++;

        Assert.Equal(ChatProjectionApplyResult.Applied, model.Apply(incoming));
        Assert.Equal(ChatProjectionApplyResult.Duplicate, model.Apply(incoming));
        Assert.Equal(1, changes);

        var wrongConversation = new ReceivedChatContent(
            new MessageId(Guid.Parse("50000000-0000-0000-0000-000000000099")),
            OtherConversationId,
            PeerUserId,
            PeerDeviceId,
            DateTimeOffset.Parse("2026-09-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new ChatTextContent(new ChatContentId(Guid.Parse("60000000-0000-0000-0000-000000000099")), "wrong"));
        Assert.Throws<ArgumentException>(() => model.Apply(wrongConversation));
    }

    private static ReceivedChatContent IncomingText(int sequence, string text, ChatContentId? replyTo = null) =>
        new(
            Id<MessageId>(5, sequence),
            ConversationId,
            PeerUserId,
            PeerDeviceId,
            DateTimeOffset.Parse("2026-09-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddMinutes(sequence),
            new ChatTextContent(Id<ChatContentId>(6, sequence), text, replyTo));

    private static ReceivedChatContent OwnText(int sequence, string text) =>
        new(
            Id<MessageId>(5, sequence),
            ConversationId,
            CurrentUserId,
            CurrentDeviceId,
            DateTimeOffset.Parse("2026-09-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddMinutes(sequence),
            new ChatTextContent(Id<ChatContentId>(6, sequence), text));

    private static ChatAttachmentContent CreateAttachment(int sequence)
    {
        const long plaintextLength = 10;
        const long ciphertextLength = plaintextLength + 4 + 16;
        const int chunkBytes = ChatAttachmentCryptoService.MinChunkPlaintextBytes;
        return new ChatAttachmentContent(
            Id<ChatContentId>(6, sequence),
            new AttachmentId(Guid.Parse($"80000000-0000-0000-0000-{sequence:000000000000}")),
            "photo.jpg",
            "image/jpeg",
            plaintextLength,
            ciphertextLength,
            chunkBytes,
            new byte[AttachmentStorageLimits.Sha256Bytes],
            new byte[32],
            new byte[16],
            "caption");
    }

    private static T Id<T>(int prefix, int sequence)
    {
        var guid = Guid.Parse($"{prefix}0000000-0000-0000-0000-{sequence:000000000000}");
        return typeof(T) == typeof(MessageId)
            ? (T)(object)new MessageId(guid)
            : (T)(object)new ChatContentId(guid);
    }

    private sealed class RecordingSender(UserId currentUserId, DeviceId currentDeviceId) : IChatContentSender
    {
        private int _sequence;

        internal List<(ConversationId ConversationId, ChatContent Content)> Sent { get; } = [];

        internal bool FailExpectedly { get; init; }

        internal UserId? EchoUserId { get; init; }

        public ValueTask<ChatContentSendResult> SendAsync(
            ConversationId conversationId,
            ChatContent content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add((conversationId, content));
            if (FailExpectedly)
            {
                return ValueTask.FromResult(ChatContentSendResult.Failed);
            }

            _sequence++;
            var delivery = new ReceivedChatContent(
                Id<MessageId>(7, _sequence),
                conversationId,
                EchoUserId ?? currentUserId,
                currentDeviceId,
                DateTimeOffset.Parse("2026-09-01T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddMinutes(_sequence),
                content);
            return ValueTask.FromResult(ChatContentSendResult.Success(delivery));
        }
    }

    private sealed class DeferredSender : IChatContentSender
    {
        internal TaskCompletionSource<ChatContentSendResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ChatContent? Content { get; private set; }

        public ValueTask<ChatContentSendResult> SendAsync(
            ConversationId conversationId,
            ChatContent content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Content = content;
            return new ValueTask<ChatContentSendResult>(Completion.Task);
        }
    }
}
