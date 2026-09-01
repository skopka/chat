using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Attachments.PostgreSql;

/// <summary>Bounded PostgreSQL <c>bytea</c> implementation for small encrypted attachments.</summary>
public sealed class PostgreSqlAttachmentStore : IAttachmentStore
{
    private readonly AttachmentDbContext _context;
    private readonly int _maxCiphertextBytes;

    /// <summary>Creates a scoped store over an isolated attachment context.</summary>
    public PostgreSqlAttachmentStore(
        AttachmentDbContext context,
        PostgreSqlAttachmentStoreOptions? options = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _maxCiphertextBytes = (options ?? new PostgreSqlAttachmentStoreOptions()).MaxCiphertextBytes;
    }

    /// <inheritdoc />
    public async ValueTask<StoredAttachment?> GetMetadataAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        RequireId(attachmentId);
        var item = await _context.Attachments.AsNoTracking()
            .Where(entity => entity.AttachmentId == attachmentId.Value)
            .Select(entity => new
            {
                entity.AttachmentId,
                entity.ConversationId,
                entity.UploaderUserId,
                entity.CiphertextLength,
                entity.CiphertextSha256,
                entity.CreatedAt,
                entity.ExpiresAt
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return item is null
            ? null
            : new StoredAttachment(
                new AttachmentId(item.AttachmentId),
                new ConversationId(item.ConversationId),
                new UserId(item.UploaderUserId),
                item.CiphertextLength,
                item.CiphertextSha256,
                item.CreatedAt,
                item.ExpiresAt);
    }

    /// <inheritdoc />
    public async ValueTask<AttachmentStoreResult> TryPutAsync(
        StoredAttachment attachment,
        Stream ciphertext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!ciphertext.CanRead)
        {
            throw new ArgumentException("Ciphertext stream must be readable.", nameof(ciphertext));
        }

        if (attachment.CiphertextLength > _maxCiphertextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(attachment), "Ciphertext exceeds the PostgreSQL store limit.");
        }

        var bytes = new byte[checked((int)attachment.CiphertextLength)];
        await ReadExactlyAsync(ciphertext, bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await ciphertext.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), attachment.CiphertextSha256.Span))
        {
            throw new InvalidDataException("Encrypted attachment does not match its declared metadata.");
        }

        var existing = await GetMetadataAsync(attachment.AttachmentId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return IsSameImmutableBlob(existing, attachment)
                ? AttachmentStoreResult.Duplicate
                : AttachmentStoreResult.Conflict;
        }

        _context.Attachments.Add(new EncryptedAttachmentEntity
        {
            AttachmentId = attachment.AttachmentId.Value,
            ConversationId = attachment.ConversationId.Value,
            UploaderUserId = attachment.UploaderUserId.Value,
            CiphertextLength = attachment.CiphertextLength,
            CiphertextSha256 = attachment.CiphertextSha256.ToArray(),
            Ciphertext = bytes,
            CreatedAt = attachment.CreatedAt,
            ExpiresAt = attachment.ExpiresAt
        });
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _context.ChangeTracker.Clear();
            return AttachmentStoreResult.Stored;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _context.ChangeTracker.Clear();
            existing = await GetMetadataAsync(attachment.AttachmentId, cancellationToken).ConfigureAwait(false);
            return existing is not null && IsSameImmutableBlob(existing, attachment)
                ? AttachmentStoreResult.Duplicate
                : AttachmentStoreResult.Conflict;
        }
    }

    /// <inheritdoc />
    public async ValueTask CopyToAsync(
        AttachmentId attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        RequireId(attachmentId);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        var bytes = await _context.Attachments.AsNoTracking()
            .Where(item => item.AttachmentId == attachmentId.Value)
            .Select(item => item.Ciphertext)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Encrypted attachment is unavailable.");
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        RequireId(attachmentId);
        var affected = await _context.Attachments
            .Where(item => item.AttachmentId == attachmentId.Value)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await _context.Attachments
            .Where(item => item.ExpiresAt != null && item.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

    private static bool IsSameImmutableBlob(StoredAttachment left, StoredAttachment right) =>
        left.AttachmentId == right.AttachmentId &&
        left.ConversationId == right.ConversationId &&
        left.UploaderUserId == right.UploaderUserId &&
        left.CiphertextLength == right.CiphertextLength &&
        left.ExpiresAt == right.ExpiresAt &&
        CryptographicOperations.FixedTimeEquals(left.CiphertextSha256.Span, right.CiphertextSha256.Span);

    private static void RequireId(AttachmentId attachmentId)
    {
        if (attachmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Attachment ID must not be empty.", nameof(attachmentId));
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var current = await source.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (current == 0)
            {
                throw new EndOfStreamException("Encrypted attachment ended before its declared length.");
            }

            read += current;
        }
    }
}
