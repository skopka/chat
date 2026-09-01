using Microsoft.EntityFrameworkCore;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Testing;

namespace Skopka.Chat.Persistence.PostgreSql.Tests;

public sealed class PostgreSqlPersistenceTests
{
    [Fact]
    public void Persistence_model_has_no_plaintext_or_private_key_columns()
    {
        using var context = CreateContext("Host=localhost;Database=skopka_model_only;Username=unused;Password=unused");
        var propertyNames = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Migration_is_discoverable()
    {
        using var context = CreateContext("Host=localhost;Database=skopka_model_only;Username=unused;Password=unused");

        var migrations = context.Database.GetMigrations();

        Assert.Contains("202608310001_InitialEncryptedChatStorage", migrations);
        Assert.Contains("202609010002_DeterministicPendingDeliveryOrder", migrations);
    }

    [Fact]
    public async Task PostgreSql_saves_and_selects_ciphertext_envelopes()
    {
        var connectionString = await GetPostgreSqlConnectionStringOrSkipAsync();

        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        var store = new PostgreSqlChatStore(context);
        var engine = new ChatServerEngine(store, store, store);
        var now = DateTimeOffset.UtcNow;
        var alice = Device(UserId.New(), DeviceId.New(), KeyId.New(), 1, now);
        var bob = Device(UserId.New(), DeviceId.New(), KeyId.New(), 65, now);
        var conversationId = ConversationId.New();
        var messageId = MessageId.New();

        try
        {
            await engine.RegisterDeviceAsync(alice);
            await engine.RegisterDeviceAsync(bob);
            await engine.CreateConversationAsync(alice.UserId, bob.UserId, conversationId, now);
            var ciphertext = new byte[] { 0x91, 0xF0, 0x0D, 0xA5, 0x7E };
            var envelope = Envelope(messageId, conversationId, alice, bob, now, ciphertext);

            Assert.Equal(SubmitEnvelopeResult.Accepted, await engine.SubmitAsync(envelope, now.AddSeconds(1)));
            var stored = Assert.Single(await engine.ReceiveAsync(bob.DeviceId, 10, now.AddSeconds(2)));
            Assert.Equal(ciphertext, stored.Envelope.Ciphertext.ToArray());
            Assert.Equal(envelope.Signature.ToArray(), stored.Envelope.Signature.ToArray());
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM envelopes WHERE message_id = {messageId.Value}");
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM conversations WHERE conversation_id = {conversationId.Value}");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM devices WHERE device_id = {alice.DeviceId.Value} OR device_id = {bob.DeviceId.Value}");
        }
    }

    [Fact]
    public async Task Concurrent_identical_inserts_create_one_envelope_and_report_duplicates()
    {
        await using var scenario = await PostgreSqlScenario.CreateAsync();
        var envelope = scenario.CreateEnvelope(MessageId.New(), [0x10, 0x20, 0x30]);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submissions = Enumerable.Range(0, 8)
            .Select(_ => StoreAfterSignalAsync(
                scenario.ConnectionString,
                envelope,
                scenario.Now.AddSeconds(1),
                start.Task))
            .ToArray();

        start.SetResult(true);
        var results = await Task.WhenAll(submissions);

        Assert.Equal(1, results.Count(result => result == EnvelopeStoreResult.Inserted));
        Assert.Equal(7, results.Count(result => result == EnvelopeStoreResult.Duplicate));
        await using var verificationContext = CreateContext(scenario.ConnectionString);
        var repository = (IEnvelopeRepository)new PostgreSqlChatStore(verificationContext);
        Assert.Single(await repository.GetPendingAsync(scenario.Bob.DeviceId, 10, scenario.Now));
    }

    [Fact]
    public async Task Concurrent_conflicting_inserts_accept_one_canonical_envelope()
    {
        await using var scenario = await PostgreSqlScenario.CreateAsync();
        var messageId = MessageId.New();
        var first = scenario.CreateEnvelope(messageId, [0x41, 0x42, 0x43]);
        var second = scenario.CreateEnvelope(messageId, [0x51, 0x52, 0x53]);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submissions = new[]
        {
            StoreAfterSignalAsync(scenario.ConnectionString, first, scenario.Now.AddSeconds(1), start.Task),
            StoreAfterSignalAsync(scenario.ConnectionString, second, scenario.Now.AddSeconds(1), start.Task)
        };

        start.SetResult(true);
        var results = await Task.WhenAll(submissions);

        Assert.Equal(1, results.Count(result => result == EnvelopeStoreResult.Inserted));
        Assert.Equal(1, results.Count(result => result == EnvelopeStoreResult.Conflict));
        await using var verificationContext = CreateContext(scenario.ConnectionString);
        var repository = (IEnvelopeRepository)new PostgreSqlChatStore(verificationContext);
        var stored = Assert.Single(await repository.GetPendingAsync(scenario.Bob.DeviceId, 10, scenario.Now));
        Assert.True(
            stored.Envelope.Ciphertext.Span.SequenceEqual(first.Ciphertext.Span) ||
            stored.Envelope.Ciphertext.Span.SequenceEqual(second.Ciphertext.Span));
    }

