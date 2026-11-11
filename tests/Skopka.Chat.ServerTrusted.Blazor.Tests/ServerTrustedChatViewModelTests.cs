using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.Blazor;

namespace Skopka.Chat.ServerTrusted.Blazor.Tests;

public sealed class ServerTrustedChatViewModelTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Catch_up_applies_edit_delete_and_new_message_by_identifier()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var edited = await host.SendAsync(conversation, TestHost.Teacher, "before edit");
        var deleted = await host.SendAsync(conversation, TestHost.Teacher, "will be deleted");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        var cursor = model.Revision;
        var changes = 0;
        model.Changed += (_, _) => Interlocked.Increment(ref changes);

        Assert.True((await host.WithServiceAsync(service => service.EditAsync(TestHost.Teacher, conversation, edited.Id, "after edit", Token))).IsSuccess);
        Assert.True((await host.WithServiceAsync(service => service.DeleteAsync(TestHost.Teacher, conversation, deleted.Id, Token))).IsSuccess);
        var added = await host.SendAsync(conversation, TestHost.Teacher, "new message");
        Assert.True(await model.CatchUpAsync(Token));

        Assert.Equal([edited.Id, deleted.Id, added.Id], model.Messages.Select(item => item.Id));
        Assert.Equal("after edit", model.Messages[0].Text);
        Assert.NotNull(model.Messages[0].EditedAt);
        Assert.Equal(TrustedMessageTombstone.DeletedByAuthor, model.Messages[1].Tombstone);
        Assert.Null(model.Messages[1].Text);
        Assert.True(model.Revision > cursor);
        Assert.True(changes > 0);

        Assert.True(await model.CatchUpAsync(Token));
        Assert.Equal(3, model.Messages.Count);
    }

    [Fact]
    public async Task Send_retry_after_lost_response_reuses_client_message_id()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var runner = new LossyRunner(host.CreateRunner());
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, runner);
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("exactly once");

        runner.LoseNextResponse();
        Assert.False(await model.SendDraftAsync(Token));
        Assert.Equal(ServerTrustedChatFailure.Failed, model.Failure);
        Assert.Equal("exactly once", model.Draft);
        Assert.False(model.IsBusy);

        Assert.True(await model.SendDraftAsync(Token));

        var stored = await host.HistoryAsync(conversation, TestHost.Learner);
        Assert.Equal("exactly once", Assert.Single(stored).Text);
        Assert.Equal(stored[0].Id, Assert.Single(model.Messages).Id);
        Assert.Equal(string.Empty, model.Draft);
        Assert.Equal(ServerTrustedChatFailure.None, model.Failure);
    }

    [Fact]
    public async Task Changing_the_draft_after_a_failure_starts_a_new_logical_send()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var runner = new LossyRunner(host.CreateRunner());
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, runner);
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("first draft");
        runner.LoseNextResponse();
        Assert.False(await model.SendDraftAsync(Token));

        model.SetDraft("second draft");
        Assert.True(await model.SendDraftAsync(Token));

        Assert.Equal(["first draft", "second draft"], (await host.HistoryAsync(conversation, TestHost.Learner)).Select(item => item.Text));
        Assert.Equal(ServerTrustedChatFailure.None, model.Failure);
    }

    [Fact]
    public async Task Revoked_access_clears_timeline_and_restored_access_reloads()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Learner, "history");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Teacher, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("unsent");
        Assert.Single(model.Messages);

        Assert.Equal(ServerTrustedChatStatus.Success, await host.WithServiceAsync(service => service.SetParticipantAsync(
            conversation, new TrustedParticipantSpec(TestHost.Teacher, TrustedParticipantRole.Writer, "teacher", canRead: false), Token)));
        Assert.False(await model.CatchUpAsync(Token));

        Assert.Empty(model.Messages);
        Assert.Null(model.Conversation);
        Assert.False(model.IsLoaded);
        Assert.False(model.CanWrite);
        Assert.Equal(ServerTrustedChatFailure.Unavailable, model.Failure);
        Assert.False(await model.SendDraftAsync(Token));

        Assert.Equal(ServerTrustedChatStatus.Success, await host.WithServiceAsync(service => service.SetParticipantAsync(
            conversation, new TrustedParticipantSpec(TestHost.Teacher, TrustedParticipantRole.Writer, "teacher", canRead: true), Token)));
        Assert.True(await model.CatchUpAsync(Token));
        Assert.Single(model.Messages);
        Assert.Equal(ServerTrustedChatFailure.None, model.Failure);
    }

    [Fact]
    public async Task Policy_revocation_during_send_clears_the_timeline()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "visible before revocation");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("rejected");
        host.Policy.Allow = context => context.ActorUserId != TestHost.Learner;

        Assert.False(await model.SendDraftAsync(Token));

        Assert.Empty(model.Messages);
        Assert.Equal(ServerTrustedChatFailure.Unavailable, model.Failure);
        Assert.Single(await host.HistoryAsync(conversation, TestHost.Teacher));
    }

    [Fact]
    public async Task Message_level_rejection_keeps_timeline_and_reports_generic_failure()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var own = await host.SendAsync(conversation, TestHost.Learner, "own");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        host.Policy.Allow = context => context.Operation != ServerTrustedChatOperation.DeleteOwn;

        Assert.False(await model.DeleteAsync(own.Id, Token));

        Assert.Single(model.Messages);
        Assert.True(model.IsLoaded);
        Assert.Equal(ServerTrustedChatFailure.Failed, model.Failure);
    }

    [Fact]
    public async Task Closed_write_result_refreshes_status_and_disables_writing()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("too late");
        Assert.Equal(ServerTrustedChatStatus.Success, await host.WithServiceAsync(service => service.CloseAsync(conversation, Token)));

        Assert.False(await model.SendDraftAsync(Token));

        Assert.True(model.IsClosed);
        Assert.False(model.CanWrite);
        Assert.Equal(ServerTrustedChatFailure.Closed, model.Failure);
    }

    [Fact]
    public async Task Edit_and_reply_flow_restores_the_previous_draft()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var question = await host.SendAsync(conversation, TestHost.Teacher, "question");
        var own = await host.SendAsync(conversation, TestHost.Learner, "typo");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        Assert.True(model.BeginReply(question.Id));
        model.SetDraft("half-written answer");

        Assert.False(model.BeginEdit(question.Id));
        Assert.True(model.BeginEdit(own.Id));
        Assert.Equal("typo", model.Draft);
        Assert.Null(model.ReplyTarget);
        model.SetDraft("fixed");
        Assert.True(await model.SaveEditAsync(Token));

        Assert.Equal("fixed", model.FindMessage(own.Id)!.Text);
        Assert.False(model.IsEditing);
        Assert.Equal("half-written answer", model.Draft);
        Assert.Equal(question.Id, model.ReplyTarget);
        Assert.True(await model.SendDraftAsync(Token));
        Assert.Equal(question.Id, model.Messages[^1].ReplyToMessageId);
        Assert.Null(model.ReplyTarget);
    }

    [Fact]
    public async Task Draft_is_bounded_by_engine_text_length()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));

        model.SetDraft(new string('a', model.MaxTextLength - 1) + "\U0001F600");

        Assert.Equal(4000, model.MaxTextLength);
        Assert.Equal(model.MaxTextLength - 1, model.Draft.Length);
        Assert.True(model.CanSend);
        model.SetDraft("   ");
        Assert.False(model.CanSend);
    }

    [Fact]
    public async Task Load_older_pages_back_and_ignores_changes_outside_the_loaded_window()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var sent = new List<TrustedMessage>();
        for (var index = 1; index <= 5; index++)
        {
            sent.Add(await host.SendAsync(conversation, TestHost.Teacher, $"m{index}"));
        }

        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner(), pageSize: 2);
        Assert.True(await model.LoadAsync(Token));
        Assert.Equal(["m4", "m5"], model.Messages.Select(item => item.Text));
        Assert.True(model.HasMoreBefore);

        Assert.True((await host.WithServiceAsync(service => service.EditAsync(TestHost.Teacher, conversation, sent[0].Id, "m1 edited", Token))).IsSuccess);
        Assert.True(await model.CatchUpAsync(Token));
        Assert.Equal(["m4", "m5"], model.Messages.Select(item => item.Text));
        var cursor = model.Revision;

        Assert.True(await model.LoadOlderAsync(Token));
        Assert.Equal(["m2", "m3", "m4", "m5"], model.Messages.Select(item => item.Text));
        Assert.True(await model.LoadOlderAsync(Token));
        Assert.Equal(["m1 edited", "m2", "m3", "m4", "m5"], model.Messages.Select(item => item.Text));
        Assert.False(model.HasMoreBefore);
        Assert.Equal(cursor, model.Revision);
        Assert.False(await model.LoadOlderAsync(Token));
    }

    [Fact]
    public async Task Mark_read_advances_only_beyond_the_known_position()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var message = await host.SendAsync(conversation, TestHost.Teacher, "unread");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));

        Assert.True(await model.MarkReadThroughLatestAsync(Token));
        Assert.False(await model.MarkReadThroughLatestAsync(Token));

        Assert.True(await host.WithServiceAsync(service => service.IsReadThroughAsync(conversation, TestHost.Learner, message.Sequence, Token)));
        Assert.Equal(0, await host.WithServiceAsync(service => service.GetUnreadTotalAsync(TestHost.Learner, Token)));
    }

    [Fact]
    public async Task Observer_can_report_but_cannot_write()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync(withParent: true);
        var message = await host.SendAsync(conversation, TestHost.Teacher, "reportable");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Parent, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));

        Assert.True(model.IsObserver);
        Assert.False(model.CanWrite);
        Assert.False(model.BeginReply(message.Id));
        model.SetDraft("observer text");
        Assert.False(await model.SendDraftAsync(Token));

        Assert.False(await model.ReportAsync(message.Id, "not a code", null, Token));
        Assert.True(await model.ReportAsync(message.Id, "abuse", "  ", Token));
        Assert.True(model.IsReported(message.Id));
        Assert.True(await model.ReportAsync(message.Id, "abuse", null, Token));
        Assert.Single(await host.HistoryAsync(conversation, TestHost.Teacher));
    }

    [Fact]
    public void Constructor_rejects_invalid_arguments()
    {
        var runner = new LossyRunner(new ThrowingRunner());
        Assert.Throws<ArgumentException>(() => new ServerTrustedChatViewModel(TrustedConversationId.New(), Guid.Empty, runner));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTrustedChatViewModel(TrustedConversationId.New(), TestHost.Learner, runner, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerTrustedChatViewModel(TrustedConversationId.New(), TestHost.Learner, runner, 101));
        Assert.Throws<ArgumentNullException>(() => new ServerTrustedChatViewModel(TrustedConversationId.New(), TestHost.Learner, null!));
    }

    [Fact]
    public async Task Transient_failure_keeps_state_and_exposes_no_exception_text()
    {
        var model = new ServerTrustedChatViewModel(TrustedConversationId.New(), TestHost.Learner, new ThrowingRunner());

        Assert.False(await model.LoadAsync(Token));

        Assert.Equal(ServerTrustedChatFailure.Failed, model.Failure);
        Assert.False(model.IsLoaded);
        Assert.Empty(model.Messages);
    }

    private sealed class LossyRunner(IServerTrustedChatOperationRunner inner) : IServerTrustedChatOperationRunner
    {
        private int _loseNext;

        public void LoseNextResponse() => Volatile.Write(ref _loseNext, 1);

        public async ValueTask<TResult> RunAsync<TResult>(Func<ServerTrustedChatService, CancellationToken, ValueTask<TResult>> operation, CancellationToken cancellationToken = default)
        {
            var result = await inner.RunAsync(operation, cancellationToken);
            if (Interlocked.Exchange(ref _loseNext, 0) == 1)
            {
                throw new IOException("synthetic lost response");
            }

            return result;
        }
    }

    private sealed class ThrowingRunner : IServerTrustedChatOperationRunner
    {
        public ValueTask<TResult> RunAsync<TResult>(Func<ServerTrustedChatService, CancellationToken, ValueTask<TResult>> operation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("synthetic store failure with private detail");
    }
}
