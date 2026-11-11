using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Skopka.Chat.Attachments;
using Skopka.Chat.Server;
using Skopka.Chat.Testing;

#pragma warning disable EF1002 // Test SQL interpolates only the generated schema identifier and fixed table names.

namespace Skopka.Chat.ServerTrusted.PostgreSql.Tests;

public sealed class PostgreSqlServerTrustedChatTests
{
    private static readonly Guid Learner = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Teacher = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Parent = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid Manager = Guid.Parse("20000000-0000-0000-0000-000000000004");
    private static readonly Guid Surrogate = Guid.Parse("20000000-0000-0000-0000-0000000000ff");

    [Fact]
    public void Migration_is_discoverable_and_schema_names_are_validated()
    {
        using var context = new ServerTrustedChatDbContext(ServerTrustedChatDbContext.CreateOptions("Host=localhost;Database=unused;Username=unused;Password=unused", "custom_schema"));
        Assert.Contains("202611110001_InitialServerTrustedChatStorage", context.Database.GetMigrations());
        Assert.Equal("custom_schema", context.Model.GetDefaultSchema());
        Assert.Throws<ArgumentException>(() => ServerTrustedChatDbContext.CreateOptions("Host=localhost", "Bad-Schema"));
        Assert.Throws<ArgumentException>(() => ServerTrustedChatDbContext.CreateOptions("Host=localhost", "x\"; DROP SCHEMA public; --"));
        Assert.False(ServerTrustedChatSchema.IsValid(new string('a', 49)));
    }

    [Fact]
    public void Postgresql_assembly_references_server_only_for_outbox_and_no_e2ee_client_or_persistence()
    {
        var references = typeof(PostgreSqlServerTrustedChatStore).Assembly.GetReferencedAssemblies().Select(static item => item.Name).ToArray();
        Assert.Contains("Skopka.Chat.Server", references);
        Assert.Contains("Skopka.Chat.ServerTrusted", references);
        foreach (var forbidden in new[] { "Skopka.Chat.Client", "Skopka.Chat.Persistence.PostgreSql", "Skopka.Chat.UI.Core", "NSec.Cryptography" })
        {
            Assert.DoesNotContain(forbidden, references);
        }
    }

