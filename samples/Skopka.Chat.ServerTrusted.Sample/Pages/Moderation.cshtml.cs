using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Skopka.Chat.ServerTrusted.Sample.Pages;

/// <summary>Sample moderation view: a non-participant manager reads the window around a reported message.</summary>
[Authorize]
public sealed class ModerationModel(ServerTrustedChatService chat, IServiceProvider services) : PageModel
{
    public IReadOnlyList<Guid> ReportIds { get; private set; } = [];

    public ModerationWindow? Window { get; private set; }

    public IReadOnlyList<TrustedAccessLogEntry> AccessLog { get; private set; } = [];

    public bool Denied { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? report)
    {
        if (!SampleUsers.TryGetCurrent(User, out var user))
        {
            return Challenge();
        }

        // Sample only: real hosts learn about reports from the message-reported outbox event.
        ReportIds = (services.GetService<InMemoryServerTrustedChatStore>()?.Events ?? [])
            .Where(item => item.EventType == ServerTrustedChatEventTypes.MessageReported)
            .Select(item => JsonDocument.Parse(item.Payload).RootElement.GetProperty("reportId").GetGuid())
            .ToArray();
        if (report is { } reportId)
        {
            var result = await chat.ReadReportWindowAsync(user.Id, new TrustedReportId(reportId), HttpContext.RequestAborted);
            Window = result.Value;
            Denied = !result.IsSuccess;
        }

        var conversation = await chat.FindConversationAsync(SampleData.HostKey, HttpContext.RequestAborted);
        if (conversation is not null)
        {
            AccessLog = await chat.GetAccessLogAsync(conversation.Id, 20, HttpContext.RequestAborted);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostHideAsync(Guid message, Guid report, bool hidden)
    {
        if (!SampleUsers.TryGetCurrent(User, out var user))
        {
            return Challenge();
        }

        await chat.SetHiddenAsync(user.Id, new TrustedMessageId(message), hidden, HttpContext.RequestAborted);
        return RedirectToPage(new { report });
    }
}
