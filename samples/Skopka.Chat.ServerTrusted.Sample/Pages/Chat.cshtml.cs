using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Skopka.Chat.ServerTrusted.Sample.Pages;

[Authorize]
public sealed class ChatModel(ServerTrustedChatService chat) : PageModel
{
    public Guid ConversationId { get; private set; }

    public Guid UserId { get; private set; }

    public string? LessonId { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? lesson)
    {
        if (!SampleUsers.TryGetCurrent(User, out var user))
        {
            return Challenge();
        }

        var conversation = await chat.FindConversationAsync(SampleData.HostKey, HttpContext.RequestAborted);
        if (conversation is null)
        {
            return NotFound();
        }

        ConversationId = conversation.Id.Value;
        UserId = user.Id;
        LessonId = lesson is { Length: > 0 and <= 64 } && lesson.All(char.IsAsciiLetterOrDigit) ? lesson : null;
        return Page();
    }
}
