// The interactive test renderer derives from the framework Renderer on purpose (no bUnit dependency).
#pragma warning disable BL0006

using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.Blazor;

namespace Skopka.Chat.ServerTrusted.Blazor.Tests;

public sealed class ServerTrustedChatRenderingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Static_render_shows_encoded_text_and_accessible_disabled_composer()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "<script>alert('synthetic')</script>\nsecond line");

        var html = await RenderChatAsync(host, conversation, TestHost.Learner);

        Assert.Contains("&lt;script&gt;alert(&#x27;synthetic&#x27;)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("role=\"log\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Conversation messages\"", html, StringComparison.Ordinal);
        Assert.Contains("<textarea class=\"skopka-chat-composer__input\" aria-label=\"Message\"", html, StringComparison.Ordinal);
        Assert.Contains("maxlength=\"4000\"", html, StringComparison.Ordinal);
        Assert.Contains(">Send</button>", html, StringComparison.Ordinal);
        Assert.Contains("disabled", html, StringComparison.Ordinal);
        Assert.Contains("teacher", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Reply</button>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Static_render_invokes_no_javascript_no_subscription_and_no_mark_read()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var message = await host.SendAsync(conversation, TestHost.Teacher, "unread synthetic text");

        // ThrowingJsRuntime would fail the render on any JavaScript call.
        var html = await RenderChatAsync(host, conversation, TestHost.Learner);

        Assert.Contains("unread synthetic text", html, StringComparison.Ordinal);
        Assert.Equal(0, host.Hub.SubscriberCount);
        var read = await host.WithServiceAsync(service => service.IsReadThroughAsync(conversation, TestHost.Learner, message.Sequence, Token));
        Assert.False(read);
        var unread = await host.WithServiceAsync(service => service.GetUnreadTotalAsync(TestHost.Learner, Token));
        Assert.Equal(1, unread);
    }

    [Fact]
    public async Task Observers_notice_lists_other_readable_observers()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync(withParent: true);

        var html = await RenderChatAsync(host, conversation, TestHost.Learner);

        Assert.Contains("class=\"skopka-chat__observers\"", html, StringComparison.Ordinal);
        Assert.Contains("This conversation is also visible to:", html, StringComparison.Ordinal);
        Assert.Contains(">parent</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observers_notice_uses_host_participant_names_and_is_absent_without_observers()
    {
        await using var host = TestHost.Create();
        var withParent = await host.CreateLearnerTeacherAsync(withParent: true);
        var withoutParent = await host.CreateLearnerTeacherAsync();

        var named = await RenderChatAsync(host, withParent, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.ParticipantName)] = (Func<TrustedParticipant, string>)(participant => participant.Label == "parent" ? "<Mum>" : participant.Label),
        });
        var none = await RenderChatAsync(host, withoutParent, TestHost.Learner);

        Assert.Contains("&lt;Mum&gt;", named, StringComparison.Ordinal);
        Assert.DoesNotContain("skopka-chat__observers", none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closed_conversation_hides_composer_and_shows_closed_notice()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "kept history");
        Assert.Equal(ServerTrustedChatStatus.Success, await host.WithServiceAsync(service => service.CloseAsync(conversation, Token)));

        var html = await RenderChatAsync(host, conversation, TestHost.Learner);

        Assert.Contains("kept history", html, StringComparison.Ordinal);
        Assert.Contains("This conversation is closed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Send</button>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observer_sees_read_only_notice_instead_of_composer()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync(withParent: true);
        await host.SendAsync(conversation, TestHost.Learner, "visible to parent");

        var html = await RenderChatAsync(host, conversation, TestHost.Parent);

        Assert.Contains("visible to parent", html, StringComparison.Ordinal);
        Assert.Contains("You can only read this conversation", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea", html, StringComparison.Ordinal);
        Assert.DoesNotContain("skopka-chat__observers", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tombstones_render_localized_strings_without_original_text()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var deleted = await host.SendAsync(conversation, TestHost.Teacher, "deleted synthetic secret");
        var hidden = await host.SendAsync(conversation, TestHost.Teacher, "hidden synthetic secret");
        var reply = await host.SendAsync(conversation, TestHost.Learner, "reply to hidden", hidden.Id);
        Assert.True((await host.WithServiceAsync(service => service.DeleteAsync(TestHost.Teacher, conversation, deleted.Id, Token))).IsSuccess);
        Assert.True((await host.WithServiceAsync(service => service.SetHiddenAsync(TestHost.Moderator, hidden.Id, true, Token))).IsSuccess);

        var html = await RenderChatAsync(host, conversation, TestHost.Learner);

        Assert.Contains("Message deleted by its author", html, StringComparison.Ordinal);
        Assert.Contains("Message hidden by a moderator", html, StringComparison.Ordinal);
        Assert.Contains(reply.Text!, html, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic secret", html, StringComparison.Ordinal);
        Assert.Contains("skopka-chat-message--tombstone", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_preview_shows_target_snippet()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var question = await host.SendAsync(conversation, TestHost.Teacher, "What is <2+2>?");
        await host.SendAsync(conversation, TestHost.Learner, "Four", question.Id);

        var html = await RenderChatAsync(host, conversation, TestHost.Learner);

        Assert.Contains("class=\"skopka-chat-message__reply\"", html, StringComparison.Ordinal);
        Assert.Contains("Reply to", html, StringComparison.Ordinal);
        Assert.Contains("What is &lt;2&#x2B;2&gt;?", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_body_empty_template_strings_and_theme_are_replaceable()
    {
        await using var host = TestHost.Create();
        var empty = await host.CreateLearnerTeacherAsync();
        var filled = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(filled, TestHost.Teacher, "markdown **source**");
        RenderFragment<TrustedMessage> body = message => builder =>
        {
            builder.OpenElement(0, "em");
            builder.AddAttribute(1, "class", "host-markdown");
            builder.AddContent(2, message.Text);
            builder.CloseElement();
        };
        RenderFragment emptyTemplate = builder => builder.AddMarkupContent(0, "<p class=\"host-empty\">Nothing here</p>");
        var strings = ServerTrustedChatStrings.Default with { Send = "<Go>", EmptyConversation = "unused" };

        var emptyHtml = await RenderChatAsync(host, empty, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.EmptyTemplate)] = emptyTemplate,
            [nameof(ServerTrustedChat.Strings)] = strings,
        });
        var filledHtml = await RenderChatAsync(host, filled, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.MessageBodyTemplate)] = body,
            [nameof(ServerTrustedChat.Strings)] = strings,
            [nameof(ServerTrustedChat.CssClass)] = "brand-chat",
            [nameof(ServerTrustedChat.Style)] = "--skopka-chat-accent: #123456",
            ["data-test"] = "island",
            [nameof(ServerTrustedChat.AuthorName)] = (Func<TrustedMessage, string>)(message => $"Author {message.AuthorLabel}"),
            [nameof(ServerTrustedChat.TimeFormatter)] = (Func<DateTimeOffset, string>)(_ => "10:00"),
        });

        Assert.Contains("host-empty", emptyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("unused", emptyHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;Go&gt;", emptyHtml, StringComparison.Ordinal);
        Assert.Contains("<em class=\"host-markdown\">markdown **source**</em>", filledHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("skopka-chat-message__text", filledHtml, StringComparison.Ordinal);
        Assert.Contains("skopka-chat skopka-chat--server-trusted brand-chat", filledHtml, StringComparison.Ordinal);
        Assert.Contains("--skopka-chat-accent: #123456", filledHtml, StringComparison.Ordinal);
        Assert.Contains("data-test=\"island\"", filledHtml, StringComparison.Ordinal);
        Assert.Contains("Author teacher", filledHtml, StringComparison.Ordinal);
        Assert.Contains("10:00", filledHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_and_composer_templates_replace_defaults()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "templated");
        await host.SendAsync(conversation, TestHost.Learner, "own templated");
        RenderFragment<ServerTrustedMessageContext> messageTemplate = context => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", context.IsOwn ? "host-own" : "host-other");
            builder.AddContent(2, $"{context.AuthorName}: {context.Message.Text} edit={context.CanEdit}");
            builder.CloseElement();
        };
        RenderFragment<ServerTrustedComposerContext> composerTemplate = context => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "host-composer");
            builder.AddContent(2, $"max={context.MaxLength} send={context.CanSend}");
            builder.CloseElement();
        };

        var html = await RenderChatAsync(host, conversation, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.MessageTemplate)] = messageTemplate,
            [nameof(ServerTrustedChat.ComposerTemplate)] = composerTemplate,
        });

        Assert.Contains("<div class=\"host-other\">teacher: templated edit=False</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"host-own\">You: own templated edit=False</div>", html, StringComparison.Ordinal);
        Assert.Contains("max=4000 send=False", html, StringComparison.Ordinal);
        Assert.DoesNotContain("skopka-chat-composer__input", html, StringComparison.Ordinal);
        Assert.DoesNotContain("skopka-chat-message__header", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Russian_preset_renders_and_has_no_empty_strings()
    {
        foreach (var strings in new[] { ServerTrustedChatStrings.Default, ServerTrustedChatStrings.Russian })
        {
            foreach (var property in typeof(ServerTrustedChatStrings).GetProperties().Where(item => item.PropertyType == typeof(string)))
            {
                Assert.False(string.IsNullOrWhiteSpace((string?)property.GetValue(strings)), property.Name);
            }
        }

        Assert.NotEqual(ServerTrustedChatStrings.Default.Send, ServerTrustedChatStrings.Russian.Send);
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var html = await RenderChatAsync(host, conversation, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.Strings)] = ServerTrustedChatStrings.Russian,
        });
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("Сообщений пока нет", decoded, StringComparison.Ordinal);
        Assert.Contains(">Отправить</button>", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_denial_renders_unavailable_without_message_text()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync(withParent: true);
        await host.SendAsync(conversation, TestHost.Teacher, "confidential synthetic text");
        host.Policy.Allow = context => context.ActorUserId != TestHost.Learner;

        var html = await RenderChatAsync(host, conversation, TestHost.Learner);

        Assert.Contains("This conversation is unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential", html, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"log\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("parent", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_participant_and_unknown_conversation_render_the_same_unavailable_state()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "participants only");

        var stranger = await RenderChatAsync(host, conversation, TestHost.Stranger);
        var unknown = await RenderChatAsync(host, TrustedConversationId.New(), TestHost.Learner);

        Assert.DoesNotContain("participants only", stranger, StringComparison.Ordinal);
        Assert.Contains("This conversation is unavailable", stranger, StringComparison.Ordinal);
        Assert.Equal(stranger, unknown);
    }

    [Fact]
    public async Task Prerender_persists_minimal_snapshot_that_restores_the_view_model()
    {
        await using var host = TestHost.Create(services => services.AddTestComponentStatePersistence());
        var conversation = await host.CreateLearnerTeacherAsync(withParent: true);
        await host.SendAsync(conversation, TestHost.Teacher, "persisted synthetic text");

        var state = await host.RenderStaticAndPersistAsync<ServerTrustedChat>(new()
        {
            [nameof(ServerTrustedChat.ConversationId)] = conversation.Value,
            [nameof(ServerTrustedChat.UserId)] = TestHost.Learner,
        });

        var entry = Assert.Single(state);
        Assert.Equal($"Skopka.Chat.ServerTrusted.Blazor/{conversation.Value:N}/{TestHost.Learner:N}", entry.Key);
        var json = Encoding.UTF8.GetString(entry.Value);
        Assert.Contains("persisted synthetic text", json, StringComparison.Ordinal);

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ServerTrustedChatSnapshot>(entry.Value, System.Text.Json.JsonSerializerOptions.Web);
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(model.TryRestore(snapshot));
        Assert.Equal("persisted synthetic text", Assert.Single(model.Messages).Text);
        Assert.Single(model.Observers);
        Assert.True(model.CanWrite);

        var otherUser = new ServerTrustedChatViewModel(conversation, TestHost.Teacher, host.CreateRunner());
        Assert.False(otherUser.TryRestore(snapshot));
        Assert.Empty(otherUser.Messages);
    }

    [Fact]
    public async Task Interactive_render_subscribes_marks_visible_messages_read_and_unsubscribes_on_dispose()
    {
        await using var host = TestHost.Create(interactiveJs: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        var first = await host.SendAsync(conversation, TestHost.Teacher, "first");
        var renderer = new InteractiveTestRenderer(host.Provider, host.Provider.GetRequiredService<ILoggerFactory>());
        try
        {
            await renderer.RenderAsync<ServerTrustedChat>(new()
            {
                [nameof(ServerTrustedChat.ConversationId)] = conversation.Value,
                [nameof(ServerTrustedChat.UserId)] = TestHost.Learner,
            });

            await TestHost.WaitUntilAsync(async () =>
                host.Hub.SubscriberCount == 1 &&
                await host.WithServiceAsync(service => service.IsReadThroughAsync(conversation, TestHost.Learner, first.Sequence, Token)) == true);

            var second = await host.SendAsync(conversation, TestHost.Teacher, "second");
            await TestHost.WaitUntilAsync(async () =>
                await host.WithServiceAsync(service => service.IsReadThroughAsync(conversation, TestHost.Learner, second.Sequence, Token)) == true);
            Assert.Equal(1, host.Hub.SubscriberCount);

            var js = host.Provider.GetRequiredService<VisibleDocumentJsRuntime>();
            Assert.Contains("import:./_content/Skopka.Chat.ServerTrusted.Blazor/serverTrustedChat.js", js.Calls);
            Assert.Contains("observeVisibility", js.Calls);
            Assert.Contains("isVisible", js.Calls);
        }
        finally
        {
            await renderer.DisposeAsync();
        }

        await TestHost.WaitUntilAsync(() => Task.FromResult(host.Hub.SubscriberCount == 0));
        Assert.Empty(renderer.Exceptions);
    }

    internal static Task<string> RenderChatAsync(TestHost host, TrustedConversationId conversation, Guid user, Dictionary<string, object?>? extra = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            [nameof(ServerTrustedChat.ConversationId)] = conversation.Value,
            [nameof(ServerTrustedChat.UserId)] = user,
        };
        foreach (var (key, value) in extra ?? [])
        {
            parameters[key] = value;
        }

        return host.RenderAsync<ServerTrustedChat>(parameters);
    }
}
