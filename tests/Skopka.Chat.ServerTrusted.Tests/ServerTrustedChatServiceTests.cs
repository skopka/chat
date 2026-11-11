using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skopka.Chat.Attachments;

namespace Skopka.Chat.ServerTrusted.Tests;

public sealed class ServerTrustedChatServiceTests
{
    private static readonly Guid Learner = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Teacher = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Parent = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Manager = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid Stranger = Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly Guid Surrogate = Guid.Parse("10000000-0000-0000-0000-0000000000ff");

    [Fact]
    public async Task Get_or_create_by_host_key_is_idempotent_and_keeps_history_across_close_and_reopen()
    {
        var fixture = new Fixture();
        var first = await fixture.CreateMentorChatAsync();
        await fixture.SendAsync(Learner, first.Id, "synthetic question");
        Assert.Equal(ServerTrustedChatStatus.Success, await fixture.Service.CloseAsync(first.Id));

        var closed = await fixture.Service.SendAsync(Teacher, first.Id, Text("after close"));
        Assert.Equal(ServerTrustedChatStatus.Closed, closed.Status);

        var again = await fixture.CreateMentorChatAsync();
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(ServerTrustedChatStatus.Success, await fixture.Service.ReopenAsync(first.Id));
        var history = await fixture.Service.GetHistoryAsync(Teacher, first.Id);
        Assert.Equal("synthetic question", Assert.Single(history.Value!.Messages).Text);
    }

