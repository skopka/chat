using System.Buffers.Binary;
using System.Text;

namespace Skopka.Chat.Protocol;

/// <summary>Hard limits for the separate encrypted-history backup v1 format.</summary>
public static class ChatBackupLimits
{
    /// <summary>Largest encoded event part, including framing and AEAD overhead.</summary>
    public const int MaxPartBytes = 66_000;
    /// <summary>Largest control record.</summary>
    public const int MaxControlBytes = 1_024;
    /// <summary>Maximum parts in one contribution.</summary>
    public const int MaxParts = 100_000;
    /// <summary>Maximum retained contribution depth.</summary>
    public const int MaxVersions = 4_096;
    /// <summary>Maximum local or remote record page.</summary>
    public const int MaxPageSize = 100;
}

/// <summary>A generic, content-free format failure.</summary>
public sealed class ChatBackupFormatException : FormatException
{
    /// <summary>Creates a bounded format failure without a parser cause.</summary>
    public ChatBackupFormatException() : base("Encrypted history backup is invalid or unsupported.") { }
}

/// <summary>Exact service and account scope, resolved independently from trusted authentication.</summary>
public sealed record ChatBackupScope
{
    /// <summary>Creates an account scope. Never derive the service from a request Host header.</summary>
    public ChatBackupScope(string serviceId, UserId userId)
    {
        DeviceBindingEncoding.ValidateReference(serviceId);
        if (userId.Value == Guid.Empty) { throw new ArgumentException("Backup account is invalid."); }
        ServiceId = serviceId; UserId = userId;
    }
    /// <summary>Configuration-owned exact service ID.</summary>
    public string ServiceId { get; }
    /// <summary>Host-authenticated account.</summary>
    public UserId UserId { get; }
    /// <inheritdoc />
    public override string ToString() => "ChatBackupScope([REDACTED])";
}

/// <summary>Immutable backup identity. A key generation is distinct from a device or local vault.</summary>
public sealed record ChatBackupArchive
{
    /// <summary>Creates backup-v1 identity; unknown versions are rejected.</summary>
    public ChatBackupArchive(ChatBackupScope scope, Guid archiveId, Guid keyGeneration, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (version != 1 || archiveId == Guid.Empty || keyGeneration == Guid.Empty) { throw new ChatBackupFormatException(); }
        Scope = scope; ArchiveId = archiveId; KeyGeneration = keyGeneration; Version = version;
    }
    /// <summary>Authenticated service/account namespace.</summary>
    public ChatBackupScope Scope { get; }
    /// <summary>Random stable archive ID.</summary>
    public Guid ArchiveId { get; }
    /// <summary>Random key-generation identifier; v1 has no rotation operation.</summary>
    public Guid KeyGeneration { get; }
    /// <summary>Separate backup format version, not the envelope version.</summary>
    public int Version { get; }
}

/// <summary>One opaque, immutable encrypted event in an ordered contribution.</summary>
public sealed class ChatBackupPart
{
    /// <summary>Creates a bounded part. Buffers are copied.</summary>
    public ChatBackupPart(Guid uploadId, int index, ReadOnlySpan<byte> previousHash, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext)
    {
        if (uploadId == Guid.Empty || index is < 0 or >= ChatBackupLimits.MaxParts || previousHash.Length != 32 ||
            nonce.Length != 24 || ciphertext.Length is < 16 or > ChatBackupLimits.MaxPartBytes - 128 ||
            (index == 0 && previousHash.ContainsAnyExcept((byte)0))) { throw new ChatBackupFormatException(); }
        UploadId = uploadId; Index = index; PreviousHash = previousHash.ToArray(); Nonce = nonce.ToArray(); Ciphertext = ciphertext.ToArray();
    }
    /// <summary>Random contribution ID, also its completed version ID.</summary>
    public Guid UploadId { get; }
    /// <summary>Zero-based position; a version must contain every preceding index.</summary>
    public int Index { get; }
    /// <summary>SHA256 of the exact preceding encoded part, or 32 zero bytes.</summary>
    public ReadOnlyMemory<byte> PreviousHash { get; }
    /// <summary>Fresh XChaCha20 nonce.</summary>
    public ReadOnlyMemory<byte> Nonce { get; }
    /// <summary>Ciphertext with appended Poly1305 tag.</summary>
    public ReadOnlyMemory<byte> Ciphertext { get; }
    /// <inheritdoc />
    public override string ToString() => "ChatBackupPart(Ciphertext=[REDACTED])";
}

