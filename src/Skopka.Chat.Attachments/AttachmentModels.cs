using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Attachments;

/// <summary>Identifies one opaque encrypted attachment blob.</summary>
public readonly record struct AttachmentId(Guid Value)
{
    /// <summary>Creates a new opaque attachment identifier.</summary>
    public static AttachmentId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Storage bounds shared by transports and storage adapters.</summary>
public static class AttachmentStorageLimits
{
    /// <summary>SHA-256 digest size.</summary>
    public const int Sha256Bytes = 32;

    /// <summary>Largest ciphertext accepted by the common contract (5 GiB).</summary>
    public const long MaxCiphertextBytes = 5L * 1024 * 1024 * 1024;
}

/// <summary>Opaque metadata visible to the attachment service and storage provider.</summary>
public sealed class StoredAttachment
{
    private readonly byte[] _ciphertextSha256;

    /// <summary>Creates validated storage metadata. No plaintext file metadata belongs here.</summary>
    public StoredAttachment(
        AttachmentId attachmentId,
        ConversationId conversationId,
        UserId uploaderUserId,
        long ciphertextLength,
        ReadOnlySpan<byte> ciphertextSha256,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null)
    {
        AttachmentValidation.RequireId(attachmentId, nameof(attachmentId));
        AttachmentValidation.RequireId(conversationId, nameof(conversationId));
        AttachmentValidation.RequireId(uploaderUserId, nameof(uploaderUserId));
        AttachmentValidation.RequireLength(ciphertextLength, nameof(ciphertextLength));
        if (ciphertextSha256.Length != AttachmentStorageLimits.Sha256Bytes)
        {
            throw new ArgumentException("Ciphertext hash has an invalid size.", nameof(ciphertextSha256));
        }

        if (expiresAt.HasValue && expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be later than creation time.");
        }

        AttachmentId = attachmentId;
        ConversationId = conversationId;
        UploaderUserId = uploaderUserId;
        CiphertextLength = ciphertextLength;
        _ciphertextSha256 = ciphertextSha256.ToArray();
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Opaque blob identifier.</summary>
    public AttachmentId AttachmentId { get; }

    /// <summary>Conversation used for authorization, not cryptographic trust.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Authenticated user who stored the blob.</summary>
    public UserId UploaderUserId { get; }

    /// <summary>Exact encrypted blob length.</summary>
    public long CiphertextLength { get; }

    /// <summary>SHA-256 over the exact encrypted blob.</summary>
    public ReadOnlyMemory<byte> CiphertextSha256 => _ciphertextSha256;

    /// <summary>Server-side creation time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Optional server-side retention deadline.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"StoredAttachment(AttachmentId={AttachmentId}, ConversationId={ConversationId}, CiphertextLength={CiphertextLength})";

    internal bool HasSameIdentity(StoredAttachment other) =>
        AttachmentId == other.AttachmentId &&
        ConversationId == other.ConversationId &&
        UploaderUserId == other.UploaderUserId &&
        CiphertextLength == other.CiphertextLength &&
        CreatedAt == other.CreatedAt &&
        ExpiresAt == other.ExpiresAt &&
        CryptographicOperations.FixedTimeEquals(_ciphertextSha256, other._ciphertextSha256);
}

/// <summary>Authenticated upload request excluding server-owned fields.</summary>
public sealed class AttachmentUploadRequest
{
    private readonly byte[] _ciphertextSha256;

    /// <summary>Creates a bounded upload request.</summary>
    public AttachmentUploadRequest(
        AttachmentId attachmentId,
        ConversationId conversationId,
        long ciphertextLength,
        ReadOnlySpan<byte> ciphertextSha256,
        DateTimeOffset? expiresAt = null)
    {
        AttachmentValidation.RequireId(attachmentId, nameof(attachmentId));
        AttachmentValidation.RequireId(conversationId, nameof(conversationId));
        AttachmentValidation.RequireLength(ciphertextLength, nameof(ciphertextLength));
        if (ciphertextSha256.Length != AttachmentStorageLimits.Sha256Bytes)
        {
            throw new ArgumentException("Ciphertext hash has an invalid size.", nameof(ciphertextSha256));
        }

        AttachmentId = attachmentId;
        ConversationId = conversationId;
        CiphertextLength = ciphertextLength;
        _ciphertextSha256 = ciphertextSha256.ToArray();
        ExpiresAt = expiresAt;
    }

    /// <summary>Requested opaque identifier.</summary>
    public AttachmentId AttachmentId { get; }

    /// <summary>Conversation to authorize.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Exact encrypted request-body length.</summary>
    public long CiphertextLength { get; }

    /// <summary>Expected SHA-256 over the encrypted request body.</summary>
    public ReadOnlyMemory<byte> CiphertextSha256 => _ciphertextSha256;

    /// <summary>Optional retention deadline.</summary>
    public DateTimeOffset? ExpiresAt { get; }
}

/// <summary>Atomic create outcome for an encrypted attachment.</summary>
public enum AttachmentStoreResult
{
    /// <summary>A new blob was stored.</summary>
    Stored = 1,

    /// <summary>The same immutable blob was already stored.</summary>
    Duplicate = 2,

    /// <summary>The identifier already belongs to different metadata or ciphertext.</summary>
    Conflict = 3,
}

/// <summary>Safe attachment-service rejection without attacker-controlled detail.</summary>
public sealed class AttachmentServiceException : InvalidOperationException
{
    /// <summary>Creates a bounded service error.</summary>
    public AttachmentServiceException(string message) : base(message)
    {
    }
}

internal static class AttachmentValidation
{
    internal static void RequireId(AttachmentId value, string parameterName)
    {
        if (value.Value == Guid.Empty)
        {
            throw new ArgumentException("Attachment ID must not be empty.", parameterName);
        }
    }

    internal static void RequireId(ConversationId value, string parameterName)
    {
        if (value.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", parameterName);
        }
    }

    internal static void RequireId(UserId value, string parameterName)
    {
        if (value.Value == Guid.Empty)
        {
            throw new ArgumentException("User ID must not be empty.", parameterName);
        }
    }

    internal static void RequireLength(long value, string parameterName)
    {
        if (value <= 0 || value > AttachmentStorageLimits.MaxCiphertextBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Ciphertext length is outside the supported range.");
        }
    }
}
