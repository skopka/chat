using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Account-serialized transactional storage of opaque backup records. No plaintext/private-key or decryption API.</summary>
public interface IChatBackupStorage
{
    /// <summary>Acquires a transaction exclusively across all writers for this trusted scope. Dispose without Commit rolls back.</summary>
    ValueTask<IChatBackupTransaction> BeginAsync(ChatBackupScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Host storage primitive used only by the backup service. Implementations must enforce account isolation and bounded records/pages.</summary>
public interface IChatBackupTransaction : IAsyncDisposable
{
    /// <summary>Reads one opaque record (at most MaxPartBytes).</summary>
    ValueTask<byte[]?> ReadAsync(string group, string key, CancellationToken cancellationToken);
    /// <summary>Atomically replaces one record within this account transaction.</summary>
    ValueTask WriteAsync(string group, string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    /// <summary>Reads at most count keys in ordinal ascending order, strictly after a cursor.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync(string group, string? after, int count, CancellationToken cancellationToken);
    /// <summary>Deletes one unreferenced pending record.</summary>
    ValueTask DeleteAsync(string group, string key, CancellationToken cancellationToken);
    /// <summary>Durably commits all changes; cancellation must not abandon a possibly committing transaction.</summary>
    ValueTask CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Per-account quotas and pending retention. Committed ancestors are never pruned by TTL.</summary>
public sealed class ChatBackupServerOptions
{
    /// <summary>Maximum combined encoded part bytes, including concurrent pending uploads.</summary>
    public long MaximumBytes { get; init; } = 1L << 30;
    /// <summary>Maximum committed versions; reaching this limit stops writes instead of deleting dependencies.</summary>
    public int MaximumVersions { get; init; } = ChatBackupLimits.MaxVersions;
    /// <summary>Maximum simultaneous unfinished uploads per account.</summary>
    public int MaximumPendingUploads { get; init; } = 4;
    /// <summary>Maximum parts in an upload.</summary>
    public int MaximumParts { get; init; } = ChatBackupLimits.MaxParts;
    /// <summary>Fixed pending lifetime measured from first begin; retries do not extend it.</summary>
    public TimeSpan PendingLifetime { get; init; } = TimeSpan.FromDays(7);
    internal void Validate()
    {
        if (MaximumBytes is < ChatBackupLimits.MaxPartBytes or > 64L << 30 || MaximumVersions is < 1 or > ChatBackupLimits.MaxVersions ||
            MaximumPendingUploads is < 1 or > 32 || MaximumParts is < 1 or > ChatBackupLimits.MaxParts ||
            PendingLifetime < TimeSpan.FromMinutes(1) || PendingLifetime > TimeSpan.FromDays(30)) { throw new ArgumentException("Backup policy is invalid."); }
    }
}

/// <summary>Opt-in ciphertext-only immutable backup service with complete-head CAS and account-serialized quotas.</summary>
public sealed class ChatBackupService
{
    private readonly IChatBackupStorage _storage;
    private readonly TimeProvider _time;
    private readonly ChatBackupServerOptions _options;
    /// <summary>Creates a service. The caller must resolve scope from trusted host authentication, never request claims/IDs alone.</summary>
    public ChatBackupService(IChatBackupStorage storage, TimeProvider timeProvider, ChatBackupServerOptions? options = null)
    { _storage = storage ?? throw new ArgumentNullException(nameof(storage)); _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)); _options = options ?? new(); _options.Validate(); }
    /// <summary>Returns account archive identity, if explicitly created.</summary>
    public ValueTask<ChatBackupArchive?> GetArchiveAsync(ChatBackupScope scope, CancellationToken cancellationToken = default) => Run(scope, async (tx, ct) =>
    { var data = await tx.ReadAsync("control", "archive", ct).ConfigureAwait(false); return data is null ? null : ChatBackupEncoding.DecodeArchive(data); }, cancellationToken);
    /// <summary>Creates an absent archive only; never changes its key generation or identity.</summary>
    public ValueTask<bool> TryCreateArchiveAsync(ChatBackupScope scope, ChatBackupArchive archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive); if (archive.Scope != scope) { throw Failure(ChatBackupFailure.Scope); }
        return Run(scope, async (tx, ct) =>
        {
            if (await tx.ReadAsync("control", "archive", ct).ConfigureAwait(false) is not null) { return false; }
            await tx.WriteAsync("control", "archive", ChatBackupEncoding.EncodeArchive(archive), ct).ConfigureAwait(false); return true;
        }, cancellationToken);
    }
    /// <summary>Returns only a fully committed head.</summary>
    public ValueTask<ChatBackupVersion?> GetHeadAsync(ChatBackupScope scope, Guid archiveId, CancellationToken cancellationToken = default) => Run(scope, async (tx, ct) =>
    {
        await RequireArchive(tx, scope, archiveId, ct).ConfigureAwait(false);
        var bytes = await tx.ReadAsync("control", "head", ct).ConfigureAwait(false); return bytes is null ? null : ChatBackupEncoding.DecodeVersion(bytes);
    }, cancellationToken);
    /// <summary>Begins/resumes one upload without extending expiry. Quotas and cleanup are atomic with creation.</summary>
    public async ValueTask BeginUploadAsync(ChatBackupScope scope, Guid archiveId, Guid uploadId, CancellationToken cancellationToken = default)
    {
        Id(uploadId);
        await Run(scope, async (tx, ct) =>
        {
            await RequireArchive(tx, scope, archiveId, ct).ConfigureAwait(false);
            if (await tx.ReadAsync("versions", Key(uploadId), ct).ConfigureAwait(false) is not null) { return true; }
            await Cleanup(tx, ct).ConfigureAwait(false);
            if (await tx.ReadAsync("pending", Key(uploadId), ct).ConfigureAwait(false) is not null) { return true; }
            var pending = await tx.ListAsync("pending", null, _options.MaximumPendingUploads, ct).ConfigureAwait(false);
            if (pending.Count >= _options.MaximumPendingUploads || await Counter(tx, "versions", ct).ConfigureAwait(false) >= _options.MaximumVersions) { throw Failure(ChatBackupFailure.Quota); }
            var descriptor = new byte[20]; BinaryPrimitives.WriteInt64BigEndian(descriptor, (_time.GetUtcNow() + _options.PendingLifetime).UtcTicks);
            await tx.WriteAsync("pending", Key(uploadId), descriptor, ct).ConfigureAwait(false); return true;
        }, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Stores one immutable part; exact retries succeed, differing bytes fail before mutation.</summary>
    public async ValueTask PutPartAsync(ChatBackupScope scope, Guid archiveId, ChatBackupPart part, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part); var bytes = ChatBackupEncoding.EncodePart(part);
        await Run(scope, async (tx, ct) =>
        {
            await RequireArchive(tx, scope, archiveId, ct).ConfigureAwait(false);
            var group = Key(part.UploadId); var key = part.Index.ToString("D10", CultureInfo.InvariantCulture);
            var previous = await tx.ReadAsync(group, key, ct).ConfigureAwait(false);
            if (previous is not null) { if (!previous.AsSpan().SequenceEqual(bytes)) { throw Failure(ChatBackupFailure.Conflict); } return true; }
            var pending = await RequirePending(tx, part.UploadId, ct).ConfigureAwait(false);
            var count = BinaryPrimitives.ReadInt32BigEndian(pending.AsSpan(8)); var size = BinaryPrimitives.ReadInt64BigEndian(pending.AsSpan(12));
            var total = await Counter(tx, "bytes", ct).ConfigureAwait(false);
            if (part.Index >= _options.MaximumParts || count >= _options.MaximumParts || total + bytes.Length > _options.MaximumBytes) { throw Failure(ChatBackupFailure.Quota); }
            BinaryPrimitives.WriteInt32BigEndian(pending.AsSpan(8), count + 1); BinaryPrimitives.WriteInt64BigEndian(pending.AsSpan(12), size + bytes.Length);
            await tx.WriteAsync(group, key, bytes, ct).ConfigureAwait(false); await tx.WriteAsync("pending", group, pending, ct).ConfigureAwait(false);
            await SetCounter(tx, "bytes", total + bytes.Length, ct).ConfigureAwait(false); return true;
        }, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Validates exact part count, length, hash chain and current parent, then atomically publishes an immutable version.</summary>
    public ValueTask<ChatBackupCommitResult> CommitAsync(ChatBackupScope scope, ChatBackupVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version); if (version.Archive.Scope != scope) { throw Failure(ChatBackupFailure.Scope); }
        return Run(scope, async (tx, ct) =>
        {
            var archive = await RequireArchive(tx, scope, version.Archive.ArchiveId, ct).ConfigureAwait(false);
            if (archive != version.Archive) { throw Failure(ChatBackupFailure.Scope); }
            var encoded = ChatBackupEncoding.EncodeVersion(version); var id = Key(version.VersionId);
            var existing = await tx.ReadAsync("versions", id, ct).ConfigureAwait(false);
            if (existing is not null) { if (!existing.AsSpan().SequenceEqual(encoded)) { throw Failure(ChatBackupFailure.Conflict); } return ChatBackupCommitResult.Duplicate; }
            var pending = await RequirePending(tx, version.VersionId, ct).ConfigureAwait(false);
            var head = await tx.ReadAsync("control", "head", ct).ConfigureAwait(false);
            if (head is null ? version.ParentId is not null : version.ParentId != ChatBackupEncoding.DecodeVersion(head).VersionId || !SHA256.HashData(head).AsSpan().SequenceEqual(version.ParentHash.Span))
            { return ChatBackupCommitResult.Conflict; }
            if (BinaryPrimitives.ReadInt32BigEndian(pending.AsSpan(8)) != version.PartCount || BinaryPrimitives.ReadInt64BigEndian(pending.AsSpan(12)) != version.TotalBytes)
            { throw Failure(ChatBackupFailure.Incomplete); }
            byte[] hash = new byte[32]; long size = 0;
            for (var index = 0; index < version.PartCount; index++)
            {
                var bytes = await tx.ReadAsync(id, index.ToString("D10", CultureInfo.InvariantCulture), ct).ConfigureAwait(false) ?? throw Failure(ChatBackupFailure.Incomplete);
                var part = ChatBackupEncoding.DecodePart(bytes);
                if (part.UploadId != version.VersionId || part.Index != index || !part.PreviousHash.Span.SequenceEqual(hash)) { throw Failure(ChatBackupFailure.Incomplete); }
                hash = SHA256.HashData(bytes); size += bytes.Length;
            }
            if (size != version.TotalBytes || !hash.AsSpan().SequenceEqual(version.FinalHash.Span)) { throw Failure(ChatBackupFailure.Incomplete); }
            var count = await Counter(tx, "versions", ct).ConfigureAwait(false); if (count >= _options.MaximumVersions) { throw Failure(ChatBackupFailure.Quota); }
            await tx.WriteAsync("versions", id, encoded, ct).ConfigureAwait(false); await tx.WriteAsync("control", "head", encoded, ct).ConfigureAwait(false);
            await tx.DeleteAsync("pending", id, ct).ConfigureAwait(false); await SetCounter(tx, "versions", count + 1, ct).ConfigureAwait(false);
            return ChatBackupCommitResult.Committed;
        }, cancellationToken);
    }
    /// <summary>Reads a completed version; unfinished descriptors are not exposed as history.</summary>
    public ValueTask<ChatBackupVersion?> GetVersionAsync(ChatBackupScope scope, Guid archiveId, Guid versionId, CancellationToken cancellationToken = default)
    {
        Id(versionId); return Run(scope, async (tx, ct) =>
        { await RequireArchive(tx, scope, archiveId, ct).ConfigureAwait(false); var bytes = await tx.ReadAsync("versions", Key(versionId), ct).ConfigureAwait(false); return bytes is null ? null : ChatBackupEncoding.DecodeVersion(bytes); }, cancellationToken);
    }
    /// <summary>Reads a part only from a completed version.</summary>
    public ValueTask<ChatBackupPart> GetPartAsync(ChatBackupScope scope, Guid archiveId, Guid versionId, int index, CancellationToken cancellationToken = default)
    {
        Id(versionId); if (index is < 0 or >= ChatBackupLimits.MaxParts) { throw new ChatBackupFormatException(); }
        return Run(scope, async (tx, ct) =>
        {
            await RequireArchive(tx, scope, archiveId, ct).ConfigureAwait(false);
            if (await tx.ReadAsync("versions", Key(versionId), ct).ConfigureAwait(false) is null) { throw Failure(ChatBackupFailure.NotFound); }
            return ChatBackupEncoding.DecodePart(await tx.ReadAsync(Key(versionId), index.ToString("D10", CultureInfo.InvariantCulture), ct).ConfigureAwait(false) ?? throw Failure(ChatBackupFailure.NotFound));
        }, cancellationToken);
    }
    /// <summary>Explicit scoped pending cleanup, also performed before beginning an upload; never touches completed ancestry.</summary>
    public async ValueTask CleanupAsync(ChatBackupScope scope, CancellationToken cancellationToken = default)
    { await Run(scope, async (tx, ct) => { await Cleanup(tx, ct).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false); }
    private async ValueTask Cleanup(IChatBackupTransaction tx, CancellationToken ct)
    {
        var ids = await tx.ListAsync("pending", null, 32, ct).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var descriptor = await tx.ReadAsync("pending", id, ct).ConfigureAwait(false) ?? throw Failure(ChatBackupFailure.Incomplete);
            if (descriptor.Length != 20) { throw new ChatBackupFormatException(); }
            if (BinaryPrimitives.ReadInt64BigEndian(descriptor) > _time.GetUtcNow().UtcTicks) { continue; }
            string? cursor = null;
            do
            {
                var keys = await tx.ListAsync(id, cursor, 100, ct).ConfigureAwait(false);
                foreach (var key in keys) { await tx.DeleteAsync(id, key, ct).ConfigureAwait(false); }
                cursor = keys.Count == 100 ? keys[^1] : null;
            } while (cursor is not null);
            await SetCounter(tx, "bytes", await Counter(tx, "bytes", ct).ConfigureAwait(false) - BinaryPrimitives.ReadInt64BigEndian(descriptor.AsSpan(12)), ct).ConfigureAwait(false);
            await tx.DeleteAsync("pending", id, ct).ConfigureAwait(false);
        }
    }
    private async ValueTask<byte[]> RequirePending(IChatBackupTransaction tx, Guid id, CancellationToken ct)
    {
        var bytes = await tx.ReadAsync("pending", Key(id), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length != 20 || BinaryPrimitives.ReadInt64BigEndian(bytes) <= _time.GetUtcNow().UtcTicks) { throw Failure(ChatBackupFailure.Incomplete); }
        return bytes;
    }
    private static async ValueTask<ChatBackupArchive> RequireArchive(IChatBackupTransaction tx, ChatBackupScope scope, Guid id, CancellationToken ct)
    {
        Id(id); var bytes = await tx.ReadAsync("control", "archive", ct).ConfigureAwait(false) ?? throw Failure(ChatBackupFailure.NotFound);
        var archive = ChatBackupEncoding.DecodeArchive(bytes); if (archive.Scope != scope || archive.ArchiveId != id) { throw Failure(ChatBackupFailure.NotFound); }
        return archive;
    }
    private static async ValueTask<long> Counter(IChatBackupTransaction tx, string key, CancellationToken ct)
    { var bytes = await tx.ReadAsync("counters", key, ct).ConfigureAwait(false); if (bytes is null) { return 0; } if (bytes.Length != 8) { throw new ChatBackupFormatException(); } return BinaryPrimitives.ReadInt64BigEndian(bytes); }
    private static ValueTask SetCounter(IChatBackupTransaction tx, string key, long value, CancellationToken ct)
    { if (value < 0) { throw new ChatBackupFormatException(); } var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return tx.WriteAsync("counters", key, bytes, ct); }
    private async ValueTask<T> Run<T>(ChatBackupScope scope, Func<IChatBackupTransaction, CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        try { await using var tx = await _storage.BeginAsync(scope, ct).ConfigureAwait(false); var result = await action(tx, ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return result; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChatBackupException) { throw; }
        catch (ChatBackupFormatException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { throw Failure(ChatBackupFailure.Unavailable); }
    }
    private static string Key(Guid id) => id.ToString("N");
    private static void Id(Guid id) { if (id == Guid.Empty) { throw new ChatBackupFormatException(); } }
    private static ChatBackupException Failure(ChatBackupFailure failure) => new(failure);
}