/// <summary>Authenticated seal of one complete contribution and its immutable ancestor.</summary>
public sealed class ChatBackupVersion
{
    /// <summary>Creates a strictly bounded v1 seal. Server commit must additionally prove completeness and head CAS.</summary>
    public ChatBackupVersion(ChatBackupArchive archive, Guid versionId, Guid? parentId, ReadOnlySpan<byte> parentHash,
        int partCount, long totalBytes, ReadOnlySpan<byte> finalHash, DateTimeOffset createdAt,
        ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (versionId == Guid.Empty || parentId == Guid.Empty || parentId == versionId || parentHash.Length != 32 ||
            finalHash.Length != 32 || partCount is < 0 or > ChatBackupLimits.MaxParts || totalBytes < 0 ||
            totalBytes > (long)partCount * ChatBackupLimits.MaxPartBytes || createdAt == default || nonce.Length != 24 || tag.Length != 16 ||
            (parentId is null && parentHash.ContainsAnyExcept((byte)0)) ||
            (partCount == 0 && (totalBytes != 0 || finalHash.ContainsAnyExcept((byte)0)))) { throw new ChatBackupFormatException(); }
        Archive = archive; VersionId = versionId; ParentId = parentId; ParentHash = parentHash.ToArray();
        PartCount = partCount; TotalBytes = totalBytes; FinalHash = finalHash.ToArray(); CreatedAt = createdAt.ToUniversalTime();
        Nonce = nonce.ToArray(); Tag = tag.ToArray();
    }
    /// <summary>Context authenticated by the seal.</summary>
    public ChatBackupArchive Archive { get; }
    /// <summary>Contribution/upload ID.</summary>
    public Guid VersionId { get; }
    /// <summary>Previous completed version, never an incomplete upload.</summary>
    public Guid? ParentId { get; }
    /// <summary>SHA256 of the complete parent seal, or zero for a root.</summary>
    public ReadOnlyMemory<byte> ParentHash { get; }
    /// <summary>Exact contiguous part count.</summary>
    public int PartCount { get; }
    /// <summary>Exact sum of encoded part lengths.</summary>
    public long TotalBytes { get; }
    /// <summary>SHA256 of the last encoded part, authenticating its complete chain.</summary>
    public ReadOnlyMemory<byte> FinalHash { get; }
    /// <summary>Authenticated client-supplied completion timestamp; not trusted clock evidence.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>Fresh nonce for the empty-plaintext seal AEAD.</summary>
    public ReadOnlyMemory<byte> Nonce { get; }
    /// <summary>Empty-plaintext authentication tag.</summary>
    public ReadOnlyMemory<byte> Tag { get; }
}

/// <summary>Atomic completion outcome. Conflict requires authenticating and rebasing on the new head.</summary>
public enum ChatBackupCommitResult
{
    /// <summary>All parts committed and head advanced atomically.</summary>
    Committed = 1,
    /// <summary>Exact already committed seal; never extends upload expiry.</summary>
    Duplicate = 2,
    /// <summary>Head moved or immutable ID has different data.</summary>
    Conflict = 3,
}

