using Microsoft.Maui.Dispatching;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.UI;

namespace Skopka.Chat.UI.Maui.Tests;

public sealed class MauiUiTests
{
    [Fact]
    public void Restored_history_updates_replaceable_native_trust_warning()
    {
        var conversation = ConversationId.New(); var user = UserId.New();
        var model = new ChatViewModel(conversation, user, new NoOpSender());
        using var presentation = new MauiChatPresentation(new ImmediateDispatcher());
        presentation.SetViewModel(model); presentation.SetStrings(new MauiChatStrings { BackupTrustWarning = "synthetic localized warning" });
        Assert.False(presentation.ContainsBackupHistory);
        model.ApplyRestored(new RestoredChatContent(conversation, user, DeviceId.New(), DateTimeOffset.UtcNow, new ChatTextContent(ChatContentId.New(), "synthetic")));
        Assert.True(presentation.ContainsBackupHistory); Assert.Single(presentation.Items);
        Assert.Equal("synthetic localized warning", presentation.Strings.BackupTrustWarning);
    }

    [Fact]
    public void Maui_ui_package_preserves_presentation_only_dependency_direction()
    {
        var references = typeof(SkopkaChatView).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .ToArray();

        Assert.Contains("Skopka.Chat.UI.Core", references);
        Assert.DoesNotContain("Skopka.Chat.Client.Http", references);
        Assert.DoesNotContain("Skopka.Chat.Client.Storage.Sqlite", references);
        Assert.DoesNotContain("Skopka.Chat.Media.FFmpeg", references);
        Assert.DoesNotContain("Skopka.Chat.Server", references);
        Assert.DoesNotContain("Skopka.Chat.Persistence.PostgreSql", references);
    }

    [Fact]
    public void Presentation_applies_stable_content_id_diff_on_dispatcher()
    {
        var dispatcher = new ImmediateDispatcher();
        using var presentation = new MauiChatPresentation(dispatcher);
        var user = UserId.New();
        var conversation = ConversationId.New();
        var contentId = ChatContentId.New();
        var viewModel = new ChatViewModel(conversation, user, new NoOpSender());
        presentation.SetViewModel(viewModel);
        viewModel.Apply(Delivery(conversation, user, contentId, new ChatTextContent(contentId, "hello"), 1));
        var original = Assert.Single(presentation.Items);

        viewModel.Apply(Delivery(
            conversation,
            user,
            ChatContentId.New(),
            new ChatReactionContent(ChatContentId.New(), contentId, "👍", ChatReactionOperation.Add),
            2));

        Assert.Same(original, Assert.Single(presentation.Items));
        Assert.Contains("👍 1", original.ReactionSummary, StringComparison.Ordinal);
        Assert.True(dispatcher.DispatchCount >= 3);
    }

    [Fact]
    public void Presentation_rejects_unbounded_or_blank_reaction_choices()
    {
        using var presentation = new MauiChatPresentation(new ImmediateDispatcher());
        Assert.Throws<ArgumentException>(() => presentation.SetReactionChoices(Enumerable.Repeat("x", 13).ToArray()));
        Assert.Throws<ArgumentException>(() => presentation.SetReactionChoices([" "]));
    }

    private static ReceivedChatContent Delivery(
        ConversationId conversation,
        UserId sender,
        ChatContentId contentId,
        ChatContent content,
        int seed) => new(
            new MessageId(GuidFrom(seed)),
            conversation,
            sender,
            new DeviceId(GuidFrom(seed + 100)),
            new DateTimeOffset(2026, 9, 2, 12, seed, 0, TimeSpan.Zero),
            content);

    private static Guid GuidFrom(int value) => new($"00000000-0000-0000-0000-{value:X12}");

    private sealed class NoOpSender : IChatContentSender
    {
        public ValueTask<ChatContentSendResult> SendAsync(
            ConversationId conversationId,
            ChatContent content,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ChatContentSendResult.Failed);
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        internal int DispatchCount { get; private set; }
        public bool IsDispatchRequired => false;

        public bool Dispatch(Action action)
        {
            DispatchCount++;
            action();
            return true;
        }

        public bool DispatchDelayed(TimeSpan delay, Action action) => Dispatch(action);
        public IDispatcherTimer CreateTimer() => new ImmediateTimer();
    }

    private sealed class ImmediateTimer : IDispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public bool IsRunning { get; private set; }
        public event EventHandler? Tick { add { } remove { } }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }
}
