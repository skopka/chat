using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.Blazor;

namespace Skopka.Chat.ServerTrusted.Blazor.Tests;

public sealed class ServerTrustedChatUnreadBadgeTests
{
    [Fact]
    public async Task Badge_renders_unread_total_with_accessible_label()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "one");
        await host.SendAsync(conversation, TestHost.Teacher, "two");
        await host.SendAsync(conversation, TestHost.Learner, "own messages are never unread");

        var html = await RenderBadgeAsync(host, TestHost.Learner);

        Assert.Contains("class=\"skopka-chat-unread\"", html, StringComparison.Ordinal);
        Assert.Contains(">2</span>", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Unread messages: 2\"", html, StringComparison.Ordinal);
        Assert.Equal(0, host.Hub.SubscriberCount);
    }

    [Fact]
    public async Task Badge_renders_nothing_for_zero_unread()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Learner, "only own");

        var html = await RenderBadgeAsync(host, TestHost.Learner);

        Assert.True(string.IsNullOrWhiteSpace(html), html);
    }

    [Fact]
    public async Task Badge_caps_display_with_localized_overflow_text()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        for (var index = 0; index < 101; index++)
        {
            await host.SendAsync(conversation, TestHost.Teacher, $"message {index}");
        }

        var html = System.Net.WebUtility.HtmlDecode(
            await RenderBadgeAsync(host, TestHost.Learner, ServerTrustedChatStrings.Russian with { UnreadOverflow = "много" }, "menu-badge"));

        Assert.Contains(">много</span>", html, StringComparison.Ordinal);
        Assert.Contains("skopka-chat-unread menu-badge", html, StringComparison.Ordinal);
        Assert.Contains("Непрочитанные сообщения: много", html, StringComparison.Ordinal);
    }

    private static Task<string> RenderBadgeAsync(TestHost host, Guid user, ServerTrustedChatStrings? strings = null, string? cssClass = null) =>
        host.RenderAsync<ServerTrustedChatUnreadBadge>(new Dictionary<string, object?>
        {
            [nameof(ServerTrustedChatUnreadBadge.UserId)] = user,
            [nameof(ServerTrustedChatUnreadBadge.Strings)] = strings,
            [nameof(ServerTrustedChatUnreadBadge.CssClass)] = cssClass,
        });
}