/// <summary>Bounded backup failure categories, safe to expose without provider details.</summary>
public enum ChatBackupFailure
{
    /// <summary>Key or encrypted archive authentication failed.</summary>
    Authentication = 1,
    /// <summary>Archive does not match the independently expected account/service.</summary>
    Scope = 2,
    /// <summary>Concurrent change, immutable-ID reuse or unexpected existing archive.</summary>
    Conflict = 3,
    /// <summary>Recovery key has not been explicitly saved and confirmed.</summary>
    ConfirmationRequired = 4,
    /// <summary>Session is locked/disposed.</summary>
    Locked = 5,
    /// <summary>Protected local storage is unavailable or corrupt.</summary>
    LocalStorage = 6,
    /// <summary>Remote operation is unavailable.</summary>
    Unavailable = 7,
    /// <summary>Configured size/count/retention quota was reached.</summary>
    Quota = 8,
    /// <summary>Required part/version is absent or inconsistent.</summary>
    Incomplete = 9,
    /// <summary>No completed backup exists.</summary>
    NotFound = 10,
    /// <summary>A retained trusted local head is not an ancestor of the remote head.</summary>
    Rollback = 11,
}

/// <summary>Generic failure with no remote/provider/parser exception attached.</summary>
public sealed class ChatBackupException : Exception
{
    /// <summary>Creates a fixed diagnostic; never accepts message content.</summary>
    public ChatBackupException(ChatBackupFailure failure) : base("History backup operation failed.") => Failure = failure;
    /// <summary>Bounded error category.</summary>
    public ChatBackupFailure Failure { get; }
}

