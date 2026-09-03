using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Random backup secret. Not an account password, vault phrase, or device key.</summary>
public sealed class ChatBackupRecoveryKey : IDisposable
{
    private readonly byte[] _secret;
    private bool _disposed;
    private ChatBackupRecoveryKey(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32) { throw new ChatBackupFormatException(); }
        _secret = secret.ToArray();
    }
    /// <summary>Creates a new key only for explicitly enabling a new archive.</summary>
    public static ChatBackupRecoveryKey Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { return new(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    /// <summary>Loads an explicitly supplied protected 32-byte secret; callers must clear their buffer.</summary>
    public static ChatBackupRecoveryKey FromBytes(ReadOnlySpan<byte> bytes) => new(bytes);
    /// <summary>Explicit secret export for a protected key store; clear the returned buffer.</summary>
    public byte[] ExportBytes() { ObjectDisposedException.ThrowIf(_disposed, this); return _secret.ToArray(); }
    /// <summary>Explicit user-facing SCB1 key export. Never log or send this string to the server.</summary>
    public string ExportRecoveryCode()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hex = Convert.ToHexString(_secret);
        return "SCB1-" + string.Join('-', Enumerable.Range(0, 8).Select(index => hex.Substring(index * 8, 8))) + "-" + Checksum(_secret);
    }
    /// <summary>Parses grouped hex with checksum. ASCII spaces/hyphens and hex letter case are ignored.</summary>
    public static ChatBackupRecoveryKey Parse(string code)
    {
        if (code is null || code.Length > 160) { throw new ChatBackupFormatException(); }
        var compact = new string(code.Where(character => character is not ('-' or ' ')).ToArray());
        if (compact.Length != 76 || !compact.StartsWith("SCB1", StringComparison.OrdinalIgnoreCase)) { throw new ChatBackupFormatException(); }
        byte[] bytes;
        try { bytes = Convert.FromHexString(compact.AsSpan(4, 64)); } catch (FormatException) { throw new ChatBackupFormatException(); }
        try
        {
            if (!string.Equals(Checksum(bytes), compact[68..], StringComparison.OrdinalIgnoreCase)) { throw new ChatBackupFormatException(); }
            return new(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal byte[] Derive(ChatBackupArchive archive, byte purpose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = ChatBackupEncoding.EncodeArchive(archive);
        var info = new byte[context.Length + 1]; context.CopyTo(info, 0); info[^1] = purpose;
        var result = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, result, "Skopka.Chat.Backup.Kdf.v1"u8, info);
        return result;
    }
    private static string Checksum(ReadOnlySpan<byte> secret)
    {
        Span<byte> input = stackalloc byte[64]; input.Clear(); "Skopka.Chat.Backup.Key.v1"u8.CopyTo(input); secret.CopyTo(input[32..]);
        Span<byte> hash = stackalloc byte[32]; SHA256.HashData(input, hash); CryptographicOperations.ZeroMemory(input);
        return Convert.ToHexString(hash[..4]);
    }
    /// <summary>Clears the controlled raw key buffer; exported immutable strings cannot be erased.</summary>
    public void Dispose() { if (!_disposed) { _disposed = true; CryptographicOperations.ZeroMemory(_secret); } }
    /// <inheritdoc />
    public override string ToString() => "ChatBackupRecoveryKey([REDACTED])";
}

/// <summary>Historical assertion authenticated by a recovery key, not independently by the original sender.</summary>
public sealed class RestoredChatContent
{
    /// <summary>Creates an explicit archive-provenance event. Never pass it to live delivery/ACK handlers.</summary>
    public RestoredChatContent(ConversationId conversationId, UserId senderUserId, DeviceId senderDeviceId, DateTimeOffset sentAt, ChatContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (conversationId.Value == Guid.Empty || senderUserId.Value == Guid.Empty || senderDeviceId.Value == Guid.Empty || sentAt == default) { throw new ChatBackupFormatException(); }
        ConversationId = conversationId; SenderUserId = senderUserId; SenderDeviceId = senderDeviceId; SentAt = sentAt.ToUniversalTime(); Content = content;
    }
    /// <summary>Archived conversation identity, not a server admission grant.</summary>
    public ConversationId ConversationId { get; }
    /// <summary>Archived sender assertion; the archive does not retain sender-signature evidence.</summary>
    public UserId SenderUserId { get; }
    /// <summary>Historical sender device assertion, never a device to clone or enroll.</summary>
    public DeviceId SenderDeviceId { get; }
    /// <summary>Archived sender time, not trusted wall-clock evidence.</summary>
    public DateTimeOffset SentAt { get; }
    /// <summary>Original strict canonical content, preserving its logical identifier and references.</summary>
    public ChatContent Content { get; }
    internal ReceivedChatContent ProjectionOnly() => new(new MessageId(Content.ContentId.Value), ConversationId, SenderUserId, SenderDeviceId, SentAt, Content);
    /// <inheritdoc />
    public override string ToString() => "RestoredChatContent(Trust=RecoveryKey, Content=[REDACTED])";
}

/// <summary>Independent backup event v1 encoding. Delivery IDs are deliberately not imported.</summary>
public static class ChatBackupEventEncoding
{
    private static ReadOnlySpan<byte> Domain => "Skopka.Chat.Backup.Event\0\x01"u8;
    private static int HeaderBytes => Domain.Length + 60;
    /// <summary>Exports a verified journal event without its recipient-specific envelope ID.</summary>
    public static byte[] Encode(ReceivedChatContent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encode(new RestoredChatContent(value.ConversationId, value.SenderUserId, value.SenderDeviceId, value.SentAt, value.Content));
    }
    /// <summary>Encodes original historical event metadata and unchanged content bytes.</summary>
    public static byte[] Encode(RestoredChatContent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var content = ChatContentEncoding.Encode(value.Content);
        try
        {
            var bytes = new byte[HeaderBytes + content.Length]; Domain.CopyTo(bytes); var span = bytes.AsSpan(Domain.Length);
            value.ConversationId.Value.TryWriteBytes(span[..16], true, out _); value.SenderUserId.Value.TryWriteBytes(span.Slice(16, 16), true, out _);
            value.SenderDeviceId.Value.TryWriteBytes(span.Slice(32, 16), true, out _); BinaryPrimitives.WriteInt64BigEndian(span[48..], value.SentAt.UtcTicks);
            BinaryPrimitives.WriteInt32BigEndian(span[56..], content.Length); content.CopyTo(span[60..]); return bytes;
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }
    /// <summary>Strictly decodes an archive assertion, never a verified live delivery.</summary>
    public static RestoredChatContent Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes.Length > HeaderBytes + ProtocolLimits.MaxPlaintextBytes || !bytes.StartsWith(Domain)) { throw new ChatBackupFormatException(); }
        var span = bytes[Domain.Length..]; var length = BinaryPrimitives.ReadInt32BigEndian(span[56..]);
        if (length < 1 || length != span.Length - 60) { throw new ChatBackupFormatException(); }
        try
        {
            return new(new ConversationId(new Guid(span[..16], true)), new UserId(new Guid(span.Slice(16, 16), true)),
                new DeviceId(new Guid(span.Slice(32, 16), true)), new DateTimeOffset(BinaryPrimitives.ReadInt64BigEndian(span[48..]), TimeSpan.Zero), ChatContentEncoding.Decode(span[60..]));
        }
        catch (Exception error) when (error is ArgumentException or FormatException) { throw new ChatBackupFormatException(); }
    }
}

/// <summary>Backup-v1 composition over the existing native/browser primitive provider.</summary>
public sealed class ChatBackupCryptography
{
    private readonly IChatCryptographyProvider _provider;
    /// <summary>Creates a native-default or explicitly browser-provided archive cipher.</summary>
    public ChatBackupCryptography(IChatCryptographyProvider? provider = null) => _provider = provider ?? ChatCryptographyDefaults.Create();
    /// <summary>Encrypts one canonical event with a fresh nonce and purpose/context-derived key.</summary>
    public ChatBackupPart Encrypt(ChatBackupRecoveryKey recoveryKey, ChatBackupArchive archive, Guid uploadId, int index, ReadOnlySpan<byte> previousHash, ReadOnlySpan<byte> encodedEvent)
    {
        ArgumentNullException.ThrowIfNull(recoveryKey); _ = ChatBackupEventEncoding.Decode(encodedEvent);
        var key = recoveryKey.Derive(archive, (byte)'P'); var nonce = RandomNumberGenerator.GetBytes(24);
        try { return new(uploadId, index, previousHash, nonce, _provider.Encrypt(key, nonce, ChatBackupEncoding.PartAssociatedData(archive, uploadId, index, previousHash), encodedEvent)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    /// <summary>Authenticates/decrypts a part into a bounded temporary buffer; clear it after staging.</summary>
    public byte[] Decrypt(ChatBackupRecoveryKey recoveryKey, ChatBackupArchive archive, ChatBackupPart part)
    {
        var key = recoveryKey.Derive(archive, (byte)'P');
        try
        {
            return _provider.Decrypt(key, part.Nonce.Span, ChatBackupEncoding.PartAssociatedData(archive, part.UploadId, part.Index, part.PreviousHash.Span), part.Ciphertext.Span)
                ?? throw new ChatBackupException(ChatBackupFailure.Authentication);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    /// <summary>Authenticates a complete contribution and the exact previous completed version.</summary>
    public ChatBackupVersion Seal(ChatBackupRecoveryKey recoveryKey, ChatBackupArchive archive, Guid id, ChatBackupVersion? parent,
        int count, long bytes, ReadOnlySpan<byte> finalHash, DateTimeOffset createdAt)
    {
        if (parent is not null && parent.Archive != archive) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        var version = new ChatBackupVersion(archive, id, parent?.VersionId, parent is null ? new byte[32] : SHA256.HashData(ChatBackupEncoding.EncodeVersion(parent)),
            count, bytes, finalHash, createdAt, RandomNumberGenerator.GetBytes(24), new byte[16]);
        var key = recoveryKey.Derive(archive, (byte)'S');
        try
        {
            var tag = _provider.Encrypt(key, version.Nonce.Span, ChatBackupEncoding.VersionAssociatedData(version), []);
            return new(archive, id, version.ParentId, version.ParentHash.Span, count, bytes, finalHash, createdAt, version.Nonce.Span, tag);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    /// <summary>Authenticates the seal using independently expected scope/archive/key generation.</summary>
    public void Verify(ChatBackupRecoveryKey recoveryKey, ChatBackupArchive expected, ChatBackupVersion version)
    {
        if (version.Archive != expected) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        var key = recoveryKey.Derive(expected, (byte)'S');
        try
        {
            var plaintext = _provider.Decrypt(key, version.Nonce.Span, ChatBackupEncoding.VersionAssociatedData(version), version.Tag.Span);
            if (plaintext is null || plaintext.Length != 0) { throw new ChatBackupException(ChatBackupFailure.Authentication); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

/// <summary>Account-authenticated, ciphertext-only backup transport. Its host owns Auth and service context.</summary>
public interface IChatBackupTransport
{
    /// <summary>Gets this authenticated account's archive, never another caller-supplied user ID.</summary>
    ValueTask<ChatBackupArchive?> GetArchiveAsync(CancellationToken cancellationToken = default);
    /// <summary>Create-only account archive registration; exact retries are allowed.</summary>
    ValueTask<bool> TryCreateArchiveAsync(ChatBackupArchive archive, CancellationToken cancellationToken = default);
    /// <summary>Gets the latest completed seal, excluding all incomplete uploads.</summary>
    ValueTask<ChatBackupVersion?> GetHeadAsync(Guid archiveId, CancellationToken cancellationToken = default);
    /// <summary>Starts or resumes one bounded immutable upload.</summary>
    ValueTask BeginUploadAsync(Guid archiveId, Guid uploadId, CancellationToken cancellationToken = default);
    /// <summary>Stores exact bytes by immutable upload/index; different bytes must be rejected.</summary>
    ValueTask PutPartAsync(Guid archiveId, ChatBackupPart part, CancellationToken cancellationToken = default);
    /// <summary>Atomically checks every part and compare-and-swaps the completed head.</summary>
    ValueTask<ChatBackupCommitResult> CommitAsync(ChatBackupVersion version, CancellationToken cancellationToken = default);
    /// <summary>Gets an immutable completed ancestor for this account.</summary>
    ValueTask<ChatBackupVersion?> GetVersionAsync(Guid archiveId, Guid versionId, CancellationToken cancellationToken = default);
    /// <summary>Gets a bounded encrypted part of this account's committed version or pending upload.</summary>
    ValueTask<ChatBackupPart?> GetPartAsync(Guid archiveId, Guid uploadId, int index, CancellationToken cancellationToken = default);
}
