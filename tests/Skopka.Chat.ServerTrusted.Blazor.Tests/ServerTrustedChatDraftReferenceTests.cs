using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.Blazor;

namespace Skopka.Chat.ServerTrusted.Blazor.Tests;

/// <summary>Host-placed draft reference on a live component: parameter changes and <c>SetDraftReferenceAsync</c>.</summary>
public sealed class ServerTrustedChatDraftReferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly HostReference Lesson = new("lesson", "1");
    private static readonly HostReference TaskSeven = new("task", "7");
    private static readonly HostReference TaskEight = new("task", "8");
    private static readonly HostReference UserPicked = new("note", "user");

    [Fact]
    public async Task Parameter_change_replaces_the_host_reference_and_keeps_text_attachments_reply_and_model()
    {
        await using var host = TestHost.Create(interactiveJs: true, attachments: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        var question = await host.SendAsync(conversation, TestHost.Teacher, "which task?");
        await using var renderer = CreateRenderer(host);
        var (chat, id) = await renderer.AttachAsync<ServerTrustedChat>();
        await renderer.SetParametersAsync(id, Parameters(conversation, Lesson));
        var model = Assert.IsType<ServerTrustedChatViewModel>(chat.Model);
        Assert.Equal([Lesson], model.DraftReferences);
        Assert.Equal(Lesson, model.HostReference);

        model.SetDraft("typed before asking");
        Assert.True(await UploadAsync(model, "photo.png"));
        Assert.True(model.BeginReply(question.Id));
        Assert.True(model.AddReference(UserPicked));

        await renderer.SetParametersAsync(id, Parameters(conversation, TaskSeven));

        Assert.Same(model, chat.Model);
        Assert.Equal([UserPicked, TaskSeven], model.DraftReferences);
        Assert.Equal(TaskSeven, model.HostReference);
        Assert.Equal("typed before asking", model.Draft);
        Assert.Equal("photo.png", Assert.Single(model.PendingAttachments).FileName);
        Assert.Equal(question.Id, model.ReplyTarget);
        Assert.Equal("which task?", Assert.Single(model.Messages).Text);
        Assert.Empty(renderer.Exceptions);
    }

    [Fact]
    public async Task Empty_parameters_remove_only_the_host_reference_and_unchanged_values_never_re_add_it()
    {
        await using var host = TestHost.Create(interactiveJs: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        await using var renderer = CreateRenderer(host);
        var (chat, id) = await renderer.AttachAsync<ServerTrustedChat>();
        await renderer.SetParametersAsync(id, Parameters(conversation, Lesson));
        var model = chat.Model!;
        Assert.True(model.AddReference(UserPicked));
        model.SetDraft("keep me");

        await renderer.SetParametersAsync(id, Parameters(conversation, null));
        Assert.Equal([UserPicked], model.DraftReferences);
        Assert.Null(model.HostReference);
        Assert.Equal("keep me", model.Draft);

        await renderer.SetParametersAsync(id, Parameters(conversation, TaskSeven));
        Assert.True(model.RemoveReference(TaskSeven));
        Assert.Null(model.HostReference);

        // A parent re-render with the same values must not bring back a reference the user removed.
        await renderer.SetParametersAsync(id, Parameters(conversation, TaskSeven));
        Assert.Equal([UserPicked], model.DraftReferences);

        // An invalid pair counts as empty: the previous host reference does not linger.
        await renderer.SetParametersAsync(id, Parameters(conversation, TaskEight));
        await renderer.SetParametersAsync(id, Parameters(conversation, "Task", "8"));
        Assert.Equal([UserPicked], model.DraftReferences);
        Assert.Same(model, chat.Model);
        Assert.Empty(renderer.Exceptions);
    }

    [Fact]
    public async Task Method_places_the_same_reference_again_after_a_send_and_is_a_no_op_while_present()
    {
        await using var host = TestHost.Create(interactiveJs: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        await using var renderer = CreateRenderer(host);
        var (chat, id) = await renderer.AttachAsync<ServerTrustedChat>();
        await renderer.SetParametersAsync(id, Parameters(conversation, null));
        var model = chat.Model!;

        Assert.True(await chat.SetDraftReferenceAsync(TaskSeven));
        model.SetDraft("about task seven");
        Assert.True(await renderer.Dispatcher.InvokeAsync(async () => await model.SendDraftAsync(Token)));
        Assert.Empty(model.DraftReferences);
        Assert.Null(model.HostReference);

        // "Ask in chat" on the same task again: the parameter could not express this, the method can.
        Assert.True(await chat.SetDraftReferenceAsync(TaskSeven));
        Assert.True(await chat.SetDraftReferenceAsync(TaskSeven));
        Assert.Equal([TaskSeven], model.DraftReferences);
        Assert.True(await chat.SetDraftReferenceAsync(null));
        Assert.Empty(model.DraftReferences);

        var stored = Assert.Single(await host.HistoryAsync(conversation, TestHost.Teacher));
        Assert.Equal(TaskSeven, Assert.Single(stored.References));
        Assert.Same(model, chat.Model);
        Assert.Empty(renderer.Exceptions);
    }

    [Fact]
    public async Task Reference_set_while_the_history_is_loading_is_kept_and_not_overwritten_by_the_initial_parameter()
    {
        var runner = new GatedRunner();
        await using var host = TestHost.Create(services => services.AddSingleton<IServerTrustedChatOperationRunner>(runner), interactiveJs: true);
        runner.Inner = host.CreateRunner();
        var conversation = await host.CreateLearnerTeacherAsync();
        await using var renderer = CreateRenderer(host);
        var (chat, id) = await renderer.AttachAsync<ServerTrustedChat>();

        Assert.False(await chat.SetDraftReferenceAsync(TaskSeven));
        var rendering = renderer.SetParametersAsync(id, Parameters(conversation, Lesson));
        await runner.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var model = chat.Model!;
        Assert.False(model.IsLoaded);

        Assert.True(await chat.SetDraftReferenceAsync(TaskSeven));
        runner.Release();
        await rendering.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.True(model.IsLoaded);
        Assert.Equal([TaskSeven], model.DraftReferences);
        Assert.Same(model, chat.Model);
        Assert.Empty(renderer.Exceptions);
    }

    [Fact]
    public async Task Host_reference_set_while_editing_waits_for_the_next_new_message()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var own = await host.SendAsync(conversation, TestHost.Learner, "typo");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("unsent question");

        Assert.True(model.BeginEdit(own.Id));
        Assert.True(model.SetHostReference(TaskSeven));
        Assert.True(model.IsEditing);
        model.SetDraft("fixed");
        Assert.True(await model.SendDraftAsync(Token));

        Assert.Empty(Assert.Single(await host.HistoryAsync(conversation, TestHost.Learner)).References);
        Assert.Equal("unsent question", model.Draft);
        Assert.Equal([TaskSeven], model.DraftReferences);
        Assert.True(await model.SendDraftAsync(Token));
        var sent = await host.HistoryAsync(conversation, TestHost.Learner);
        Assert.Equal(2, sent.Count);
        Assert.Equal(TaskSeven, Assert.Single(sent[1].References));
    }

    [Fact]
    public async Task Reference_change_during_an_edit_does_not_reuse_the_stashed_client_message_id()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var own = await host.SendAsync(conversation, TestHost.Learner, "typo");
        var runner = new LossyRunner(host.CreateRunner());
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, runner);
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("question");
        runner.LoseNextResponse();
        Assert.False(await model.SendDraftAsync(Token));

        Assert.True(model.BeginEdit(own.Id));
        Assert.True(model.SetHostReference(TaskSeven));
        model.Cancel();

        // Same text with a new reference is a new logical send, not a conflicting retry of the lost one.
        Assert.True(await model.SendDraftAsync(Token));
        var stored = await host.HistoryAsync(conversation, TestHost.Learner);
        Assert.Equal(3, stored.Count);
        Assert.Empty(stored[1].References);
        Assert.Equal(TaskSeven, Assert.Single(stored[2].References));
    }

    [Fact]
    public async Task Repeating_the_host_reference_keeps_the_retry_identity_of_a_lost_send()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var runner = new LossyRunner(host.CreateRunner());
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, runner);
        Assert.True(await model.LoadAsync(Token));
        Assert.True(model.SetHostReference(TaskSeven));
        model.SetDraft("question");
        runner.LoseNextResponse();
        Assert.False(await model.SendDraftAsync(Token));

        Assert.True(model.SetHostReference(TaskSeven));
        Assert.True(await model.SendDraftAsync(Token));

        Assert.Single(await host.HistoryAsync(conversation, TestHost.Learner));
    }

    [Fact]
    public async Task Adopting_or_releasing_a_reference_already_in_the_draft_keeps_the_retry_identity()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var runner = new LossyRunner(host.CreateRunner());
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, runner);
        Assert.True(await model.LoadAsync(Token));
        Assert.True(model.AddReference(UserPicked));
        model.SetDraft("question");
        runner.LoseNextResponse();
        Assert.False(await model.SendDraftAsync(Token));

        // The reference set is unchanged, so the retry must reuse the client message ID instead of creating a second message.
        Assert.True(model.SetHostReference(UserPicked));
        Assert.True(model.SetHostReference(null));
        Assert.Equal([UserPicked], model.DraftReferences);
        Assert.True(await model.SendDraftAsync(Token));

        Assert.Single(await host.HistoryAsync(conversation, TestHost.Learner));
    }

    [Fact]
    public async Task Host_reference_placed_before_the_prerender_snapshot_is_restored_survives_the_restore()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "history");
        var prerendered = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await prerendered.LoadAsync(Token));
        var snapshot = prerendered.CreateSnapshot();

        var interactive = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(interactive.SetHostReference(TaskSeven));
        Assert.True(interactive.TryRestore(snapshot));

        Assert.Equal([TaskSeven], interactive.DraftReferences);
        Assert.Equal(TaskSeven, interactive.HostReference);
        Assert.Equal("history", Assert.Single(interactive.Messages).Text);
    }

    [Fact]
    public async Task Host_reference_never_exceeds_the_reference_limit()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        for (var index = 1; index < ServerTrustedChatLimits.MaxHostReferences; index++)
        {
            Assert.True(model.AddReference(new HostReference("note", index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        // The last free place goes to the host reference; replacing it reuses that place.
        Assert.True(model.SetHostReference(TaskSeven));
        Assert.True(model.SetHostReference(TaskEight));
        Assert.Equal(ServerTrustedChatLimits.MaxHostReferences, model.DraftReferences.Count);
        Assert.Contains(TaskEight, model.DraftReferences);
        Assert.DoesNotContain(TaskSeven, model.DraftReferences);

        // With every place taken by other references, a host reference is refused and nothing changes.
        Assert.True(model.SetHostReference(null));
        Assert.True(model.AddReference(UserPicked));
        Assert.False(model.SetHostReference(TaskSeven));
        Assert.Null(model.HostReference);
        Assert.Equal(ServerTrustedChatLimits.MaxHostReferences, model.DraftReferences.Count);

        // A reference already in the draft is adopted, not duplicated, and stays when the host moves on.
        Assert.True(model.SetHostReference(UserPicked));
        Assert.Equal(ServerTrustedChatLimits.MaxHostReferences, model.DraftReferences.Count);
        Assert.True(model.SetHostReference(null));
        Assert.Contains(UserPicked, model.DraftReferences);
    }

    private static InteractiveTestRenderer CreateRenderer(TestHost host) =>
        new(host.Provider, host.Provider.GetRequiredService<ILoggerFactory>());

    private static Dictionary<string, object?> Parameters(TrustedConversationId conversation, HostReference? reference) =>
        Parameters(conversation, reference?.Type, reference?.Id);

    private static Dictionary<string, object?> Parameters(TrustedConversationId conversation, string? type, string? id) => new()
    {
        [nameof(ServerTrustedChat.ConversationId)] = conversation.Value,
        [nameof(ServerTrustedChat.UserId)] = TestHost.Learner,
        [nameof(ServerTrustedChat.DraftReferenceType)] = type,
        [nameof(ServerTrustedChat.DraftReferenceId)] = id,
        // A delegate parameter makes every parent render look changed, as in a real host wrapper.
        [nameof(ServerTrustedChat.AuthorName)] = (Func<TrustedMessage, string>)(message => message.AuthorLabel),
    };

    private static async Task<bool> UploadAsync(ServerTrustedChatViewModel model, string fileName)
    {
        using var content = new MemoryStream([1, 2, 3]);
        return await model.UploadAttachmentAsync(fileName, "image/png", content, Token);
    }

    /// <summary>Blocks the first engine operation (the history load) until released.</summary>
    private sealed class GatedRunner : IServerTrustedChatOperationRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public IServerTrustedChatOperationRunner Inner { get; set; } = null!;

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask<TResult> RunAsync<TResult>(Func<ServerTrustedChatService, CancellationToken, ValueTask<TResult>> operation, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Blocked.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await Inner.RunAsync(operation, cancellationToken);
        }
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
}