/// <summary>Canonical bounded binary backup-v1 records and AEAD metadata. Never signs JSON.</summary>
public static class ChatBackupEncoding
{
    private static ReadOnlySpan<byte> Domain => "Skopka.Chat.Backup\0\x01"u8;
    /// <summary>Encodes immutable account/archive/key-generation identity.</summary>
    public static byte[] EncodeArchive(ChatBackupArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using var output = Start((byte)'A'); WriteArchive(output, archive); return output.ToArray();
    }
    /// <summary>Strictly decodes archive identity; callers compare it with independently expected context.</summary>
    public static ChatBackupArchive DecodeArchive(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes, (byte)'A', ChatBackupLimits.MaxControlBytes);
        var archive = reader.Archive(); reader.End(); return archive;
    }
    /// <summary>Encodes exactly the bytes hashed, stored and retried for a part.</summary>
    public static byte[] EncodePart(ChatBackupPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        using var output = Start((byte)'P'); WriteGuid(output, part.UploadId); WriteInt(output, part.Index);
        output.Write(part.PreviousHash.Span); output.Write(part.Nonce.Span); WriteInt(output, part.Ciphertext.Length); output.Write(part.Ciphertext.Span);
        return output.ToArray();
    }
    /// <summary>Strictly decodes a bounded opaque part.</summary>
    public static ChatBackupPart DecodePart(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes, (byte)'P', ChatBackupLimits.MaxPartBytes);
        var result = new ChatBackupPart(reader.Guid(), reader.Int(), reader.Bytes(32), reader.Bytes(24), reader.Blob(ChatBackupLimits.MaxPartBytes - 128));
        reader.End(); return result;
    }
    /// <summary>Part AEAD source of truth, binding context, purpose, position and the prior ciphertext hash.</summary>
    public static byte[] PartAssociatedData(ChatBackupArchive archive, Guid uploadId, int index, ReadOnlySpan<byte> previousHash)
    {
        if (uploadId == Guid.Empty || index is < 0 or >= ChatBackupLimits.MaxParts || previousHash.Length != 32) { throw new ChatBackupFormatException(); }
        using var output = Start((byte)'D'); WriteArchive(output, archive); WriteGuid(output, uploadId); WriteInt(output, index); output.Write(previousHash);
        return output.ToArray();
    }
    /// <summary>Seal AEAD source of truth, including exact predecessor and completion metadata.</summary>
    public static byte[] VersionAssociatedData(ChatBackupVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        using var output = Start((byte)'S'); WriteArchive(output, version.Archive); WriteGuid(output, version.VersionId);
        WriteGuid(output, version.ParentId ?? Guid.Empty); output.Write(version.ParentHash.Span); WriteInt(output, version.PartCount);
        WriteLong(output, version.TotalBytes); output.Write(version.FinalHash.Span); WriteLong(output, version.CreatedAt.UtcTicks);
        return output.ToArray();
    }
    /// <summary>Encodes an authenticated seal, including nonce and tag.</summary>
    public static byte[] EncodeVersion(ChatBackupVersion version)
    {
        using var output = new MemoryStream(); output.Write(VersionAssociatedData(version)); output.Write(version.Nonce.Span); output.Write(version.Tag.Span);
        return output.ToArray();
    }
    /// <summary>Strictly decodes a seal. Structural validity is not cryptographic authenticity.</summary>
    public static ChatBackupVersion DecodeVersion(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes, (byte)'S', ChatBackupLimits.MaxControlBytes);
        var archive = reader.Archive(); var id = reader.Guid(); var parent = reader.Guid(); var hash = reader.Bytes(32);
        var count = reader.Int(); var size = reader.Long(); var last = reader.Bytes(32); var ticks = reader.Long();
        DateTimeOffset time;
        try { time = new DateTimeOffset(ticks, TimeSpan.Zero); } catch (ArgumentOutOfRangeException) { throw new ChatBackupFormatException(); }
        var result = new ChatBackupVersion(archive, id, parent == Guid.Empty ? null : parent, hash, count, size, last, time, reader.Bytes(24), reader.Bytes(16));
        reader.End(); return result;
    }
    private static MemoryStream Start(byte purpose) { var result = new MemoryStream(); result.Write(Domain); result.WriteByte(purpose); return result; }
    private static void WriteArchive(Stream output, ChatBackupArchive archive)
    {
        var service = Encoding.UTF8.GetBytes(archive.Scope.ServiceId); WriteInt(output, service.Length); output.Write(service);
        WriteGuid(output, archive.Scope.UserId.Value); WriteGuid(output, archive.ArchiveId); WriteGuid(output, archive.KeyGeneration);
    }
    private static void WriteGuid(Stream output, Guid value) { Span<byte> bytes = stackalloc byte[16]; value.TryWriteBytes(bytes, bigEndian: true, out _); output.Write(bytes); }
    private static void WriteInt(Stream output, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); output.Write(bytes); }
    private static void WriteLong(Stream output, long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); output.Write(bytes); }
    private ref struct Reader
    {
        private ReadOnlySpan<byte> _bytes;
        internal Reader(ReadOnlySpan<byte> bytes, byte purpose, int maximum)
        {
            if (bytes.Length > maximum || bytes.Length <= Domain.Length || !bytes.StartsWith(Domain) || bytes[Domain.Length] != purpose) { throw new ChatBackupFormatException(); }
            _bytes = bytes[(Domain.Length + 1)..];
        }
        internal ReadOnlySpan<byte> Bytes(int length) { if (length < 0 || length > _bytes.Length) { throw new ChatBackupFormatException(); } var value = _bytes[..length]; _bytes = _bytes[length..]; return value; }
        internal int Int() => BinaryPrimitives.ReadInt32BigEndian(Bytes(4));
        internal long Long() => BinaryPrimitives.ReadInt64BigEndian(Bytes(8));
        internal Guid Guid() => new(Bytes(16), bigEndian: true);
        internal ReadOnlySpan<byte> Blob(int maximum) { var length = Int(); if (length > maximum) { throw new ChatBackupFormatException(); } return Bytes(length); }
        internal ChatBackupArchive Archive()
        {
            try { var service = new UTF8Encoding(false, true).GetString(Blob(256)); return new(new ChatBackupScope(service, new UserId(Guid())), Guid(), Guid()); }
            catch (ArgumentException) { throw new ChatBackupFormatException(); }
        }
        internal void End() { if (!_bytes.IsEmpty) { throw new ChatBackupFormatException(); } }
    }
}