    [Fact]
    public async Task Concurrent_polling_is_at_least_once_and_first_acknowledgement_wins()
    {
        await using var scenario = await PostgreSqlScenario.CreateAsync();
        var envelope = scenario.CreateEnvelope(MessageId.New(), [0x61, 0x62, 0x63]);
        await using (var submissionContext = CreateContext(scenario.ConnectionString))
        {
            var engine = CreateEngine(submissionContext);
            Assert.Equal(
                SubmitEnvelopeResult.Accepted,
                await engine.SubmitAsync(envelope, scenario.Now.AddSeconds(1)));
        }

        var pollStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = new[]
        {
            ReceiveAfterSignalAsync(scenario, pollStart.Task),
            ReceiveAfterSignalAsync(scenario, pollStart.Task)
        };
        pollStart.SetResult(true);
        var batches = await Task.WhenAll(polls);

        Assert.All(batches, batch =>
            Assert.Equal(envelope.MessageId, Assert.Single(batch).Envelope.MessageId));

        var ackStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgements = new[]
        {
            AcknowledgeAfterSignalAsync(scenario, envelope.MessageId, ackStart.Task),
            AcknowledgeAfterSignalAsync(scenario, envelope.MessageId, ackStart.Task)
        };
        ackStart.SetResult(true);
        var acknowledged = await Task.WhenAll(acknowledgements);

        Assert.Equal(1, acknowledged.Count(result => result));
        Assert.Equal(1, acknowledged.Count(result => !result));
        await using var verificationContext = CreateContext(scenario.ConnectionString);
        Assert.Empty(await CreateEngine(verificationContext)
            .ReceiveAsync(scenario.Bob.DeviceId, 10, scenario.Now.AddSeconds(3)));
    }

    [Fact]
    public async Task Pending_batch_has_a_deterministic_message_id_tie_breaker()
    {
        await using var scenario = await PostgreSqlScenario.CreateAsync();
        var firstId = new MessageId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var secondId = new MessageId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var thirdId = new MessageId(Guid.Parse("00000000-0000-0000-0000-000000000003"));
        await using var context = CreateContext(scenario.ConnectionString);
        var repository = (IEnvelopeRepository)new PostgreSqlChatStore(context);

        Assert.Equal(EnvelopeStoreResult.Inserted, await repository.TryAddAsync(
            scenario.CreateEnvelope(thirdId, [3]), scenario.Now));
        Assert.Equal(EnvelopeStoreResult.Inserted, await repository.TryAddAsync(
            scenario.CreateEnvelope(firstId, [1]), scenario.Now));
        Assert.Equal(EnvelopeStoreResult.Inserted, await repository.TryAddAsync(
            scenario.CreateEnvelope(secondId, [2]), scenario.Now));

        var batch = await repository.GetPendingAsync(scenario.Bob.DeviceId, 2, scenario.Now);

        Assert.Equal([firstId, secondId], batch.Select(item => item.Envelope.MessageId));
    }

    [Fact]
    public async Task Ttl_cleanup_deletes_only_expired_envelopes_and_is_idempotent()
    {
        await using var scenario = await PostgreSqlScenario.CreateAsync();
        var expired = scenario.CreateEnvelope(
            MessageId.New(),
            [0x71],
            scenario.Now.AddMinutes(1));
        var live = scenario.CreateEnvelope(
            MessageId.New(),
            [0x72],
            scenario.Now.AddMinutes(3));
        await using (var submissionContext = CreateContext(scenario.ConnectionString))
        {
            var engine = CreateEngine(submissionContext);
            Assert.Equal(SubmitEnvelopeResult.Accepted, await engine.SubmitAsync(expired, scenario.Now));
            Assert.Equal(SubmitEnvelopeResult.Accepted, await engine.SubmitAsync(live, scenario.Now));
        }

        await using var cleanupContext = CreateContext(scenario.ConnectionString);
        var repository = (IEnvelopeRepository)new PostgreSqlChatStore(cleanupContext);
        var cleanupAt = scenario.Now.AddMinutes(2);

        Assert.Equal(1, await repository.DeleteExpiredAsync(cleanupAt));
        Assert.Equal(0, await repository.DeleteExpiredAsync(cleanupAt));
        var pending = Assert.Single(await repository.GetPendingAsync(scenario.Bob.DeviceId, 10, cleanupAt));
        Assert.Equal(live.MessageId, pending.Envelope.MessageId);
    }

