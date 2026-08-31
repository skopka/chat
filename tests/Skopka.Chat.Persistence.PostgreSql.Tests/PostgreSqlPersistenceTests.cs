using Microsoft.EntityFrameworkCore;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

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
    }

    [Fact]
    public async Task PostgreSql_saves_and_selects_ciphertext_envelopes()
    {
        var connectionString = Environment.GetEnvironmentVariable("SKOPKA_CHAT_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Skip("Set SKOPKA_CHAT_POSTGRES to a disposable PostgreSQL database to run this integration test.");
        }

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
        byte[] ciphertext) => new(
        ProtocolVersions.V1,
        messageId,
        conversationId,
        sender.DeviceId,
        recipient.DeviceId,
        sender.KeyId,
        recipient.KeyId,
        now,
        now.AddDays(1),
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
        Enumerable.Range(32, 24).Select(value => (byte)value).ToArray(),
        ciphertext,
        Enumerable.Repeat((byte)7, 16).ToArray(),
        Enumerable.Repeat((byte)9, 64).ToArray());
}
