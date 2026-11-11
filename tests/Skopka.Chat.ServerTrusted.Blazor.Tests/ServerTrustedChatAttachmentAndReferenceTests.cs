using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.Blazor;

namespace Skopka.Chat.ServerTrusted.Blazor.Tests;

public sealed class ServerTrustedChatAttachmentAndReferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly HostReference Lesson = new("lesson", "4f0d2c1e-0000-0000-0000-000000000001");

    [Fact]
    public async Task Upload_then_send_carries_attachment_and_reference_without_text()
    {
        await using var host = TestHost.Create(attachments: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        Assert.False(model.CanSend);

        using (var content = new MemoryStream([0x89, 0x50, 0x4e, 0x47]))
        {
            Assert.True(await model.UploadAttachmentAsync("photo.png", "image/png", content, Token));
        }

        Assert.Equal("photo.png", Assert.Single(model.PendingAttachments).FileName);
        Assert.False(model.IsUploading);
        Assert.True(model.CanSend);
        Assert.True(model.AddReference(Lesson));
        Assert.True(await model.SendDraftAsync(Token));

        var stored = Assert.Single(await host.HistoryAsync(conversation, TestHost.Teacher));
        Assert.Null(stored.Text);
        Assert.Equal("photo.png", Assert.Single(stored.Attachments).FileName);
        Assert.Equal(Lesson, Assert.Single(stored.References));
        Assert.Empty(model.PendingAttachments);
        Assert.Empty(model.DraftReferences);
        Assert.False(model.CanSend);
    }

    [Fact]
    public async Task Upload_limits_and_rejections_map_to_generic_attachment_failure()
    {
        await using var host = TestHost.Create(attachments: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));

        Assert.False(await UploadAsync(model, "reject.png"));
        Assert.Equal(ServerTrustedChatFailure.AttachmentRejected, model.Failure);
        Assert.False(await UploadAsync(model, "bad/name.png"));
        Assert.Equal(ServerTrustedChatFailure.AttachmentRejected, model.Failure);

        for (var index = 0; index < model.MaxAttachmentsPerMessage; index++)
        {
            Assert.True(await UploadAsync(model, $"photo{index}.png"));
        }

        Assert.False(model.CanUpload);
        Assert.False(await UploadAsync(model, "one-too-many.png"));
        Assert.Equal(ServerTrustedChatFailure.AttachmentRejected, model.Failure);
        Assert.Equal(3, model.PendingAttachments.Count);

        Assert.True(model.RemovePendingAttachment(model.PendingAttachments[0].Id));
        Assert.False(model.RemovePendingAttachment(TrustedAttachmentId.New()));
        Assert.True(model.CanUpload);
        Assert.Equal(2, model.PendingAttachments.Count);
    }

    [Fact]
    public async Task Upload_without_engine_attachment_support_is_rejected_but_keeps_the_timeline()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "history");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));

        Assert.False(await UploadAsync(model, "photo.png"));

        Assert.Equal(ServerTrustedChatFailure.AttachmentRejected, model.Failure);
        Assert.Single(model.Messages);
        Assert.Empty(model.PendingAttachments);
    }

    [Fact]
    public async Task Upload_is_refused_for_observer_and_closed_conversation()
    {
        await using var host = TestHost.Create(attachments: true);
        var conversation = await host.CreateLearnerTeacherAsync(withParent: true);
        var observer = new ServerTrustedChatViewModel(conversation, TestHost.Parent, host.CreateRunner());
        var writer = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await observer.LoadAsync(Token));
        Assert.True(await writer.LoadAsync(Token));

        Assert.False(observer.CanUpload);
        Assert.False(await UploadAsync(observer, "photo.png"));
        Assert.Equal(ServerTrustedChatStatus.Success, await host.WithServiceAsync(service => service.CloseAsync(conversation, Token)));
        Assert.False(await UploadAsync(writer, "photo.png"));
        Assert.Equal(ServerTrustedChatFailure.Closed, writer.Failure);
        Assert.True(writer.IsClosed);
    }

    [Fact]
    public async Task Changing_attachments_or_references_after_a_lost_response_starts_a_new_logical_send()
    {
        await using var host = TestHost.Create(attachments: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        var runner = new LossyRunner(host.CreateRunner());
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, runner);
        Assert.True(await model.LoadAsync(Token));
        model.SetDraft("same text");
        runner.LoseNextResponse();
        Assert.False(await model.SendDraftAsync(Token));

        // Same text with a reference added would be a conflicting retry if the client message ID were reused.
        Assert.True(model.AddReference(Lesson));
        Assert.True(await model.SendDraftAsync(Token));

        var stored = await host.HistoryAsync(conversation, TestHost.Learner);
        Assert.Equal(2, stored.Count);
        Assert.Empty(stored[0].References);
        Assert.Equal(Lesson, Assert.Single(stored[1].References));
        Assert.Equal(ServerTrustedChatFailure.None, model.Failure);
    }

    [Fact]
    public async Task Editing_never_sends_pending_attachments_or_references()
    {
        await using var host = TestHost.Create(attachments: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        var own = await host.SendAsync(conversation, TestHost.Learner, "typo");
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));
        Assert.True(await UploadAsync(model, "photo.png"));
        Assert.True(model.AddReference(Lesson));

        Assert.True(model.BeginEdit(own.Id));
        Assert.False(model.CanUpload);
        Assert.False(await UploadAsync(model, "while-editing.png"));
        model.SetDraft("fixed");
        Assert.True(await model.SendDraftAsync(Token));

        var edited = Assert.Single(await host.HistoryAsync(conversation, TestHost.Learner));
        Assert.Equal("fixed", edited.Text);
        Assert.Empty(edited.Attachments);
        Assert.Empty(edited.References);
        Assert.Single(model.PendingAttachments);
        Assert.Single(model.DraftReferences);
    }

    [Fact]
    public async Task Draft_references_are_distinct_and_bounded()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        var model = new ServerTrustedChatViewModel(conversation, TestHost.Learner, host.CreateRunner());
        Assert.True(await model.LoadAsync(Token));

        Assert.True(model.AddReference(new HostReference("lesson", "1")));
        Assert.False(model.AddReference(new HostReference("lesson", "1")));
        for (var index = 2; index <= ServerTrustedChatLimits.MaxHostReferences; index++)
        {
            Assert.True(model.AddReference(new HostReference("task", index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        Assert.False(model.AddReference(new HostReference("task", "overflow")));
        Assert.Equal(ServerTrustedChatLimits.MaxHostReferences, model.DraftReferences.Count);
        Assert.True(model.RemoveReference(new HostReference("lesson", "1")));
        Assert.False(model.RemoveReference(new HostReference("lesson", "1")));
        model.ClearReferences();
        Assert.Empty(model.DraftReferences);
    }

    [Fact]
    public async Task Message_references_and_attachments_render_with_safe_links_only()
    {
        await using var host = TestHost.Create(attachments: true);
        var conversation = await host.CreateLearnerTeacherAsync();
        var safe = await host.UploadAsync(conversation, TestHost.Teacher, "safe.png");
        var script = await host.UploadAsync(conversation, TestHost.Teacher, "script.png");
        var data = await host.UploadAsync(conversation, TestHost.Teacher, "data.png");
        await host.SendAsync(conversation, TestHost.Teacher, "with files", attachments: [safe.Id, script.Id, data.Id]);
        var slashes = await host.UploadAsync(conversation, TestHost.Teacher, "slashes.png");
        var backslashes = await host.UploadAsync(conversation, TestHost.Teacher, "backslashes.png");
        var mixed = await host.UploadAsync(conversation, TestHost.Teacher, "mixed.png");
        await host.SendAsync(conversation, TestHost.Teacher, "more files", attachments: [slashes.Id, backslashes.Id, mixed.Id]);
        await host.SendAsync(conversation, TestHost.Teacher, "with links", references:
        [
            new HostReference("lesson", "safe"),
            new HostReference("lesson", "script"),
            new HostReference("lesson", "data"),
            new HostReference("lesson", "slashes"),
        ]);
        await host.SendAsync(conversation, TestHost.Teacher, "more links", references:
        [
            new HostReference("task", "backslashes"),
            new HostReference("task", "mixed"),
            new HostReference("task", "spaced"),
        ]);
        var unsafeUrls = new Dictionary<string, string>
        {
            ["script"] = "javascript:alert(1)",
            ["data"] = "data:text/html,<b>x</b>",
            ["slashes"] = "//evil.example/x",
            ["backslashes"] = "\\\\evil.example/x",
            ["mixed"] = "/\\evil.example/x",
            ["spaced"] = " //evil.example/x",
        };
        string? Url(string key) => key == "safe" ? $"/files/{key}" : unsafeUrls.GetValueOrDefault(key);

        var html = await ServerTrustedChatRenderingTests.RenderChatAsync(host, conversation, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.AttachmentUrl)] = (Func<TrustedAttachmentInfo, string>)(attachment => Url(Path.GetFileNameWithoutExtension(attachment.FileName))!),
            [nameof(ServerTrustedChat.ReferenceUrl)] = (Func<HostReference, string?>)(reference => Url(reference.Id)),
            [nameof(ServerTrustedChat.ReferenceLabel)] = (Func<HostReference, string>)(reference => $"<{reference.Type} {reference.Id}>"),
        });

        Assert.Contains("href=\"/files/safe\"", html, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(html, "href=\""));
        Assert.DoesNotContain("evil.example", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data:", html, StringComparison.Ordinal);
        Assert.Contains("slashes.png", html, StringComparison.Ordinal);
        Assert.Contains("&lt;lesson script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;task spaced&gt;", html, StringComparison.Ordinal);
        Assert.Contains("class=\"skopka-chat-message__references\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/files/1", true)]
    [InlineData("files/1?x=a:b", true)]
    [InlineData("https://cdn.example/x", true)]
    [InlineData("HTTP://cdn.example/x", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("vbscript:x", false)]
    [InlineData("//evil.example", false)]
    [InlineData("\\\\evil.example", false)]
    [InlineData("\\evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData(" //evil.example", false)]
    [InlineData("/files/1 ", false)]
    [InlineData("/files/\n1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Link_filter_accepts_only_same_origin_relative_and_http_urls(string? url, bool expected) =>
        Assert.Equal(expected, ServerTrustedChat.IsSafeLink(url));

    [Fact]
    public async Task Reference_template_replaces_default_reference_rendering()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();
        await host.SendAsync(conversation, TestHost.Teacher, "see lesson", references: [Lesson]);
        Microsoft.AspNetCore.Components.RenderFragment<HostReference> template = reference => builder =>
        {
            builder.OpenElement(0, "strong");
            builder.AddAttribute(1, "class", "host-reference");
            builder.AddContent(2, reference.Id);
            builder.CloseElement();
        };

        var html = await ServerTrustedChatRenderingTests.RenderChatAsync(host, conversation, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.ReferenceTemplate)] = template,
        });

        Assert.Contains($"<strong class=\"host-reference\">{Lesson.Id}</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"skopka-chat-reference\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_draft_reference_parameters_render_a_removable_chip()
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();

        var html = await ServerTrustedChatRenderingTests.RenderChatAsync(host, conversation, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.DraftReferenceType)] = "lesson",
            [nameof(ServerTrustedChat.DraftReferenceId)] = "42",
            [nameof(ServerTrustedChat.ReferenceLabel)] = (Func<HostReference, string>)(reference => $"Lesson {reference.Id}"),
            [nameof(ServerTrustedChat.ReferenceUrl)] = (Func<HostReference, string?>)(reference => $"/lessons/{reference.Id}"),
        });

        Assert.Contains("skopka-chat-chip--reference", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"skopka-chat-reference\" href=\"/lessons/42\" rel=\"noopener noreferrer\">Lesson 42</a>", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Remove link: Lesson 42\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Lesson", "42")]
    [InlineData("lesson", "with space")]
    [InlineData("lesson", null)]
    [InlineData(null, "42")]
    [InlineData("9lesson", "42")]
    public async Task Invalid_draft_reference_parameters_are_ignored(string? type, string? id)
    {
        await using var host = TestHost.Create();
        var conversation = await host.CreateLearnerTeacherAsync();

        var html = await ServerTrustedChatRenderingTests.RenderChatAsync(host, conversation, TestHost.Learner, new()
        {
            [nameof(ServerTrustedChat.DraftReferenceType)] = type,
            [nameof(ServerTrustedChat.DraftReferenceId)] = id,
        });

        Assert.DoesNotContain("skopka-chat-chip", html, StringComparison.Ordinal);
        Assert.Contains("<textarea", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_input_renders_only_when_allowed_for_a_writer_of_an_open_conversation()
    {
        await using var host = TestHost.Create(attachments: true);
        var open = await host.CreateLearnerTeacherAsync(withParent: true);
        var closed = await host.CreateLearnerTeacherAsync();
        Assert.Equal(ServerTrustedChatStatus.Success, await host.WithServiceAsync(service => service.CloseAsync(closed, Token)));
        var allowed = new Dictionary<string, object?>
        {
            [nameof(ServerTrustedChat.AllowAttachments)] = true,
            [nameof(ServerTrustedChat.AttachmentAccept)] = "image/png,image/jpeg",
        };

        var writer = await ServerTrustedChatRenderingTests.RenderChatAsync(host, open, TestHost.Learner, allowed);
        var notAllowed = await ServerTrustedChatRenderingTests.RenderChatAsync(host, open, TestHost.Learner);
        var observer = await ServerTrustedChatRenderingTests.RenderChatAsync(host, open, TestHost.Parent, allowed);
        var closedHtml = await ServerTrustedChatRenderingTests.RenderChatAsync(host, closed, TestHost.Learner, allowed);

        Assert.Contains("type=\"file\"", writer, StringComparison.Ordinal);
        Assert.Contains("accept=\"image/png,image/jpeg\"", writer, StringComparison.Ordinal);
        Assert.Contains("Attach file", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"file\"", notAllowed, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"file\"", observer, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"file\"", closedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachment_strings_exist_in_both_presets()
    {
        foreach (var strings in new[] { ServerTrustedChatStrings.Default, ServerTrustedChatStrings.Russian })
        {
            Assert.False(string.IsNullOrWhiteSpace(strings.AttachFile));
            Assert.False(string.IsNullOrWhiteSpace(strings.RemoveAttachment));
            Assert.False(string.IsNullOrWhiteSpace(strings.Uploading));
            Assert.False(string.IsNullOrWhiteSpace(strings.AttachmentRejected));
            Assert.False(string.IsNullOrWhiteSpace(strings.References));
            Assert.False(string.IsNullOrWhiteSpace(strings.RemoveReference));
        }

        Assert.NotEqual(ServerTrustedChatStrings.Default.AttachFile, ServerTrustedChatStrings.Russian.AttachFile);
    }

    private static async Task<bool> UploadAsync(ServerTrustedChatViewModel model, string fileName)
    {
        using var content = new MemoryStream([1, 2, 3]);
        return await model.UploadAttachmentAsync(fileName, "image/png", content, Token);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        for (var index = value.IndexOf(fragment, StringComparison.Ordinal); index >= 0; index = value.IndexOf(fragment, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
