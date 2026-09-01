using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Text.Encodings.Web;
using Skopka.Chat.Attachments;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.UI;
using Skopka.Chat.UI.Blazor;

namespace Skopka.Chat.UI.Blazor.Tests;

public sealed class BlazorRenderingTests
{
    private static readonly ConversationId ConversationId =
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly UserId CurrentUserId =
        new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly UserId PeerUserId =
        new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    private static readonly DeviceId PeerDeviceId =
        new(Guid.Parse("30000000-0000-0000-0000-000000000002"));

    [Fact]
    public void Blazor_assembly_depends_on_ui_core_but_not_server_or_persistence()
    {
        var references = typeof(SkopkaChat).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();

        Assert.Contains("Skopka.Chat.UI.Core", references);
        Assert.DoesNotContain("Skopka.Chat.Server", references);
        Assert.DoesNotContain("Skopka.Chat.Persistence.PostgreSql", references);
    }

    [Fact]
    public async Task Default_component_renders_encoded_message_and_accessible_composer()
    {
        var model = CreateModel();
        model.Apply(new ReceivedChatContent(
            new MessageId(Guid.Parse("40000000-0000-0000-0000-000000000001")),
            ConversationId,
            PeerUserId,
            PeerDeviceId,
            DateTimeOffset.Parse("2026-09-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new ChatTextContent(
                new ChatContentId(Guid.Parse("50000000-0000-0000-0000-000000000001")),
                "<script>private text</script>")));

        var html = await RenderAsync<SkopkaChat>(new Dictionary<string, object?>
        {
            [nameof(SkopkaChat.ViewModel)] = model,
            [nameof(SkopkaChat.TimeFormatter)] = (Func<DateTimeOffset, string>)(_ => "10:00"),
        });

        Assert.Contains("role=\"log\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Message\"", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;private text&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>private text</script>", html, StringComparison.Ordinal);
        Assert.Contains("10:00", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Templates_strings_and_theme_scope_are_replaceable()
    {
        var model = CreateModel();
        model.Apply(new ReceivedChatContent(
            new MessageId(Guid.Parse("40000000-0000-0000-0000-000000000002")),
            ConversationId,
            PeerUserId,
            PeerDeviceId,
            DateTimeOffset.Parse("2026-09-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new ChatTextContent(
                new ChatContentId(Guid.Parse("50000000-0000-0000-0000-000000000002")),
                "template text")));
        RenderFragment<ChatMessageTemplateContext> messageTemplate = context => builder =>
        {
            builder.OpenElement(0, "strong");
            builder.AddAttribute(1, "class", "host-message");
            builder.AddContent(2, context.Message.Text);
            builder.CloseElement();
        };
        RenderFragment<ChatComposerTemplateContext> composerTemplate = _ => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "host-composer");
            builder.AddContent(2, "custom composer");
            builder.CloseElement();
        };

        var html = await RenderAsync<SkopkaChat>(new Dictionary<string, object?>
        {
            [nameof(SkopkaChat.ViewModel)] = model,
            [nameof(SkopkaChat.CssClass)] = "brand-chat",
            [nameof(SkopkaChat.Style)] = "--skopka-chat-accent: rebeccapurple",
            [nameof(SkopkaChat.Strings)] = SkopkaChatStrings.Default with { Timeline = "Сообщения" },
            [nameof(SkopkaChat.MessageTemplate)] = messageTemplate,
            [nameof(SkopkaChat.ComposerTemplate)] = composerTemplate,
        });

        Assert.Contains("brand-chat", html, StringComparison.Ordinal);
        Assert.Contains("--skopka-chat-accent: rebeccapurple", html, StringComparison.Ordinal);
        Assert.Contains($"aria-label=\"{HtmlEncoder.Default.Encode("Сообщения")}\"", html, StringComparison.Ordinal);
        Assert.Contains("host-message", html, StringComparison.Ordinal);
        Assert.Contains("template text", html, StringComparison.Ordinal);
        Assert.Contains("host-composer", html, StringComparison.Ordinal);
        Assert.DoesNotContain("skopka-chat-message__text", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attachment_default_is_safe_and_host_can_replace_the_entire_card()
    {
        var model = CreateModel();
        model.Apply(IncomingAttachment("<img src=x>.jpg"));

        var defaultHtml = await RenderAsync<SkopkaChat>(new Dictionary<string, object?>
        {
            [nameof(SkopkaChat.ViewModel)] = model,
        });

        Assert.Contains("skopka-chat-attachment__file", defaultHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x&gt;.jpg", defaultHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x>", defaultHtml, StringComparison.Ordinal);

        RenderFragment<ChatAttachmentTemplateContext> template = context => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "host-attachment");
            builder.AddContent(2, context.Attachment.FileName);
            builder.CloseElement();
        };
        var customHtml = await RenderAsync<SkopkaChat>(new Dictionary<string, object?>
        {
            [nameof(SkopkaChat.ViewModel)] = model,
            [nameof(SkopkaChat.AttachmentTemplate)] = template,
        });

        Assert.Contains("host-attachment", customHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("skopka-chat-attachment__file", customHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composer_exposes_optional_media_picker_with_auto_as_the_default_mode()
    {
        var model = CreateModel();
        ChatBrowserAttachmentSender sender = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        };

        var withoutMedia = await RenderAsync<SkopkaChat>(new Dictionary<string, object?>
        {
            [nameof(SkopkaChat.ViewModel)] = model,
        });
        Assert.DoesNotContain("type=\"file\"", withoutMedia, StringComparison.Ordinal);

        var withMedia = await RenderAsync<SkopkaChat>(new Dictionary<string, object?>
        {
            [nameof(SkopkaChat.ViewModel)] = model,
            [nameof(SkopkaChat.AttachmentSender)] = sender,
        });

        Assert.Contains("type=\"file\"", withMedia, StringComparison.Ordinal);
        Assert.Contains("accept=\"image/*,video/*\"", withMedia, StringComparison.Ordinal);
        Assert.Contains("Send as file", withMedia, StringComparison.Ordinal);
        Assert.DoesNotContain("checked", withMedia, StringComparison.OrdinalIgnoreCase);
    }

    private static ChatViewModel CreateModel() =>
        new(ConversationId, CurrentUserId, new FailingSender());

    private static ReceivedChatContent IncomingAttachment(string fileName)
    {
        const long plaintextLength = 10;
        const long ciphertextLength = plaintextLength + 4 + 16;
        const int chunkBytes = ChatAttachmentCryptoService.MinChunkPlaintextBytes;
        return new ReceivedChatContent(
            new MessageId(Guid.Parse("40000000-0000-0000-0000-000000000003")),
            ConversationId,
            PeerUserId,
            PeerDeviceId,
            DateTimeOffset.Parse("2026-09-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new ChatAttachmentContent(
                new ChatContentId(Guid.Parse("50000000-0000-0000-0000-000000000003")),
                new AttachmentId(Guid.Parse("60000000-0000-0000-0000-000000000003")),
                fileName,
                "image/jpeg",
                plaintextLength,
                ciphertextLength,
                chunkBytes,
                new byte[AttachmentStorageLimits.Sha256Bytes],
                new byte[32],
                new byte[16]));
    }

    private static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime, TestJsRuntime>();
        await using var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(provider, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
    }

    private sealed class FailingSender : IChatContentSender
    {
        public ValueTask<ChatContentSendResult> SendAsync(
            ConversationId conversationId,
            ChatContent content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ChatContentSendResult.Failed);
        }
    }

    private sealed class TestJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