    [Fact]
    public async Task Migration_creates_every_table_and_history_inside_the_configured_schema()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name", connection);
        command.Parameters.AddWithValue("schema", database.Schema);
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            ["__skopka_chat_trusted_migrations", "access_log", "attachments", "blob_deletions", "conversations", "event_outbox", "message_versions", "messages", "participants", "reports"],
            tables);
        await using var publicTables = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('messages', 'conversations')", connection);
        Assert.Equal(0L, await publicTables.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concurrent_sends_get_unique_gap_free_sequences_revisions_and_one_event_each()
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(async () =>
            await database.NewService().SendAsync(index % 2 == 0 ? Learner : Teacher, conversation.Id, Text($"m{index}")))));

        Assert.All(results, static item => Assert.True(item.IsSuccess));
        Assert.Equal(Enumerable.Range(1, 40).Select(static item => (long)item), results.Select(static item => item.Value!.Sequence).Order());
        Assert.Equal(40, results.Select(static item => item.Value!.ChangeRevision).Distinct().Count());
        Assert.Equal(40, await database.CountAsync("event_outbox"));
        var stored = (await database.NewService().GetConversationAsync(Learner, conversation.Id)).Value!.Conversation;
        Assert.Equal((40L, 40L), (stored.LastSequence, stored.ChangeRevision));
    }

    [Fact]
    public async Task Concurrent_exact_retries_create_one_message_and_one_event()
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        var request = Text("synthetic once");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            await database.NewService().SendAsync(Learner, conversation.Id, request))));

        Assert.All(results, static item => Assert.True(item.IsSuccess));
        Assert.Single(results.Select(static item => item.Value!.Id).Distinct());
        Assert.Equal(1, await database.CountAsync("messages"));
        Assert.Equal(1, await database.CountAsync("event_outbox"));
        Assert.Equal(ServerTrustedChatStatus.Conflict, (await database.NewService().SendAsync(Learner, conversation.Id, request with { Text = "other" })).Status);
    }

    [Fact]
    public async Task Close_racing_sends_never_accepts_a_message_after_closure()
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        var sends = Enumerable.Range(0, 30).Select(index => Task.Run(async () =>
            await database.NewService().SendAsync(Learner, conversation.Id, Text($"m{index}")))).ToArray();
        var close = Task.Run(async () => await database.NewService().CloseAsync(conversation.Id));
        var results = await Task.WhenAll(sends);
        Assert.Equal(ServerTrustedChatStatus.Success, await close);

        Assert.All(results, static item => Assert.True(item.Status is ServerTrustedChatStatus.Success or ServerTrustedChatStatus.Closed));
        var accepted = results.Count(static item => item.IsSuccess);
        Assert.Equal(accepted, await database.CountAsync("messages"));
        Assert.Equal(accepted, await database.CountAsync("event_outbox"));
        Assert.Equal(ServerTrustedChatStatus.Closed, (await database.NewService().SendAsync(Learner, conversation.Id, Text("late"))).Status);
        await using var db = database.NewContext();
        var closedAt = await db.Database.SqlQueryRaw<DateTimeOffset>($"SELECT closed_at AS \"Value\" FROM \"{database.Schema}\".conversations").SingleAsync();
        Assert.DoesNotContain(results.Where(static item => item.IsSuccess), item => item.Value!.CreatedAt > closedAt.AddSeconds(1));
    }

    [Fact]
    public async Task Concurrent_read_markers_keep_the_clamped_maximum_and_emit_only_advancing_events()
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        for (var index = 0; index < 10; index++)
        {
            await database.NewService().SendAsync(Learner, conversation.Id, Text($"m{index}"));
        }

        var targets = Enumerable.Range(1, 30).Select(static item => (long)(item % 12)).ToArray();
        var results = await Task.WhenAll(targets.Select(target => Task.Run(async () =>
            await database.NewService().MarkReadAsync(Teacher, conversation.Id, target))));

        Assert.All(results, static item => Assert.True(item.IsSuccess));
        Assert.True(await database.NewService().IsReadThroughAsync(conversation.Id, Teacher, 10));
        Assert.Equal(0, await database.NewService().GetUnreadTotalAsync(Teacher));
        await using var db = database.NewContext();
        var sequences = (await db.Database.SqlQueryRaw<long>(
                $"SELECT (convert_from(payload, 'UTF8')::jsonb ->> 'readThroughSequence')::bigint AS \"Value\" FROM \"{database.Schema}\".event_outbox WHERE event_type = 'skopka.chat.server-trusted.read-marker-advanced' ORDER BY occurred_at, event_id")
            .ToListAsync());
        Assert.Equal(sequences.Distinct().Count(), sequences.Count);
        Assert.Equal(10, sequences.Max());
    }

    [Fact]
    public async Task Concurrent_edit_and_delete_leave_a_consistent_message_with_unique_revisions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        var message = (await database.NewService().SendAsync(Teacher, conversation.Id, Text("original"))).Value!;
        var edits = Enumerable.Range(0, 10).Select(index => Task.Run(async () =>
            await database.NewService().EditAsync(Teacher, conversation.Id, message.Id, $"edit {index}"))).ToArray();
        var delete = Task.Run(async () => await database.NewService().DeleteAsync(Teacher, conversation.Id, message.Id));
        var editResults = await Task.WhenAll(edits);
        Assert.True((await delete).IsSuccess);

        Assert.All(editResults, static item => Assert.True(item.Status is ServerTrustedChatStatus.Success or ServerTrustedChatStatus.Conflict));
        var history = (await database.NewService().GetHistoryAsync(Learner, conversation.Id)).Value!;
        Assert.Equal(TrustedMessageTombstone.DeletedByAuthor, Assert.Single(history.Messages).Tombstone);
        Assert.Equal(editResults.Count(static item => item.IsSuccess), await database.CountAsync("message_versions"));
        Assert.Equal(1 + editResults.Count(static item => item.IsSuccess) + 1, history.ChangeRevision);
    }

    [Fact]
    public async Task Concurrent_get_or_create_by_host_key_creates_one_conversation_and_participant_set()
    {
        await using var database = await TestDatabase.CreateAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Task.Run(async () =>
            (await database.NewService().GetOrCreateConversationAsync("iqzone:mentor:race", TrustedConversationKind.Direct, Participants())).Value!.Id)));
        Assert.Single(results.Distinct());
        Assert.Equal(1, await database.CountAsync("conversations"));
        Assert.Equal(2, await database.CountAsync("participants"));
    }

    [Fact]
    public async Task Concurrent_reports_by_one_reporter_create_one_report_and_one_event()
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        var message = (await database.NewService().SendAsync(Teacher, conversation.Id, Text("synthetic"))).Value!;
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            await database.NewService().ReportAsync(Learner, message.Id, "abuse", "synthetic comment"))));
        Assert.Single(results.Select(static item => item.Value!.Id).Distinct());
        Assert.Equal(1, await database.CountAsync("reports"));
        Assert.Equal(1, await database.CountAsync("event_outbox", "event_type = 'skopka.chat.server-trusted.message-reported'"));
    }

    [Fact]
    public async Task Report_window_is_logged_atomically_and_shows_retained_content_to_the_moderator()
    {
        await using var database = await TestDatabase.CreateAsync(options => options.ReportWindowRadius = 1);
        var conversation = await database.CreateMentorChatAsync();
        var service = database.NewService();
        var before = (await service.SendAsync(Teacher, conversation.Id, Text("before"))).Value!;
        var target = (await service.SendAsync(Teacher, conversation.Id, Text("rude original"))).Value!;
        await service.EditAsync(Teacher, conversation.Id, target.Id, "softened");
        var after = (await service.SendAsync(Teacher, conversation.Id, Text("deleted later"))).Value!;
        await service.SendAsync(Teacher, conversation.Id, Text("outside window"));
        var report = (await service.ReportAsync(Learner, target.Id, "abuse")).Value!;
        await service.DeleteAsync(Teacher, conversation.Id, after.Id);

        Assert.Equal(ServerTrustedChatStatus.NotFoundOrForbidden, (await service.ReadReportWindowAsync(Learner, report.Id)).Status);
        var window = (await service.ReadReportWindowAsync(Manager, report.Id)).Value!;
        Assert.Equal([before.Id, target.Id, after.Id], window.Messages.Select(static item => item.Message.Id));
        var reported = window.Messages.Single(static item => item.IsReportTarget);
        Assert.Equal("softened", reported.RetainedText);
        Assert.Equal("rude original", Assert.Single(reported.PriorVersions));
        Assert.Equal("deleted later", window.Messages[2].RetainedText);
        Assert.Null(window.Messages[2].Message.Text);
        var entry = Assert.Single(await service.GetAccessLogAsync(conversation.Id));
        Assert.Equal((Manager, TrustedAccessAction.ReadReportWindow, 1L, 3L), (entry.ActorUserId, entry.Action, entry.FromSequence!.Value, entry.ToSequence!.Value));

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var revoke = new NpgsqlCommand($"ALTER TABLE \"{database.Schema}\".access_log ADD CONSTRAINT ck_block CHECK (false) NOT VALID", connection);
            await revoke.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<DbUpdateException>(async () => await service.ReadReportWindowAsync(Manager, report.Id));
    }

    [Theory]
    [InlineData(TrustedForgetMode.EraseContent)]
    [InlineData(TrustedForgetMode.ReassignAuthor)]
    public async Task Forget_user_removes_participation_markers_and_replaces_ids(TrustedForgetMode mode)
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        var service = database.NewService();
        var teacherMessage = (await service.SendAsync(Teacher, conversation.Id, Text("teacher text"))).Value!;
        await service.EditAsync(Teacher, conversation.Id, teacherMessage.Id, "teacher text 2");
        var learnerMessage = (await service.SendAsync(Learner, conversation.Id, Text("learner text"))).Value!;
        await service.MarkReadAsync(Teacher, conversation.Id, 2);
        var report = (await service.ReportAsync(Teacher, learnerMessage.Id, "abuse")).Value!;
        await service.ReadReportWindowAsync(Manager, report.Id);
        var revision = (await service.GetHistoryAsync(Learner, conversation.Id)).Value!.ChangeRevision;

        Assert.Equal(ServerTrustedChatStatus.Success, await service.ForgetUserAsync(Teacher, mode, Surrogate));
        Assert.Equal(ServerTrustedChatStatus.Success, await service.ForgetUserAsync(Manager, mode, Surrogate));

        Assert.Equal(1, await database.CountAsync("participants"));
        Assert.Null(await service.IsReadThroughAsync(conversation.Id, Teacher, 1));
        var changes = (await service.GetChangesAsync(Learner, conversation.Id, revision)).Value!.Messages;
        var former = Assert.Single(changes);
        Assert.Equal(Surrogate, former.AuthorUserId);
        Assert.Equal(mode == TrustedForgetMode.EraseContent ? TrustedMessageTombstone.Erased : TrustedMessageTombstone.None, former.Tombstone);
        Assert.Equal(mode == TrustedForgetMode.EraseContent ? 0 : 1, await database.CountAsync("message_versions"));
        Assert.Equal(1, await database.CountAsync("reports", $"reporter_user_id = '{Surrogate}'"));
        Assert.Equal(1, await database.CountAsync("access_log", $"actor_user_id = '{Surrogate}'"));
        Assert.Equal(0, await database.CountAsync("messages", $"author_user_id = '{Teacher}'"));
        Assert.Equal(mode == TrustedForgetMode.EraseContent ? 1 : 0, await database.CountAsync("messages", "is_erased AND host_references = '[]'"));

        Assert.Equal(ServerTrustedChatStatus.Success, await service.DeleteConversationAsync(conversation.Id));
        Assert.Equal(1, await database.CountAsync("access_log"));
        Assert.Equal(1, await service.PurgeAccessLogBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(0, await database.CountAsync("access_log"));
    }

    [Fact]
    public async Task Unread_counts_respect_capping_visibility_tombstones_and_stored_read_capability()
    {
        await using var database = await TestDatabase.CreateAsync(options => options.UnreadCountCap = 5);
        var conversation = await database.CreateMentorChatAsync();
        var service = database.NewService();
        var first = (await service.SendAsync(Learner, conversation.Id, Text("hidden soon"))).Value!;
        for (var index = 0; index < 7; index++)
        {
            await service.SendAsync(Learner, conversation.Id, Text($"m{index}"));
        }

        Assert.Equal(5, await service.GetUnreadTotalAsync(Teacher));
        await service.SetParticipantAsync(conversation.Id, new TrustedParticipantSpec(Parent, TrustedParticipantRole.Observer, "parent", seesHistory: false));
        Assert.Equal(0, await service.GetUnreadTotalAsync(Parent));
        await service.SendAsync(Teacher, conversation.Id, Text("new"));
        Assert.Equal(1, await service.GetUnreadTotalAsync(Parent));

        await service.MarkReadAsync(Teacher, conversation.Id, 5);
        Assert.Equal(3, await service.GetUnreadTotalAsync(Teacher));
        await service.SetHiddenAsync(Manager, first.Id, true);
        await service.SetParticipantAsync(conversation.Id, new TrustedParticipantSpec(Teacher, TrustedParticipantRole.Writer, "teacher", canRead: false));
        Assert.Equal(0, await service.GetUnreadTotalAsync(Teacher));
        Assert.Single(await service.ListConversationsAsync(Parent));
    }

    [Fact]
    public async Task Attachments_bind_once_and_queued_blob_deletions_survive_until_completed()
    {
        var blobs = new MemoryBlobStore();
        await using var database = await TestDatabase.CreateAsync(blobs: blobs);
        var conversation = await database.CreateMentorChatAsync();
        var service = database.NewService();
        var uploaded = (await service.UploadAttachmentAsync(Learner, conversation.Id, "a.png", "image/png", new MemoryStream(Encoding.UTF8.GetBytes("synthetic image")))).Value!;
        var sends = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
            await database.NewService().SendAsync(Learner, conversation.Id, new TrustedSendRequest { ClientMessageId = Guid.NewGuid(), AttachmentIds = [uploaded.Id] }))));
        Assert.Single(sends, static item => item.IsSuccess);
        Assert.All(sends.Where(static item => !item.IsSuccess), static item => Assert.Equal(ServerTrustedChatStatus.Conflict, item.Status));
        var download = (await service.OpenAttachmentAsync(Teacher, uploaded.Id)).Value!;
        using (var destination = new MemoryStream())
        {
            await download.CopyToAsync(destination);
            Assert.Equal("synthetic image", Encoding.UTF8.GetString(destination.ToArray()));
        }

        blobs.FailDeletes = true;
        await Assert.ThrowsAsync<IOException>(async () => await service.DeleteConversationAsync(conversation.Id));
        Assert.Equal(0, await database.CountAsync("conversations"));
        Assert.Equal(1, await database.CountAsync("blob_deletions"));
        blobs.FailDeletes = false;
        Assert.Equal(1, await service.RunMaintenanceAsync());
        Assert.Equal(0, await database.CountAsync("blob_deletions"));
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task Outbox_redelivers_after_lease_expiry_and_dispatcher_reschedules_failures()
    {
        await using var database = await TestDatabase.CreateAsync();
        var conversation = await database.CreateMentorChatAsync();
        await database.NewService().SendAsync(Learner, conversation.Id, Text("synthetic"));
        var outbox = new PostgreSqlServerTrustedChatEventOutbox(database.Factory);
        var now = DateTimeOffset.UtcNow;

        var claimed = Assert.Single(await outbox.ClaimPendingAsync("worker-a", 10, now, TimeSpan.FromSeconds(30)));
        Assert.Empty(await outbox.ClaimPendingAsync("worker-b", 10, now, TimeSpan.FromSeconds(30)));
        var reclaimed = Assert.Single(await outbox.ClaimPendingAsync("worker-b", 10, now.AddMinutes(1), TimeSpan.FromSeconds(30)));
        Assert.Equal(claimed.Message.EventId, reclaimed.Message.EventId);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.False(await outbox.MarkPublishedAsync(claimed.Message.EventId, "worker-a", now.AddMinutes(1)));
        Assert.True(await outbox.MarkPublishedAsync(claimed.Message.EventId, "worker-b", now.AddMinutes(1)));

        await database.NewService().SendAsync(Learner, conversation.Id, Text("second"));
        var failing = new RecordingPublisher { Fail = true };
        var dispatcher = new ChatServerEventDispatcher(outbox, failing, TimeProvider.System);
        Assert.Equal(new ChatServerEventDispatchResult(1, 0, 1), await dispatcher.DispatchBatchAsync("worker-c"));
        Assert.Equal(1, await database.CountAsync("event_outbox", "published_at IS NULL AND last_failed_at IS NOT NULL"));
        Assert.Equal(1, await outbox.DeletePublishedBeforeAsync(now.AddHours(1), 100));
    }

    [Fact]
    public async Task Notify_bridge_and_dispatch_worker_deliver_metadata_after_commit()
    {
        await using var database = await TestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IServerTrustedChatAccessPolicy, AllowAllPolicy>();
        services.AddSingleton<IServerTrustedChatRateLimiter, UnlimitedServerTrustedChatRateLimiter>();
        services.AddSkopkaChatServerTrustedPostgreSql(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.Schema = database.Schema;
            options.EnableMaintenance = false;
            options.Dispatch.IdleDelay = TimeSpan.FromMilliseconds(100);
        });
        var publisher = new RecordingPublisher();
        services.AddSingleton<IServerTrustedChatEventPublisher>(publisher);
        services.AddSkopkaChatServerTrustedEventDispatch<RecordingPublisher>();
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            var hub = provider.GetRequiredService<IServerTrustedChatLiveSignals>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var signals = hub.SubscribeAsync(null, timeout.Token).GetAsyncEnumerator(timeout.Token);
            var next = signals.MoveNextAsync().AsTask();
            try
            {
                await Task.Delay(1000, timeout.Token);

                // A foreign process: a second engine whose signal sink is not the hub, so only NOTIFY can reach the hub.
                var conversation = await database.CreateMentorChatAsync();
                var foreign = database.NewService(signals: null);
                var sent = (await foreign.SendAsync(Learner, conversation.Id, Text("synthetic body"))).Value!;
                ServerTrustedChatSignal received;
                do
                {
                    Assert.True(await next);
                    received = signals.Current;
                    next = signals.MoveNextAsync().AsTask();
                }
                while (received.Kind != ServerTrustedChatSignalKind.MessagesChanged);

                Assert.Equal(new ServerTrustedChatSignal(conversation.Id, ServerTrustedChatSignalKind.MessagesChanged, sent.ChangeRevision), received);
                while (publisher.Published.IsEmpty)
                {
                    await Task.Delay(100, timeout.Token);
                }

                var message = Assert.Single(publisher.Published);
                Assert.Equal(ServerTrustedChatEventTypes.MessageCreated, message.EventType);
                Assert.DoesNotContain("synthetic body", Encoding.UTF8.GetString(message.Payload.Span), StringComparison.Ordinal);
            }
            finally
            {
                await timeout.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
                await signals.DisposeAsync();
            }
        }
        finally
        {
            foreach (var service in hosted)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    private static TrustedSendRequest Text(string text) => new() { ClientMessageId = Guid.NewGuid(), Text = text };

    private static List<TrustedParticipantSpec> Participants() =>
    [
        new(Learner, TrustedParticipantRole.Writer, "learner"),
        new(Teacher, TrustedParticipantRole.Writer, "teacher"),
    ];

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly ServerTrustedChatOptions _options;
        private readonly MemoryBlobStore? _blobs;

        private TestDatabase(string connectionString, string schema, ServerTrustedChatOptions options, MemoryBlobStore? blobs)
        {
            ConnectionString = connectionString;
            Schema = schema;
            _options = options;
            _blobs = blobs;
            Factory = new ContextFactory(ServerTrustedChatDbContext.CreateOptions(connectionString, schema));
            Hub = new InProcessServerTrustedChatSignalHub();
        }

        public string ConnectionString { get; }

        public string Schema { get; }

        public ContextFactory Factory { get; }

        public InProcessServerTrustedChatSignalHub Hub { get; }

        public static async Task<TestDatabase> CreateAsync(Action<ServerTrustedChatOptions>? configure = null, MemoryBlobStore? blobs = null)
        {
            var connectionString = await PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync();
            var options = new ServerTrustedChatOptions();
            configure?.Invoke(options);
            var database = new TestDatabase(connectionString, "st_" + Guid.NewGuid().ToString("N")[..20], options, blobs);
            await using var context = database.NewContext();
            await context.Database.MigrateAsync();
            return database;
        }

        public ServerTrustedChatDbContext NewContext() => Factory.CreateDbContext();

        public ServerTrustedChatService NewService() => NewService(Hub);

        public ServerTrustedChatService NewService(IServerTrustedChatSignalSink? signals) =>
            new(new PostgreSqlServerTrustedChatStore(Factory), new AllowAllPolicy(), new UnlimitedServerTrustedChatRateLimiter(), _options, TimeProvider.System, signals,
                _blobs, _blobs is null ? null : new AcceptAllValidator());

        public async Task<TrustedConversation> CreateMentorChatAsync() =>
            (await NewService().GetOrCreateConversationAsync($"iqzone:mentor:{Learner}:{Teacher}", TrustedConversationKind.Direct, Participants())).Value!;

        public async Task<long> CountAsync(string table, string? where = null)
        {
            await using var context = NewContext();
            return await context.Database.SqlQueryRaw<long>($"SELECT count(*)::bigint AS \"Value\" FROM \"{Schema}\".{table}{(where is null ? string.Empty : " WHERE " + where)}").SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{Schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class ContextFactory(DbContextOptions<ServerTrustedChatDbContext> options) : IDbContextFactory<ServerTrustedChatDbContext>
    {
        public ServerTrustedChatDbContext CreateDbContext() => new(options);
    }

    private sealed class AllowAllPolicy : IServerTrustedChatAccessPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ServerTrustedAccessContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class AcceptAllValidator : IServerTrustedAttachmentValidator
    {
        public ValueTask<ServerTrustedAttachmentVerdict> ValidateAsync(ServerTrustedAttachmentCandidate candidate, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ServerTrustedAttachmentVerdict.Accept("image/png"));
    }

    private sealed class RecordingPublisher : IChatServerEventPublisher, IServerTrustedChatEventPublisher
    {
        public bool Fail { get; set; }

        public System.Collections.Concurrent.ConcurrentQueue<ChatServerOutboxMessage> Published { get; } = new();

        public ValueTask PublishAsync(ChatServerOutboxMessage message, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("synthetic broker failure");
            }

            Published.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryBlobStore : IAttachmentStore
    {
        public System.Collections.Concurrent.ConcurrentDictionary<AttachmentId, byte[]> Blobs { get; } = new();

        public bool FailDeletes { get; set; }

        public ValueTask<StoredAttachment?> GetMetadataAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default) => ValueTask.FromResult<StoredAttachment?>(null);

        public async ValueTask<AttachmentStoreResult> TryPutAsync(StoredAttachment attachment, Stream ciphertext, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await ciphertext.CopyToAsync(buffer, cancellationToken);
            Assert.True(SHA256.HashData(buffer.ToArray()).AsSpan().SequenceEqual(attachment.CiphertextSha256.Span));
            return Blobs.TryAdd(attachment.AttachmentId, buffer.ToArray()) ? AttachmentStoreResult.Stored : AttachmentStoreResult.Conflict;
        }

        public async ValueTask CopyToAsync(AttachmentId attachmentId, Stream destination, CancellationToken cancellationToken = default) =>
            await destination.WriteAsync(Blobs[attachmentId], cancellationToken);

        public ValueTask<bool> DeleteAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default) =>
            FailDeletes ? throw new IOException("synthetic storage outage") : ValueTask.FromResult(Blobs.TryRemove(attachmentId, out _));

        public ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }
}