    private static ValueTask<string> GetPostgreSqlConnectionStringOrSkipAsync() =>
        PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync();

    private static ChatServerEngine CreateEngine(ChatDbContext context)
    {
        var store = new PostgreSqlChatStore(context);
        return new ChatServerEngine(store, store, store);
    }

    private static async Task<EnvelopeStoreResult> StoreAfterSignalAsync(
        string connectionString,
        EncryptedEnvelope envelope,
        DateTimeOffset acceptedAt,
        Task start)
    {
        await start;
        await using var context = CreateContext(connectionString);
        var repository = (IEnvelopeRepository)new PostgreSqlChatStore(context);
        return await repository.TryAddAsync(envelope, acceptedAt);
    }

    private static async Task<IReadOnlyList<StoredEnvelope>> ReceiveAfterSignalAsync(
        PostgreSqlScenario scenario,
        Task start)
    {
        await start;
        await using var context = CreateContext(scenario.ConnectionString);
        return await CreateEngine(context).ReceiveAsync(
            scenario.Bob.DeviceId,
            10,
            scenario.Now.AddSeconds(2));
    }

    private static async Task<bool> AcknowledgeAfterSignalAsync(
        PostgreSqlScenario scenario,
        MessageId messageId,
        Task start)
    {
        await start;
        await using var context = CreateContext(scenario.ConnectionString);
        return await CreateEngine(context).AcknowledgeAsync(
            scenario.Bob.DeviceId,
            messageId,
            scenario.Now.AddSeconds(3));
    }

    private static ChatDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ChatDbContext(options);
    }

    private static PublicDevice Device(
        UserId userId,
        DeviceId deviceId,
        KeyId keyId,
        int seed,
        DateTimeOffset registeredAt) => new(
        userId,
        deviceId,
        keyId,
        Enumerable.Range(seed, 32).Select(value => (byte)value).ToArray(),
        Enumerable.Range(seed + 32, 32).Select(value => (byte)value).ToArray(),
        registeredAt);

    private static EncryptedEnvelope Envelope(
        MessageId messageId,
        ConversationId conversationId,
        PublicDevice sender,
        PublicDevice recipient,
        DateTimeOffset now,
        byte[] ciphertext,
        DateTimeOffset? expiresAt = null) => new(
        ProtocolVersions.V1,
        messageId,
        conversationId,
        sender.DeviceId,
        recipient.DeviceId,
        sender.KeyId,
        recipient.KeyId,
        now,
        expiresAt ?? now.AddDays(1),
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
        Enumerable.Range(32, 24).Select(value => (byte)value).ToArray(),
        ciphertext,
        Enumerable.Repeat((byte)7, 16).ToArray(),
        Enumerable.Repeat((byte)9, 64).ToArray());

    private sealed class PostgreSqlScenario : IAsyncDisposable
    {
        private PostgreSqlScenario(
            string connectionString,
            DateTimeOffset now,
            PublicDevice alice,
            PublicDevice bob,
            ConversationId conversationId)
        {
            ConnectionString = connectionString;
            Now = now;
            Alice = alice;
            Bob = bob;
            ConversationId = conversationId;
        }

        public string ConnectionString { get; }
        public DateTimeOffset Now { get; }
        public PublicDevice Alice { get; }
        public PublicDevice Bob { get; }
        public ConversationId ConversationId { get; }

        public static async Task<PostgreSqlScenario> CreateAsync()
        {
            var connectionString = await GetPostgreSqlConnectionStringOrSkipAsync();
            var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
            var alice = Device(UserId.New(), DeviceId.New(), KeyId.New(), 1, now);
            var bob = Device(UserId.New(), DeviceId.New(), KeyId.New(), 65, now);
            var scenario = new PostgreSqlScenario(
                connectionString,
                now,
                alice,
                bob,
                ConversationId.New());
            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();

            try
            {
                var engine = CreateEngine(context);
                await engine.RegisterDeviceAsync(alice);
                await engine.RegisterDeviceAsync(bob);
                await engine.CreateConversationAsync(alice.UserId, bob.UserId, scenario.ConversationId, now);
                return scenario;
            }
            catch
            {
                await scenario.DisposeAsync();
                throw;
            }
        }

        public EncryptedEnvelope CreateEnvelope(
            MessageId messageId,
            byte[] ciphertext,
            DateTimeOffset? expiresAt = null) =>
            Envelope(messageId, ConversationId, Alice, Bob, Now, ciphertext, expiresAt);

        public async ValueTask DisposeAsync()
        {
            await using var context = CreateContext(ConnectionString);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM envelopes WHERE conversation_id = {ConversationId.Value}");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM conversations WHERE conversation_id = {ConversationId.Value}");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM devices WHERE device_id = {Alice.DeviceId.Value} OR device_id = {Bob.DeviceId.Value}");
        }
    }
}