    [Fact]
    public async Task Direct_conversation_requires_exactly_two_writers()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.GetOrCreateConversationAsync("k:1", TrustedConversationKind.Direct,
            [new TrustedParticipantSpec(Learner, TrustedParticipantRole.Writer, "learner")]);
        Assert.Equal(ServerTrustedChatStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Observer_reads_and_reports_but_cannot_write_and_stranger_gets_generic_rejection()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync(withParent: true);
        var message = await fixture.SendAsync(Teacher, conversation.Id, "synthetic lesson note");

        var parentRead = await fixture.Service.GetHistoryAsync(Parent, conversation.Id);
        Assert.True(parentRead.IsSuccess);
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.SendAsync(Parent, conversation.Id, Text("x"))).Status);
        Assert.True((await fixture.Service.ReportAsync(Parent, message.Id, "abuse")).IsSuccess);

        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.GetHistoryAsync(Stranger, conversation.Id)).Status);
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden,
            (await fixture.Service.GetHistoryAsync(Stranger, TrustedConversationId.New())).Status);
    }

    [Fact]
    public async Task Host_policy_can_deny_one_participant_without_affecting_the_other()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        await fixture.SendAsync(Learner, conversation.Id, "synthetic");
        fixture.Policy.Deny = context => context.ActorUserId == Teacher && context.Operation == ServerTrustedChatOperation.ReadHistory;

        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.GetHistoryAsync(Teacher, conversation.Id)).Status);
        Assert.True((await fixture.Service.GetHistoryAsync(Learner, conversation.Id)).IsSuccess);
        Assert.Contains(fixture.Policy.Asked, item => item.Operation == ServerTrustedChatOperation.ReadHistory && item.ActorUserId == Learner);
    }

    [Fact]
    public async Task Revoking_stored_read_excludes_conversation_from_unread_total_and_reads()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        await fixture.SendAsync(Learner, conversation.Id, "one");
        await fixture.SendAsync(Learner, conversation.Id, "two");
        Assert.Equal(2, await fixture.Service.GetUnreadTotalAsync(Teacher));

        await fixture.Service.CloseAsync(conversation.Id);
        await fixture.Service.SetParticipantAsync(conversation.Id, new TrustedParticipantSpec(Teacher, TrustedParticipantRole.Writer, "teacher", canRead: false));

        Assert.Equal(0, await fixture.Service.GetUnreadTotalAsync(Teacher));
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.GetHistoryAsync(Teacher, conversation.Id)).Status);
        Assert.True((await fixture.Service.GetHistoryAsync(Learner, conversation.Id)).IsSuccess);
        Assert.Empty(await fixture.Service.ListConversationsAsync(Teacher));
    }

    [Fact]
    public async Task Exact_retry_returns_original_message_and_writes_one_event_while_changed_retry_conflicts()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var request = Text("synthetic once");

        var first = await fixture.Service.SendAsync(Learner, conversation.Id, request);
        var retry = await fixture.Service.SendAsync(Learner, conversation.Id, request);
        var changed = await fixture.Service.SendAsync(Learner, conversation.Id, request with { Text = "different" });

        Assert.Equal(first.Value!.Id, retry.Value!.Id);
        Assert.Equal(ServerTrustedChatStatus.Conflict, changed.Status);
        Assert.Single(fixture.Store.Events, item => item.EventType == ServerTrustedChatEventTypes.MessageCreated);
        Assert.Equal(1, first.Value.Sequence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0000control")]
    [InlineData("lone-surrogate")]
    public async Task Invalid_text_is_rejected_before_persistence(string text)
    {
        text = text == "lone-surrogate" ? "lone" + (char)0xD800 + "surrogate" : text;
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var result = await fixture.Service.SendAsync(Learner, conversation.Id, Text(text));
        Assert.Equal(ServerTrustedChatStatus.Invalid, result.Status);
        Assert.Empty(fixture.Store.Events);
        Assert.Empty((await fixture.Service.GetHistoryAsync(Learner, conversation.Id)).Value!.Messages);
    }

    [Fact]
    public async Task Text_limit_reference_limit_and_crlf_normalization_are_enforced()
    {
        var fixture = new Fixture(options => options.MaxTextLength = 10);
        var conversation = await fixture.CreateMentorChatAsync();
        Assert.Equal(ServerTrustedChatStatus.Invalid, (await fixture.Service.SendAsync(Learner, conversation.Id, Text(new string('a', 11)))).Status);
        var normalized = await fixture.Service.SendAsync(Learner, conversation.Id, Text("a\r\nb"));
        Assert.Equal("a\nb", normalized.Value!.Text);

        var tooMany = Text("refs") with
        {
            References = [new("lesson", "1"), new("lesson", "2"), new("task", "3"), new("task", "4"), new("course", "5")],
        };
        Assert.Equal(ServerTrustedChatStatus.Invalid, (await fixture.Service.SendAsync(Learner, conversation.Id, tooMany)).Status);
        Assert.Throws<ArgumentException>(() => new HostReference("Lesson", "1"));
    }

    [Fact]
    public async Task Host_reference_is_authorized_and_stored_without_entering_events()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var request = Text("about this") with { References = [new HostReference("lesson", "lesson-secret-id")] };
        fixture.Policy.Deny = context => context.Operation == ServerTrustedChatOperation.AttachHostReference;
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.SendAsync(Learner, conversation.Id, request)).Status);

        fixture.Policy.Deny = null;
        var sent = await fixture.Service.SendAsync(Learner, conversation.Id, request);
        Assert.Equal("lesson-secret-id", Assert.Single(sent.Value!.References).Id);
        var payload = Encoding.UTF8.GetString(Assert.Single(fixture.Store.Events).Payload.Span);
        Assert.Contains("\"referenceTypes\":[\"lesson\"]", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("lesson-secret-id", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("about this", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_payloads_have_pinned_metadata_only_shape()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var message = await fixture.SendAsync(Learner, conversation.Id, "synthetic plaintext body");
        await fixture.Service.MarkReadAsync(Teacher, conversation.Id, message.Sequence);
        await fixture.Service.ReportAsync(Teacher, message.Id, "spam", "synthetic comment text");

        var events = fixture.Store.Events;
        Assert.Equal(
            [ServerTrustedChatEventTypes.MessageCreated, ServerTrustedChatEventTypes.ReadMarkerAdvanced, ServerTrustedChatEventTypes.MessageReported],
            events.Select(static item => item.EventType));
        string[][] expected =
        [
            ["eventId", "messageId", "conversationId", "sequence", "authorUserId", "authorLabel", "attachmentCount", "referenceTypes", "createdAt"],
            ["eventId", "conversationId", "userId", "readThroughSequence", "at"],
            ["eventId", "reportId", "messageId", "conversationId", "reporterUserId", "reasonCode", "createdAt"],
        ];
        for (var index = 0; index < events.Count; index++)
        {
            using var document = JsonDocument.Parse(events[index].Payload);
            Assert.Equal(expected[index], document.RootElement.EnumerateObject().Select(static item => item.Name));
            Assert.Equal(conversation.Id.ToString(), events[index].PartitionKey);
            var raw = Encoding.UTF8.GetString(events[index].Payload.Span);
            Assert.DoesNotContain("synthetic", raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Read_marker_is_monotonic_clamped_and_drives_unread_counts()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        for (var index = 0; index < 3; index++)
        {
            await fixture.SendAsync(Learner, conversation.Id, $"m{index}");
        }

        Assert.Equal(3, (await fixture.Service.GetConversationAsync(Teacher, conversation.Id)).Value!.UnreadCount);
        Assert.Equal(3, (await fixture.Service.MarkReadAsync(Teacher, conversation.Id, 99)).Value);
        Assert.Equal(3, (await fixture.Service.MarkReadAsync(Teacher, conversation.Id, 1)).Value);
        Assert.Equal(0, await fixture.Service.GetUnreadTotalAsync(Teacher));
        Assert.Equal(0, await fixture.Service.GetUnreadTotalAsync(Learner));
        Assert.True(await fixture.Service.IsReadThroughAsync(conversation.Id, Teacher, 3));
        Assert.False(await fixture.Service.IsReadThroughAsync(conversation.Id, Learner, 1));
        Assert.Single(fixture.Store.Events, item => item.EventType == ServerTrustedChatEventTypes.ReadMarkerAdvanced);
    }

    [Fact]
    public async Task Paging_returns_ascending_bounded_pages_and_respects_history_visibility()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        for (var index = 1; index <= 5; index++)
        {
            await fixture.SendAsync(Learner, conversation.Id, $"m{index}");
        }

        var newest = (await fixture.Service.GetHistoryAsync(Teacher, conversation.Id, limit: 2)).Value!;
        Assert.Equal([4L, 5L], newest.Messages.Select(static item => item.Sequence));
        Assert.True(newest.HasMoreBefore);
        var older = (await fixture.Service.GetHistoryAsync(Teacher, conversation.Id, beforeSequence: 4, limit: 10)).Value!;
        Assert.Equal([1L, 2L, 3L], older.Messages.Select(static item => item.Sequence));
        Assert.False(older.HasMoreBefore);
        Assert.Equal(ServerTrustedChatStatus.Invalid, (await fixture.Service.GetHistoryAsync(Teacher, conversation.Id, limit: 101)).Status);

        await fixture.Service.SetParticipantAsync(conversation.Id, new TrustedParticipantSpec(Parent, TrustedParticipantRole.Observer, "parent", seesHistory: false));
        Assert.Empty((await fixture.Service.GetHistoryAsync(Parent, conversation.Id)).Value!.Messages);
        await fixture.SendAsync(Learner, conversation.Id, "m6");
        Assert.Equal(6, Assert.Single((await fixture.Service.GetHistoryAsync(Parent, conversation.Id)).Value!.Messages).Sequence);
    }

    [Fact]
    public async Task Changes_since_revision_include_edits_deletes_and_hiding_without_new_sequences()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var a = await fixture.SendAsync(Learner, conversation.Id, "a");
        var b = await fixture.SendAsync(Learner, conversation.Id, "b");
        var cursor = (await fixture.Service.GetChangesAsync(Teacher, conversation.Id, 0)).Value!.ChangeRevision;

        Assert.True((await fixture.Service.EditAsync(Learner, conversation.Id, a.Id, "a2")).IsSuccess);
        Assert.True((await fixture.Service.DeleteAsync(Learner, conversation.Id, b.Id)).IsSuccess);
        var changes = (await fixture.Service.GetChangesAsync(Teacher, conversation.Id, cursor)).Value!;

        Assert.Equal([a.Id, b.Id], changes.Messages.Select(static item => item.Id));
        Assert.Equal("a2", changes.Messages[0].Text);
        Assert.NotNull(changes.Messages[0].EditedAt);
        Assert.Equal(TrustedMessageTombstone.DeletedByAuthor, changes.Messages[1].Tombstone);
        Assert.Null(changes.Messages[1].Text);
        Assert.Equal(2, (await fixture.Service.GetConversationAsync(Teacher, conversation.Id)).Value!.Conversation.LastSequence);
        Assert.Empty((await fixture.Service.GetChangesAsync(Teacher, conversation.Id, changes.ChangeRevision)).Value!.Messages);
    }

    [Fact]
    public async Task Only_author_may_edit_or_delete_and_not_after_close()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var message = await fixture.SendAsync(Learner, conversation.Id, "mine");
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.EditAsync(Teacher, conversation.Id, message.Id, "theirs")).Status);
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.DeleteAsync(Teacher, conversation.Id, message.Id)).Status);
        await fixture.Service.CloseAsync(conversation.Id);
        Assert.Equal(ServerTrustedChatStatus.Closed, (await fixture.Service.EditAsync(Learner, conversation.Id, message.Id, "late")).Status);
        Assert.Equal(ServerTrustedChatStatus.Closed, (await fixture.Service.DeleteAsync(Learner, conversation.Id, message.Id)).Status);
    }

    [Fact]
    public async Task Reply_must_target_a_visible_message_of_the_same_conversation()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var other = await fixture.Service.GetOrCreateConversationAsync("other", TrustedConversationKind.Direct,
            [new(Learner, TrustedParticipantRole.Writer, "learner"), new(Stranger, TrustedParticipantRole.Writer, "teacher")]);
        var foreign = await fixture.SendAsync(Learner, other.Value!.Id, "foreign");
        var reply = await fixture.Service.SendAsync(Learner, conversation.Id, Text("re") with { ReplyToMessageId = foreign.Id });
        Assert.Equal(ServerTrustedChatStatus.Conflict, reply.Status);

        var target = await fixture.SendAsync(Teacher, conversation.Id, "target");
        var ok = await fixture.Service.SendAsync(Learner, conversation.Id, Text("re") with { ReplyToMessageId = target.Id });
        Assert.Equal(target.Id, ok.Value!.ReplyToMessageId);
    }

    [Fact]
    public async Task Rate_limiter_rejects_before_any_state_change()
    {
        var fixture = new Fixture(rateLimiter: new DenyingRateLimiter());
        var conversation = await fixture.CreateMentorChatAsync();
        Assert.Equal(ServerTrustedChatStatus.RateLimited, (await fixture.Service.SendAsync(Learner, conversation.Id, Text("x"))).Status);
        Assert.Empty(fixture.Store.Events);
    }

    [Fact]
    public async Task Report_window_is_bounded_flagged_logged_and_only_for_non_participants()
    {
        var fixture = new Fixture(options => options.ReportWindowRadius = 2);
        var conversation = await fixture.CreateMentorChatAsync();
        var messages = new List<TrustedMessage>();
        for (var index = 1; index <= 7; index++)
        {
            messages.Add(await fixture.SendAsync(index % 2 == 0 ? Teacher : Learner, conversation.Id, $"m{index}"));
        }

        var report = (await fixture.Service.ReportAsync(Learner, messages[3].Id, "abuse", "synthetic")).Value!;
        var duplicate = (await fixture.Service.ReportAsync(Learner, messages[3].Id, "spam")).Value!;
        Assert.Equal(report.Id, duplicate.Id);

        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.ReadReportWindowAsync(Teacher, report.Id)).Status);
        fixture.Policy.Deny = context => context.Operation == ServerTrustedChatOperation.ReadReportWindow;
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.ReadReportWindowAsync(Manager, report.Id)).Status);
        Assert.Empty(await fixture.Service.GetAccessLogAsync(conversation.Id));

        fixture.Policy.Deny = null;
        var window = (await fixture.Service.ReadReportWindowAsync(Manager, report.Id)).Value!;
        Assert.Equal([2L, 3L, 4L, 5L, 6L], window.Messages.Select(static item => item.Message.Sequence));
        Assert.Equal(4, Assert.Single(window.Messages, static item => item.IsReportTarget).Message.Sequence);
        var entry = Assert.Single(await fixture.Service.GetAccessLogAsync(conversation.Id));
        Assert.Equal((Manager, TrustedAccessAction.ReadReportWindow, report.Id, 2L, 6L), (entry.ActorUserId, entry.Action, entry.ReportId!.Value, entry.FromSequence!.Value, entry.ToSequence!.Value));
    }

    [Fact]
    public async Task Report_window_fails_closed_when_access_log_cannot_be_written()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var message = await fixture.SendAsync(Learner, conversation.Id, "synthetic");
        var report = (await fixture.Service.ReportAsync(Teacher, message.Id, "abuse")).Value!;
        fixture.Store.FailAccessLogWritesForTesting = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Service.ReadReportWindowAsync(Manager, report.Id));
    }

    [Fact]
    public async Task Moderator_sees_retained_deleted_text_and_prior_versions_while_participants_see_tombstones()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var edited = await fixture.SendAsync(Teacher, conversation.Id, "original wording");
        await fixture.Service.EditAsync(Teacher, conversation.Id, edited.Id, "softened wording");
        var deleted = await fixture.SendAsync(Teacher, conversation.Id, "deleted wording");
        var report = (await fixture.Service.ReportAsync(Learner, edited.Id, "abuse")).Value!;
        await fixture.Service.DeleteAsync(Teacher, conversation.Id, deleted.Id);

        var participantView = (await fixture.Service.GetHistoryAsync(Learner, conversation.Id)).Value!.Messages;
        Assert.Null(participantView.Single(item => item.Id == deleted.Id).Text);

        var window = (await fixture.Service.ReadReportWindowAsync(Manager, report.Id)).Value!.Messages;
        var target = window.Single(static item => item.IsReportTarget);
        Assert.Equal(["original wording"], target.PriorVersions);
        Assert.Equal("softened wording", target.RetainedText);
        var removed = window.Single(item => item.Message.Id == deleted.Id);
        Assert.True(removed.IsDeletedByAuthor);
        Assert.Equal("deleted wording", removed.RetainedText);
    }

    [Fact]
    public async Task Hiding_is_policy_gated_logged_and_tombstoned_for_participants()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var message = await fixture.SendAsync(Teacher, conversation.Id, "synthetic");
        fixture.Policy.Deny = context => context.Operation == ServerTrustedChatOperation.Hide && context.ActorUserId != Manager;
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.SetHiddenAsync(Learner, message.Id, true)).Status);

        var hidden = await fixture.Service.SetHiddenAsync(Manager, message.Id, true);
        Assert.Equal(TrustedMessageTombstone.HiddenByModerator, hidden.Value!.Tombstone);
        Assert.Null(Assert.Single((await fixture.Service.GetHistoryAsync(Learner, conversation.Id)).Value!.Messages).Text);
        Assert.Equal(0, await fixture.Service.GetUnreadTotalAsync(Learner));
        Assert.Equal(TrustedAccessAction.Hide, Assert.Single(await fixture.Service.GetAccessLogAsync(conversation.Id)).Action);
        Assert.Equal(TrustedMessageTombstone.None, (await fixture.Service.SetHiddenAsync(Manager, message.Id, false)).Value!.Tombstone);
    }

    [Theory]
    [InlineData(TrustedForgetMode.EraseContent)]
    [InlineData(TrustedForgetMode.ReassignAuthor)]
    public async Task Forget_removes_participation_and_markers_and_replaces_ids(TrustedForgetMode mode)
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var learnerMessage = await fixture.SendAsync(Learner, conversation.Id, "learner text");
        var teacherMessage = await fixture.SendAsync(Teacher, conversation.Id, "teacher text");
        await fixture.Service.MarkReadAsync(Teacher, conversation.Id, 2);
        var report = (await fixture.Service.ReportAsync(Teacher, learnerMessage.Id, "abuse")).Value!;
        await fixture.Service.ReadReportWindowAsync(Manager, report.Id);

        Assert.Equal(ServerTrustedChatStatus.Invalid, await fixture.Service.ForgetUserAsync(Teacher, mode, Teacher));
        Assert.Equal(ServerTrustedChatStatus.Success, await fixture.Service.ForgetUserAsync(Teacher, mode, Surrogate));
        Assert.Equal(ServerTrustedChatStatus.Success, await fixture.Service.ForgetUserAsync(Manager, mode, Surrogate));

        Assert.Null(await fixture.Store.GetParticipantAsync(conversation.Id, Teacher));
        Assert.Null(await fixture.Store.GetParticipantAsync(conversation.Id, Surrogate));
        Assert.Null(await fixture.Service.IsReadThroughAsync(conversation.Id, Teacher, 1));
        var history = (await fixture.Service.GetHistoryAsync(Learner, conversation.Id)).Value!.Messages;
        var formerTeacher = history.Single(item => item.Id == teacherMessage.Id);
        Assert.Equal(Surrogate, formerTeacher.AuthorUserId);
        Assert.Equal(mode == TrustedForgetMode.EraseContent ? null : "teacher text", formerTeacher.Text);
        Assert.Equal("learner text", history.Single(item => item.Id == learnerMessage.Id).Text);
        Assert.Equal(Surrogate, (await fixture.Store.GetReportAsync(report.Id))!.ReporterUserId);
        Assert.Equal(Surrogate, Assert.Single(await fixture.Service.GetAccessLogAsync(conversation.Id)).ActorUserId);
    }

    [Fact]
    public async Task Attachments_are_validated_bound_once_downloadable_by_readers_and_cleaned_up()
    {
        var blobs = new MemoryBlobStore();
        var fixture = new Fixture(blobs: blobs);
        var conversation = await fixture.CreateMentorChatAsync(withParent: true);
        var content = Encoding.UTF8.GetBytes("synthetic-png-bytes");

        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden,
            (await fixture.Service.UploadAttachmentAsync(Parent, conversation.Id, "a.png", "image/png", new MemoryStream(content))).Status);
        Assert.Equal(ServerTrustedChatStatus.Invalid,
            (await fixture.Service.UploadAttachmentAsync(Learner, conversation.Id, "../a.png", "image/png", new MemoryStream(content))).Status);
        Assert.Equal(ServerTrustedChatStatus.Invalid,
            (await fixture.Service.UploadAttachmentAsync(Learner, conversation.Id, "evil.svg", "image/svg+xml", new MemoryStream(content))).Status);

        var uploaded = (await fixture.Service.UploadAttachmentAsync(Learner, conversation.Id, "a.png", "application/octet-stream", new MemoryStream(content))).Value!;
        Assert.Equal("image/png", uploaded.ContentType);
        Assert.Equal(ServerTrustedChatStatus.Conflict,
            (await fixture.Service.SendAsync(Teacher, conversation.Id, new TrustedSendRequest { ClientMessageId = Guid.NewGuid(), AttachmentIds = [uploaded.Id] })).Status);
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.OpenAttachmentAsync(Teacher, uploaded.Id)).Status);

        var sent = await fixture.Service.SendAsync(Learner, conversation.Id, new TrustedSendRequest { ClientMessageId = Guid.NewGuid(), AttachmentIds = [uploaded.Id] });
        Assert.Equal(uploaded.Id, Assert.Single(sent.Value!.Attachments).Id);
        Assert.Equal(ServerTrustedChatStatus.Conflict,
            (await fixture.Service.SendAsync(Learner, conversation.Id, new TrustedSendRequest { ClientMessageId = Guid.NewGuid(), AttachmentIds = [uploaded.Id] })).Status);

        var download = (await fixture.Service.OpenAttachmentAsync(Parent, uploaded.Id)).Value!;
        using var destination = new MemoryStream();
        await download.CopyToAsync(destination);
        Assert.Equal(content, destination.ToArray());
        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await fixture.Service.OpenAttachmentAsync(Stranger, uploaded.Id)).Status);

        await fixture.Service.DeleteConversationAsync(conversation.Id);
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task Oversized_upload_and_expired_pending_upload_leave_no_blob()
    {
        var blobs = new MemoryBlobStore();
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-11T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var fixture = new Fixture(options => options.MaxAttachmentBytes = 8, blobs: blobs, time: time);
        var conversation = await fixture.CreateMentorChatAsync();
        Assert.Equal(ServerTrustedChatStatus.Invalid,
            (await fixture.Service.UploadAttachmentAsync(Learner, conversation.Id, "a.png", "image/png", new MemoryStream(new byte[9]))).Status);
        await fixture.Service.UploadAttachmentAsync(Learner, conversation.Id, "a.png", "image/png", new MemoryStream(new byte[8]));
        Assert.Single(blobs.Blobs);

        time.Now += TimeSpan.FromHours(2);
        Assert.Equal(1, await fixture.Service.RunMaintenanceAsync());
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task Retention_purges_only_conversations_closed_before_cutoff()
    {
        var fixture = new Fixture();
        var old = await fixture.CreateMentorChatAsync();
        var open = (await fixture.Service.GetOrCreateConversationAsync("open", TrustedConversationKind.Direct,
            [new(Learner, TrustedParticipantRole.Writer, "learner"), new(Stranger, TrustedParticipantRole.Writer, "teacher")])).Value!;
        await fixture.Service.CloseAsync(old.Id);
        fixture.Store.SetClosedAtForTesting(old.Id, DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(1, await fixture.Service.PurgeClosedBeforeAsync(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Null(await fixture.Store.GetConversationAsync(old.Id));
        Assert.NotNull(await fixture.Store.GetConversationAsync(open.Id));
    }

    [Fact]
    public async Task Access_log_survives_conversation_deletion_until_host_purge()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var message = await fixture.SendAsync(Learner, conversation.Id, "synthetic");
        var report = (await fixture.Service.ReportAsync(Teacher, message.Id, "abuse")).Value!;
        await fixture.Service.ReadReportWindowAsync(Manager, report.Id);

        Assert.Equal(ServerTrustedChatStatus.Success, await fixture.Service.DeleteConversationAsync(conversation.Id));
        Assert.Single(await fixture.Service.GetAccessLogAsync(conversation.Id));
        Assert.Equal(0, await fixture.Service.PurgeAccessLogBeforeAsync(DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(1, await fixture.Service.PurgeAccessLogBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Empty(await fixture.Service.GetAccessLogAsync(conversation.Id));
    }

    [Fact]
    public async Task Host_references_are_hidden_on_tombstones_and_wiped_by_erasure()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        var withReference = Text("see lesson") with { References = [new HostReference("lesson", "l-1")] };
        var deleted = (await fixture.Service.SendAsync(Learner, conversation.Id, withReference)).Value!;
        var erased = (await fixture.Service.SendAsync(Teacher, conversation.Id, withReference with { ClientMessageId = Guid.NewGuid() })).Value!;
        await fixture.Service.DeleteAsync(Learner, conversation.Id, deleted.Id);
        await fixture.Service.ForgetUserAsync(Teacher, TrustedForgetMode.EraseContent, Surrogate);

        var history = (await fixture.Service.GetHistoryAsync(Learner, conversation.Id)).Value!.Messages;
        Assert.All(history, static item => Assert.Empty(item.References));
        Assert.Empty((await fixture.Store.GetMessageAsync(erased.Id))!.References);
    }

    [Fact]
    public async Task Participant_limit_is_enforced()
    {
        var fixture = new Fixture(options => options.MaxParticipants = 2);
        var conversation = await fixture.CreateMentorChatAsync();
        Assert.Equal(ServerTrustedChatStatus.LimitReached,
            await fixture.Service.SetParticipantAsync(conversation.Id, new TrustedParticipantSpec(Parent, TrustedParticipantRole.Observer, "parent")));
    }

    [Fact]
    public async Task Signals_carry_no_content_and_follow_commits()
    {
        var fixture = new Fixture();
        var conversation = await fixture.CreateMentorChatAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = fixture.Hub.SubscribeAsync(conversation.Id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = subscription.MoveNextAsync();
        while (fixture.Hub.SubscriberCount == 0)
        {
            await Task.Yield();
        }

        var message = await fixture.SendAsync(Learner, conversation.Id, "synthetic");
        Assert.True(await pending);
        Assert.Equal(new ServerTrustedChatSignal(conversation.Id, ServerTrustedChatSignalKind.MessagesChanged, message.ChangeRevision), subscription.Current);
    }

    [Fact]
    public void Options_reject_values_beyond_ceilings()
    {
        Assert.Throws<ArgumentException>(() => new ServerTrustedChatOptions { MaxTextLength = 16_001 }.Validate());
        Assert.Throws<ArgumentException>(() => new ServerTrustedChatOptions { MaxAttachmentsPerMessage = 11 }.Validate());
        Assert.Throws<ArgumentException>(() => new ServerTrustedChatOptions { ReportWindowRadius = 51 }.Validate());
        Assert.Throws<ArgumentException>(() => new ServerTrustedChatOptions { MaxAttachmentBytes = 51L * 1024 * 1024 }.Validate());
    }

    [Fact]
    public void Core_assembly_references_no_e2ee_client_server_or_persistence_assembly()
    {
        var references = typeof(ServerTrustedChatService).Assembly.GetReferencedAssemblies().Select(static item => item.Name).ToArray();
        Assert.Contains("Skopka.Chat.Attachments", references);
        foreach (var forbidden in new[] { "Skopka.Chat.Server", "Skopka.Chat.Client", "Skopka.Chat.UI.Core", "Skopka.Chat.Persistence.PostgreSql", "Microsoft.EntityFrameworkCore", "NSec.Cryptography", "Microsoft.AspNetCore.Components" })
        {
            Assert.DoesNotContain(forbidden, references);
        }
    }

    [Fact]
    public void E2ee_assemblies_do_not_reference_server_trusted_assemblies()
    {
        foreach (var assembly in new[] { typeof(Skopka.Chat.Server.ChatServerEngine).Assembly, typeof(Skopka.Chat.Client.ChatReceiver).Assembly, typeof(Skopka.Chat.UI.ChatViewModel).Assembly, typeof(Skopka.Chat.Persistence.PostgreSql.ChatDbContext).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), static item => item.Name!.StartsWith("Skopka.Chat.ServerTrusted", StringComparison.Ordinal));
        }
    }

    private static TrustedSendRequest Text(string text) => new() { ClientMessageId = Guid.NewGuid(), Text = text };

    private sealed class Fixture
    {
        public Fixture(Action<ServerTrustedChatOptions>? configure = null, IServerTrustedChatRateLimiter? rateLimiter = null, MemoryBlobStore? blobs = null, TimeProvider? time = null)
        {
            var options = new ServerTrustedChatOptions();
            configure?.Invoke(options);
            Service = new ServerTrustedChatService(Store, Policy, rateLimiter ?? new UnlimitedServerTrustedChatRateLimiter(), options, time ?? TimeProvider.System, Hub,
                blobs, blobs is null ? null : new PngOnlyValidator());
        }

        public InMemoryServerTrustedChatStore Store { get; } = new();

        public RecordingPolicy Policy { get; } = new();

        public InProcessServerTrustedChatSignalHub Hub { get; } = new();

        public ServerTrustedChatService Service { get; }

        public async Task<TrustedConversation> CreateMentorChatAsync(bool withParent = false)
        {
            List<TrustedParticipantSpec> participants =
            [
                new(Learner, TrustedParticipantRole.Writer, "learner"),
                new(Teacher, TrustedParticipantRole.Writer, "teacher"),
            ];
            if (withParent)
            {
                participants.Add(new(Parent, TrustedParticipantRole.Observer, "parent"));
            }

            var result = await Service.GetOrCreateConversationAsync($"iqzone:mentor:{Learner}:{Teacher}", TrustedConversationKind.Direct, participants);
            return result.Value!;
        }

        public async Task<TrustedMessage> SendAsync(Guid author, TrustedConversationId conversationId, string text)
        {
            var result = await Service.SendAsync(author, conversationId, Text(text));
            Assert.True(result.IsSuccess, result.Status.ToString());
            return result.Value!;
        }
    }

    private sealed class RecordingPolicy : IServerTrustedChatAccessPolicy
    {
        public Func<ServerTrustedAccessContext, bool>? Deny { get; set; }

        public List<ServerTrustedAccessContext> Asked { get; } = [];

        public ValueTask<bool> AuthorizeAsync(ServerTrustedAccessContext context, CancellationToken cancellationToken = default)
        {
            Asked.Add(context);
            return ValueTask.FromResult(Deny?.Invoke(context) != true);
        }
    }

    private sealed class DenyingRateLimiter : IServerTrustedChatRateLimiter
    {
        public ValueTask<bool> TryAcquireAsync(Guid actorUserId, ServerTrustedChatOperation operation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class PngOnlyValidator : IServerTrustedAttachmentValidator
    {
        public ValueTask<ServerTrustedAttachmentVerdict> ValidateAsync(ServerTrustedAttachmentCandidate candidate, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(candidate.FileName.EndsWith(".png", StringComparison.Ordinal)
                ? ServerTrustedAttachmentVerdict.Accept("image/png")
                : ServerTrustedAttachmentVerdict.Rejected);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class MemoryBlobStore : IAttachmentStore
    {
        public Dictionary<AttachmentId, (StoredAttachment Metadata, byte[] Content)> Blobs { get; } = [];

        public ValueTask<StoredAttachment?> GetMetadataAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Blobs.TryGetValue(attachmentId, out var item) ? item.Metadata : null);

        public async ValueTask<AttachmentStoreResult> TryPutAsync(StoredAttachment attachment, Stream ciphertext, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await ciphertext.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            if (bytes.LongLength != attachment.CiphertextLength || !SHA256.HashData(bytes).AsSpan().SequenceEqual(attachment.CiphertextSha256.Span))
            {
                throw new InvalidOperationException("Length or hash mismatch.");
            }

            if (Blobs.ContainsKey(attachment.AttachmentId))
            {
                return AttachmentStoreResult.Conflict;
            }

            Blobs.Add(attachment.AttachmentId, (attachment, bytes));
            return AttachmentStoreResult.Stored;
        }

        public async ValueTask CopyToAsync(AttachmentId attachmentId, Stream destination, CancellationToken cancellationToken = default) =>
            await destination.WriteAsync(Blobs[attachmentId].Content, cancellationToken);

        public ValueTask<bool> DeleteAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Blobs.Remove(attachmentId));

        public ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }
}
