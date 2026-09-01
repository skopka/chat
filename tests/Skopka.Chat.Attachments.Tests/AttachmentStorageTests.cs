using System.Security.Cryptography;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Skopka.Chat.Attachments.PostgreSql;
using Skopka.Chat.Attachments.S3;
using Skopka.Chat.Protocol;
using Skopka.Chat.Testing;

namespace Skopka.Chat.Attachments.Tests;

public sealed class AttachmentStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Attachment_packages_preserve_the_storage_dependency_boundary()
    {
        var coreReferences = typeof(AttachmentStorageService).Assembly.GetReferencedAssemblies()
            .Select(static item => item.Name)
            .ToArray();
        Assert.Contains("Skopka.Chat.Protocol", coreReferences);
        Assert.DoesNotContain("Skopka.Chat.Client", coreReferences);
        Assert.DoesNotContain(coreReferences, static item => item?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(coreReferences, static item => item?.StartsWith("AWSSDK", StringComparison.Ordinal) == true);

        var postgreSqlReferences = typeof(PostgreSqlAttachmentStore).Assembly.GetReferencedAssemblies()
            .Select(static item => item.Name)
            .ToArray();
        Assert.Contains("Skopka.Chat.Attachments", postgreSqlReferences);
        Assert.DoesNotContain("Skopka.Chat.Client", postgreSqlReferences);
        Assert.DoesNotContain("Skopka.Chat.Server", postgreSqlReferences);

        var s3References = typeof(S3AttachmentStore).Assembly.GetReferencedAssemblies()
            .Select(static item => item.Name)
            .ToArray();
        Assert.Contains("Skopka.Chat.Attachments", s3References);
        Assert.DoesNotContain("Skopka.Chat.Client", s3References);
        Assert.DoesNotContain("Skopka.Chat.Server", s3References);
    }

    [Fact]
    public async Task Service_rejects_unauthorized_upload_before_opening_storage()
    {
        var store = new RecordingStore();
        var service = new AttachmentStorageService(store, new DenyingAuthorizer(), new FixedTimeProvider(Now));
        var ciphertext = new byte[] { 1, 2, 3 };
        var request = new AttachmentUploadRequest(
            AttachmentId.New(),
            ConversationId.New(),
            ciphertext.Length,
            SHA256.HashData(ciphertext));

        await Assert.ThrowsAsync<AttachmentServiceException>(async () =>
            await service.UploadAsync(UserId.New(), request, new MemoryStream(ciphertext, writable: false)));

        Assert.False(store.PutCalled);
    }

    [Fact]
    public async Task Service_uses_authenticated_uploader_and_redacts_authorization_failures()
    {
        var userId = UserId.New();
        var store = new RecordingStore();
        var service = new AttachmentStorageService(store, new AllowingAuthorizer(), new FixedTimeProvider(Now));
        var ciphertext = new byte[] { 4, 5, 6 };
        var request = new AttachmentUploadRequest(
            AttachmentId.New(),
            ConversationId.New(),
            ciphertext.Length,
            SHA256.HashData(ciphertext));

        Assert.Equal(
            AttachmentStoreResult.Stored,
            await service.UploadAsync(userId, request, new MemoryStream(ciphertext, writable: false)));
        Assert.Equal(userId, Assert.IsType<StoredAttachment>(store.Stored).UploaderUserId);
        Assert.Equal(Now, store.Stored.CreatedAt);
    }

    [Fact]
    public void PostgreSql_model_and_migration_contain_ciphertext_only()
    {
        using var context = CreateContext("Host=localhost;Database=skopka_attachment_model;Username=unused;Password=unused");
        var properties = context.Model.GetEntityTypes()
            .SelectMany(static entity => entity.GetProperties())
            .Select(static property => property.Name)
            .ToArray();

        Assert.Contains("Ciphertext", properties);
        Assert.DoesNotContain(properties, static name => name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, static name => name.Contains("FileName", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, static name => name.Contains("MediaType", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("202609010003_InitialEncryptedAttachmentStorage", context.Database.GetMigrations());
    }

    [Fact]
    public async Task PostgreSql_store_validates_hash_and_preserves_immutable_idempotency()
    {
        var connectionString = await GetPostgreSqlConnectionStringOrSkipAsync();
        var attachmentId = AttachmentId.New();
        var conversationId = ConversationId.New();
        var uploaderUserId = UserId.New();
        var ciphertext = Enumerable.Range(1, 128).Select(static value => (byte)value).ToArray();
        var metadata = new StoredAttachment(
            attachmentId,
            conversationId,
            uploaderUserId,
            ciphertext.Length,
            SHA256.HashData(ciphertext),
            Now);

        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        var store = new PostgreSqlAttachmentStore(context);
        try
        {
            Assert.Equal(
                AttachmentStoreResult.Stored,
                await store.TryPutAsync(metadata, new MemoryStream(ciphertext, writable: false)));
            Assert.Equal(
                AttachmentStoreResult.Duplicate,
                await store.TryPutAsync(metadata, new MemoryStream(ciphertext, writable: false)));

            var conflicting = new StoredAttachment(
                attachmentId,
                conversationId,
                uploaderUserId,
                ciphertext.Length,
                SHA256.HashData(ciphertext),
                Now,
                Now.AddDays(1));
            Assert.Equal(
                AttachmentStoreResult.Conflict,
                await store.TryPutAsync(conflicting, new MemoryStream(ciphertext, writable: false)));

            await using var copied = new MemoryStream();
            await store.CopyToAsync(attachmentId, copied);
            Assert.Equal(ciphertext, copied.ToArray());
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM chat_attachments WHERE attachment_id = {attachmentId.Value}");
        }
    }

    [Fact]
    public async Task S3_store_validates_ciphertext_before_any_network_request()
    {
        using var client = new AmazonS3Client("synthetic-access", "synthetic-secret", new AmazonS3Config
        {
            ServiceURL = "http://127.0.0.1:1",
            ForcePathStyle = true
        });
        var options = new S3AttachmentStoreOptions { BucketName = "encrypted-attachments", KeyPrefix = "chat" };
        var store = new S3AttachmentStore(client, options);
        var bytes = new byte[] { 1, 2, 3 };
        var metadata = new StoredAttachment(
            AttachmentId.New(),
            ConversationId.New(),
            UserId.New(),
            bytes.Length,
            new byte[32],
            Now);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.TryPutAsync(metadata, new MemoryStream(bytes, writable: false)));
    }

    private static AttachmentDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AttachmentDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AttachmentDbContext(options);
    }

    private static ValueTask<string> GetPostgreSqlConnectionStringOrSkipAsync() =>
        PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync();

    private sealed class RecordingStore : IAttachmentStore
    {
        public bool PutCalled { get; private set; }
        public StoredAttachment? Stored { get; private set; }

        public ValueTask<StoredAttachment?> GetMetadataAsync(
            AttachmentId attachmentId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Stored);

        public ValueTask<AttachmentStoreResult> TryPutAsync(
            StoredAttachment attachment,
            Stream ciphertext,
            CancellationToken cancellationToken = default)
        {
            PutCalled = true;
            Stored = attachment;
            return ValueTask.FromResult(AttachmentStoreResult.Stored);
        }

        public ValueTask CopyToAsync(
            AttachmentId attachmentId,
            Stream destination,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<bool> DeleteAsync(
            AttachmentId attachmentId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<int> DeleteExpiredAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }

    private sealed class DenyingAuthorizer : IAttachmentAccessAuthorizer
    {
        public ValueTask<bool> CanUploadAsync(
            UserId authenticatedUserId,
            ConversationId conversationId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<bool> CanDownloadAsync(
            UserId authenticatedUserId,
            StoredAttachment attachment,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<bool> CanDeleteAsync(
            UserId authenticatedUserId,
            StoredAttachment attachment,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class AllowingAuthorizer : IAttachmentAccessAuthorizer
    {
        public ValueTask<bool> CanUploadAsync(
            UserId authenticatedUserId,
            ConversationId conversationId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<bool> CanDownloadAsync(
            UserId authenticatedUserId,
            StoredAttachment attachment,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<bool> CanDeleteAsync(
            UserId authenticatedUserId,
            StoredAttachment attachment,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
