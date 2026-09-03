using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage;

/// <summary>Opt-in, UI-independent encrypted backup session. Dispose and await it on logout/account switch.</summary>
/// <remarks>Owns the supplied key-store/workspace session handles, but never the live journal, transport or device identity.</remarks>
public sealed class ChatBackupCoordinator : IAsyncDisposable
{
    private readonly IChatBackupKeyStore _keys;
    private readonly IChatBackupWorkspace _workspace;
    private readonly IChatEventStore _events;
    private readonly IChatBackupTransport _transport;
    private readonly ChatBackupCryptography _crypto;
    private readonly TimeProvider _time;
    private readonly ChatBackupClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private volatile bool _closed;

    /// <summary>Creates an explicitly scoped session. The caller supplies normally authenticated transport and protected adapters.</summary>
    public ChatBackupCoordinator(IChatBackupKeyStore keys, IChatBackupWorkspace workspace, IChatEventStore events,
        IChatBackupTransport transport, ChatBackupCryptography crypto, TimeProvider timeProvider, ChatBackupClientOptions? options = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys)); _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _events = events ?? throw new ArgumentNullException(nameof(events)); _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto)); _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (keys.Scope != workspace.Scope) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        _options = options ?? new(); _options.Validate(); Scope = new(keys.Scope.ServiceId, keys.Scope.UserId);
    }
    /// <summary>Independently expected service/account identity, unrelated to the current Auth session ID.</summary>
    public ChatBackupScope Scope { get; }
    /// <summary>Safe reusable UI state. Recovery keys are returned only by an explicit export operation.</summary>
    public ChatBackupStatus Status { get; private set; } = new(ChatBackupPhase.Disabled);
    /// <summary>Optional progress observer; no content, secret or provider exception is reported.</summary>
    public IProgress<ChatBackupStatus>? Progress { get; set; }

    /// <summary>Loads status without creating an archive or key.</summary>
    public ValueTask<ChatBackupStatus> RefreshAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        var state = await LoadStateAsync(token).ConfigureAwait(false);
        using var credential = await _keys.LoadAsync(token).ConfigureAwait(false);
        if (credential is null) { Set(new(ChatBackupPhase.Disabled)); return Status; }
        ValidateCredential(credential, state);
        var head = await _transport.GetHeadAsync(credential.Archive.ArchiveId, token).ConfigureAwait(false);
        using var key = credential.OpenKey();
        if (head is not null) { await VerifyAncestorsAsync(key, credential.Archive, head, state.Pin, token).ConfigureAwait(false); }
        else if (state.Pin is not null) { throw new ChatBackupException(ChatBackupFailure.Rollback); }
        Set(new(state.Confirmed ? ChatBackupPhase.Ready : ChatBackupPhase.AwaitingConfirmation, LastBackupAt: head?.CreatedAt)); return Status;
    }, cancellationToken);

    /// <summary>Explicitly creates a recovery key for an absent account archive, or re-exports the same retained key.</summary>
    /// <remarks>The returned immutable string is a secret. Show/save it locally; never log or send it to the server.</remarks>
    public ValueTask<string> BeginEnableAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        var state = await LoadStateAsync(token).ConfigureAwait(false);
        using var retained = await _keys.LoadAsync(token).ConfigureAwait(false);
        var remote = await _transport.GetArchiveAsync(token).ConfigureAwait(false);
        if (remote is not null && remote.Scope != Scope) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        if (retained is not null)
        {
            ValidateCredential(retained, state);
            if (remote is not null && remote != retained.Archive) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
            using var saved = retained.OpenKey(); Set(new(state.Confirmed ? ChatBackupPhase.Ready : ChatBackupPhase.AwaitingConfirmation));
            return saved.ExportRecoveryCode();
        }
        if (remote is not null || state.ArchiveId != Guid.Empty) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
        using var created = ChatBackupRecoveryKey.Create(); var bytes = created.ExportBytes();
        try
        {
            using var credential = new ChatBackupCredential(new(Scope, Guid.NewGuid(), Guid.NewGuid()), bytes);
            if (!await _keys.TryCreateAsync(credential, token).ConfigureAwait(false)) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
            state.ArchiveId = credential.Archive.ArchiveId; state.Confirmed = false;
            await SaveStateAsync(state, token).ConfigureAwait(false); Set(new(ChatBackupPhase.AwaitingConfirmation));
            return created.ExportRecoveryCode();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }, cancellationToken);

    /// <summary>Confirms that the user retained the exact recovery key. Does not silently generate or replace anything.</summary>
    public ValueTask ConfirmRecoveryKeyAsync(string recoveryCode, CancellationToken cancellationToken = default) => AsVoid(RunAsync(async token =>
    {
        using var supplied = ChatBackupRecoveryKey.Parse(recoveryCode);
        using var credential = await RequireCredentialAsync(token).ConfigureAwait(false); using var saved = credential.OpenKey();
        RequireSameKey(saved, supplied); var state = await LoadStateAsync(token).ConfigureAwait(false); ValidateCredential(credential, state);
        state.ArchiveId = credential.Archive.ArchiveId; state.Confirmed = true;
        await SaveStateAsync(state, token).ConfigureAwait(false); Set(new(ChatBackupPhase.Ready)); return true;
    }, cancellationToken));

    /// <summary>Unlocks an existing completed archive on a new device with its recovery key; never clones device identity.</summary>
    public ValueTask UnlockAsync(string recoveryCode, CancellationToken cancellationToken = default) => AsVoid(RunAsync(async token =>
    {
        using var key = ChatBackupRecoveryKey.Parse(recoveryCode);
        var archive = await _transport.GetArchiveAsync(token).ConfigureAwait(false) ?? throw new ChatBackupException(ChatBackupFailure.NotFound);
        if (archive.Scope != Scope) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        var head = await _transport.GetHeadAsync(archive.ArchiveId, token).ConfigureAwait(false) ?? throw new ChatBackupException(ChatBackupFailure.NotFound);
        var state = await LoadStateAsync(token).ConfigureAwait(false);
        await VerifyAncestorsAsync(key, archive, head, state.Pin, token).ConfigureAwait(false);
        using var retained = await _keys.LoadAsync(token).ConfigureAwait(false);
        if (retained is not null)
        {
            if (retained.Archive != archive) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
            using var saved = retained.OpenKey(); RequireSameKey(saved, key);
        }
        else
        {
            var bytes = key.ExportBytes();
            try
            {
                using var credential = new ChatBackupCredential(archive, bytes);
                if (!await _keys.TryCreateAsync(credential, token).ConfigureAwait(false)) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        state.ArchiveId = archive.ArchiveId; state.Confirmed = true;
        // The new head becomes a freshness pin only after a complete restore or a committed contribution.
        await SaveStateAsync(state, token).ConfigureAwait(false); Set(new(ChatBackupPhase.Ready, LastBackupAt: head.CreatedAt)); return true;
    }, cancellationToken));

    /// <summary>Prepares, uploads/resumes and atomically appends a verified-journal contribution. No outbox is exported.</summary>
    public ValueTask<ChatBackupVersion> BackupAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        using var credential = await RequireCredentialAsync(token).ConfigureAwait(false); using var key = credential.OpenKey();
        var state = await LoadStateAsync(token).ConfigureAwait(false); ValidateCredential(credential, state);
        if (!state.Confirmed) { throw new ChatBackupException(ChatBackupFailure.ConfirmationRequired); }
        await CleanupAsync(state, token).ConfigureAwait(false);
        var archive = await _transport.GetArchiveAsync(token).ConfigureAwait(false);
        if (archive is not null && archive != credential.Archive) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
        if (archive is null && !await _transport.TryCreateArchiveAsync(credential.Archive, token).ConfigureAwait(false)) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
        archive = credential.Archive;
        if (state.Upload?.Prepared != true) { await PrepareAsync(state, key, archive, token).ConfigureAwait(false); }
        var upload = state.Upload!;
        var completed = await _transport.GetVersionAsync(archive.ArchiveId, upload.Id, token).ConfigureAwait(false);
        if (completed is not null)
        {
            RequireExactCompleted(key, archive, upload, completed);
            await FinishBackupAsync(state, completed, token).ConfigureAwait(false); return completed;
        }
        await _transport.BeginUploadAsync(archive.ArchiveId, upload.Id, token).ConfigureAwait(false);
        long sent = 0;
        for (var index = 0; index < upload.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var encoded = await _workspace.ReadAsync(UploadGroup(upload.Id), PartKey(index), token).ConfigureAwait(false)
                ?? throw new ChatBackupException(ChatBackupFailure.Incomplete);
            try
            {
                var part = ChatBackupEncoding.DecodePart(encoded);
                if (part.UploadId != upload.Id || part.Index != index) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
                await _transport.PutPartAsync(archive.ArchiveId, part, token).ConfigureAwait(false); sent += encoded.Length;
                Set(new(ChatBackupPhase.Uploading, index + 1, sent, Status.LastBackupAt));
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        for (var attempt = 0; attempt < _options.MaximumCommitAttempts; attempt++)
        {
            // Check commit ambiguity before rebasing; a successful immutable ID can never be rewritten.
            completed = await _transport.GetVersionAsync(archive.ArchiveId, upload.Id, token).ConfigureAwait(false);
            if (completed is not null) { RequireExactCompleted(key, archive, upload, completed); await FinishBackupAsync(state, completed, token).ConfigureAwait(false); return completed; }
            var parent = await _transport.GetHeadAsync(archive.ArchiveId, token).ConfigureAwait(false);
            if (parent is not null) { await VerifyAncestorsAsync(key, archive, parent, state.Pin, token).ConfigureAwait(false); }
            else if (state.Pin is not null) { throw new ChatBackupException(ChatBackupFailure.Rollback); }
            var seal = _crypto.Seal(key, archive, upload.Id, parent, upload.Count, upload.Bytes, upload.Hash, _time.GetUtcNow());
            upload.Seal = ChatBackupEncoding.EncodeVersion(seal); await SaveStateAsync(state, token).ConfigureAwait(false);
            var result = await _transport.CommitAsync(seal, token).ConfigureAwait(false);
            if (result is ChatBackupCommitResult.Committed or ChatBackupCommitResult.Duplicate)
            { await FinishBackupAsync(state, seal, token).ConfigureAwait(false); return seal; }
            if (result != ChatBackupCommitResult.Conflict) { throw new ChatBackupException(ChatBackupFailure.Unavailable); }
        }
        throw new ChatBackupException(ChatBackupFailure.Conflict);
    }, cancellationToken);

    /// <summary>Authenticates the entire archive into invisible staging, then atomically exposes it without live-event callbacks or ACKs.</summary>
    public ValueTask<long> RestoreAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        using var credential = await RequireCredentialAsync(token).ConfigureAwait(false); using var key = credential.OpenKey();
        var state = await LoadStateAsync(token).ConfigureAwait(false); ValidateCredential(credential, state);
        if (!state.Confirmed) { throw new ChatBackupException(ChatBackupFailure.ConfirmationRequired); }
        await CleanupAsync(state, token).ConfigureAwait(false);
        var head = await _transport.GetHeadAsync(credential.Archive.ArchiveId, token).ConfigureAwait(false) ?? throw new ChatBackupException(ChatBackupFailure.NotFound);
        await VerifyAncestorsAsync(key, credential.Archive, head, state.Pin, token).ConfigureAwait(false);
        var encodedHead = ChatBackupEncoding.EncodeVersion(head);
        if (state.RestoredHead is not null && state.RestoredHead.AsSpan().SequenceEqual(encodedHead))
        { Set(new(ChatBackupPhase.Completed, state.RestoredCount, LastBackupAt: head.CreatedAt)); return state.RestoredCount; }
        if (state.Restore is null || !state.Restore.Head.AsSpan().SequenceEqual(encodedHead))
        {
            if (state.Restore is not null)
            {
                await DeleteGroupAsync(RestoreGroup(state.Restore.Id), token).ConfigureAwait(false);
                await DeleteGroupAsync(ProofGroup(state.Restore.Id), token).ConfigureAwait(false);
            }
            state.Restore = new() { Id = Guid.NewGuid(), Head = encodedHead, CursorVersionId = head.VersionId }; await SaveStateAsync(state, token).ConfigureAwait(false);
        }
        var checkpoint = state.Restore;
        var group = RestoreGroup(checkpoint.Id); long count = checkpoint.ProcessedParts; long totalBytes = checkpoint.ProcessedBytes; var version = head;
        var resumed = false; long expectedPartCount = 0;
        if (totalBytes > _options.MaximumBytes) { throw new ChatBackupException(ChatBackupFailure.Quota); }
        for (var depth = 0; ; depth++)
        {
            if (depth >= _options.MaximumVersions) { throw new ChatBackupException(ChatBackupFailure.Quota); }
            _crypto.Verify(key, credential.Archive, version);
            expectedPartCount += version.PartCount;
            if (version.VersionId == checkpoint.CursorVersionId) { resumed = true; }
            var previous = checkpoint.PreviousHash; long contributionBytes = checkpoint.ContributionBytes;
            if (resumed && checkpoint.NextIndex > version.PartCount) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
            for (var index = checkpoint.NextIndex; resumed && index < version.PartCount; index++)
            {
                var part = await _transport.GetPartAsync(credential.Archive.ArchiveId, version.VersionId, index, token).ConfigureAwait(false)
                    ?? throw new ChatBackupException(ChatBackupFailure.Incomplete);
                if (part.UploadId != version.VersionId || part.Index != index || !part.PreviousHash.Span.SequenceEqual(previous)) { throw new ChatBackupException(ChatBackupFailure.Incomplete); }
                var encoded = ChatBackupEncoding.EncodePart(part); contributionBytes += encoded.Length; totalBytes += encoded.Length;
                if (totalBytes > _options.MaximumBytes) { throw new ChatBackupException(ChatBackupFailure.Quota); }
                previous = SHA256.HashData(encoded); var plaintext = _crypto.Decrypt(key, credential.Archive, part);
                try
                {
                    _ = ChatBackupEventEncoding.Decode(plaintext);
                    var eventId = Convert.ToHexStringLower(SHA256.HashData(plaintext));
                    await _workspace.WriteAsync(group, eventId, plaintext, cancellationToken: token).ConfigureAwait(false);
                    await _workspace.WriteAsync(ProofGroup(checkpoint.Id), version.VersionId.ToString("N") + "-" + PartKey(index),
                        Convert.FromHexString(eventId), cancellationToken: token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                count++;
                // The row is durable before advancing this protected cursor. A crash between the two writes replays an exact duplicate.
                checkpoint.NextIndex = index + 1; checkpoint.PreviousHash = previous; checkpoint.ContributionBytes = contributionBytes;
                checkpoint.ProcessedParts = count; checkpoint.ProcessedBytes = totalBytes;
                await SaveStateAsync(state, token).ConfigureAwait(false);
                Set(new(ChatBackupPhase.Restoring, count, totalBytes, head.CreatedAt));
            }
            if (resumed && (contributionBytes != version.TotalBytes || !version.FinalHash.Span.SequenceEqual(previous))) { throw new ChatBackupException(ChatBackupFailure.Incomplete); }
            if (version.ParentId is not Guid parentId)
            { if (!resumed) { throw new ChatBackupException(ChatBackupFailure.Incomplete); } break; }
            var parent = await _transport.GetVersionAsync(credential.Archive.ArchiveId, parentId, token).ConfigureAwait(false) ?? throw new ChatBackupException(ChatBackupFailure.Incomplete);
            if (!version.ParentHash.Span.SequenceEqual(SHA256.HashData(ChatBackupEncoding.EncodeVersion(parent)))) { throw new ChatBackupException(ChatBackupFailure.Authentication); }
            if (resumed)
            {
                checkpoint.CursorVersionId = parentId; checkpoint.NextIndex = 0; checkpoint.PreviousHash = new byte[32]; checkpoint.ContributionBytes = 0;
                await SaveStateAsync(state, token).ConfigureAwait(false);
            }
            version = parent;
        }
        // A durable cursor is not proof that earlier staging rows survived a local deletion/corruption.
        // Recheck every part-to-event reference, one bounded record at a time, before making the snapshot visible.
        long verifiedParts = 0; string? proofCursor = null;
        do
        {
            var page = await _workspace.ReadPageAsync(ProofGroup(checkpoint.Id), proofCursor, 50, token).ConfigureAwait(false);
            foreach (var proofId in page.Keys)
            {
                var hash = await _workspace.ReadAsync(ProofGroup(checkpoint.Id), proofId, token).ConfigureAwait(false);
                if (hash?.Length != 32) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
                var eventBytes = await _workspace.ReadAsync(group, Convert.ToHexStringLower(hash), token).ConfigureAwait(false)
                    ?? throw new ChatBackupException(ChatBackupFailure.LocalStorage);
                try
                {
                    if (!SHA256.HashData(eventBytes).AsSpan().SequenceEqual(hash)) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
                    _ = ChatBackupEventEncoding.Decode(eventBytes); verifiedParts++;
                }
                finally { CryptographicOperations.ZeroMemory(eventBytes); }
            }
            proofCursor = page.NextCursor;
        } while (proofCursor is not null);
        if (verifiedParts != expectedPartCount || count != expectedPartCount) { throw new ChatBackupException(ChatBackupFailure.Incomplete); }
        token.ThrowIfCancellationRequested();
        if (state.RestoredId != Guid.Empty) { state.Garbage.Add(RestoreGroup(state.RestoredId)); state.Garbage.Add(ProofGroup(state.RestoredId)); }
        state.RestoredId = state.Restore.Id; state.RestoredHead = encodedHead; state.Restore = null; state.Pin = encodedHead;
        // Count unique staged variants with bounded key pages; repeated overlapping device histories are not new events.
        state.RestoredCount = await CountGroupAsync(group, token).ConfigureAwait(false);
        await SaveStateAsync(state, token).ConfigureAwait(false); Set(new(ChatBackupPhase.Completed, state.RestoredCount, totalBytes, head.CreatedAt)); return state.RestoredCount;
    }, cancellationToken);

    /// <summary>Streams only the completely committed restored snapshot, never staging or live delivery callbacks.</summary>
    /// <remarks>Apply deliberately through ChatConversationProjection.ApplyRestored. Enumeration holds the session lease; dispose enumerators promptly.</remarks>
    public async IAsyncEnumerable<RestoredChatContent> ReadRestoredAsync(ConversationId? conversationId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Check(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token); var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Check(); await using var lease = await _workspace.AcquireAsync(token).ConfigureAwait(false); var state = await LoadStateAsync(token).ConfigureAwait(false);
            using var credential = await RequireCredentialAsync(token).ConfigureAwait(false); ValidateCredential(credential, state);
            if (!state.Confirmed) { throw new ChatBackupException(ChatBackupFailure.ConfirmationRequired); }
            if (state.RestoredId == Guid.Empty) { yield break; }
            var group = RestoreGroup(state.RestoredId); string? cursor = null;
            do
            {
                var page = await _workspace.ReadPageAsync(group, cursor, 50, token).ConfigureAwait(false);
                foreach (var id in page.Keys)
                {
                    var bytes = await _workspace.ReadAsync(group, id, token).ConfigureAwait(false) ?? throw new ChatBackupException(ChatBackupFailure.LocalStorage);
                    RestoredChatContent item;
                    try
                    {
                        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != id) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
                        item = ChatBackupEventEncoding.Decode(bytes);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    if (conversationId is null || item.ConversationId == conversationId) { yield return item; }
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask PrepareAsync(BackupLocalState state, ChatBackupRecoveryKey key, ChatBackupArchive archive, CancellationToken token)
    {
        if (state.Upload is not null) { await DeleteGroupAsync(UploadGroup(state.Upload.Id), token).ConfigureAwait(false); }
        for (var pass = 0; pass < 2; pass++)
        {
            var upload = new BackupUploadState { Id = Guid.NewGuid() }; state.Upload = upload; await SaveStateAsync(state, token).ConfigureAwait(false);
            var skip = pass == 0 ? state.ExportCount : 0; var prefixMatches = skip == 0; var rolling = new byte[32]; long sourceCount = 0;
            await foreach (var item in _events.ReadAllAsync(token).ConfigureAwait(false))
            {
                var plaintext = ChatBackupEventEncoding.Encode(item);
                try
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(rolling); hash.AppendData(plaintext); rolling = hash.GetHashAndReset();
                    sourceCount++;
                    if (sourceCount <= skip)
                    {
                        if (sourceCount == skip) { prefixMatches = rolling.AsSpan().SequenceEqual(state.ExportHash); }
                        continue;
                    }
                    if (!prefixMatches) { break; }
                    if (upload.Count >= _options.MaximumParts) { throw new ChatBackupException(ChatBackupFailure.Quota); }
                    var part = _crypto.Encrypt(key, archive, upload.Id, upload.Count, upload.Hash, plaintext); var bytes = ChatBackupEncoding.EncodePart(part);
                    if (upload.Bytes + bytes.Length > _options.MaximumBytes) { throw new ChatBackupException(ChatBackupFailure.Quota); }
                    await _workspace.WriteAsync(UploadGroup(upload.Id), PartKey(upload.Count), bytes, cancellationToken: token).ConfigureAwait(false);
                    upload.Count++; upload.Bytes += bytes.Length; upload.Hash = SHA256.HashData(bytes);
                    Set(new(ChatBackupPhase.Preparing, upload.Count, upload.Bytes, Status.LastBackupAt));
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            if (!prefixMatches || sourceCount < skip)
            { await DeleteGroupAsync(UploadGroup(upload.Id), token).ConfigureAwait(false); continue; }
            upload.SourceCount = sourceCount; upload.SourceHash = rolling; upload.Prepared = true;
            await SaveStateAsync(state, token).ConfigureAwait(false); return;
        }
        throw new ChatBackupException(ChatBackupFailure.LocalStorage);
    }
    private async ValueTask VerifyAncestorsAsync(ChatBackupRecoveryKey key, ChatBackupArchive archive, ChatBackupVersion head, byte[]? pin, CancellationToken token)
    {
        var expectedPin = pin is null ? null : ChatBackupEncoding.DecodeVersion(pin); var found = expectedPin is null; var current = head;
        for (var depth = 0; depth < _options.MaximumVersions; depth++)
        {
            _crypto.Verify(key, archive, current);
            if (expectedPin?.VersionId == current.VersionId)
            {
                if (!ChatBackupEncoding.EncodeVersion(current).AsSpan().SequenceEqual(pin)) { throw new ChatBackupException(ChatBackupFailure.Rollback); }
                found = true;
            }
            if (current.ParentId is not Guid parentId)
            { if (!found) { throw new ChatBackupException(ChatBackupFailure.Rollback); } return; }
            var parent = await _transport.GetVersionAsync(archive.ArchiveId, parentId, token).ConfigureAwait(false) ?? throw new ChatBackupException(ChatBackupFailure.Incomplete);
            if (parent.VersionId != parentId || !current.ParentHash.Span.SequenceEqual(SHA256.HashData(ChatBackupEncoding.EncodeVersion(parent)))) { throw new ChatBackupException(ChatBackupFailure.Authentication); }
            current = parent;
        }
        throw new ChatBackupException(ChatBackupFailure.Quota);
    }
    private void RequireExactCompleted(ChatBackupRecoveryKey key, ChatBackupArchive archive, BackupUploadState upload, ChatBackupVersion completed)
    {
        _crypto.Verify(key, archive, completed);
        if (upload.Seal is null || !ChatBackupEncoding.EncodeVersion(completed).AsSpan().SequenceEqual(upload.Seal)) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
    }
    private async ValueTask FinishBackupAsync(BackupLocalState state, ChatBackupVersion seal, CancellationToken token)
    {
        var upload = state.Upload!; state.ExportCount = upload.SourceCount; state.ExportHash = upload.SourceHash;
        state.Pin = ChatBackupEncoding.EncodeVersion(seal); state.Garbage.Add(UploadGroup(upload.Id)); state.Upload = null;
        await SaveStateAsync(state, token).ConfigureAwait(false); Set(new(ChatBackupPhase.Completed, seal.PartCount, seal.TotalBytes, seal.CreatedAt));
    }
    private async ValueTask<ChatBackupCredential> RequireCredentialAsync(CancellationToken token) =>
        await _keys.LoadAsync(token).ConfigureAwait(false) ?? throw new ChatBackupException(ChatBackupFailure.Locked);
    private void ValidateCredential(ChatBackupCredential credential, BackupLocalState state)
    {
        if (credential.Archive.Scope != Scope || (state.ArchiveId != Guid.Empty && credential.Archive.ArchiveId != state.ArchiveId)) { throw new ChatBackupException(ChatBackupFailure.Scope); }
    }
    private static void RequireSameKey(ChatBackupRecoveryKey first, ChatBackupRecoveryKey second)
    {
        var a = first.ExportBytes(); var b = second.ExportBytes();
        try { if (!CryptographicOperations.FixedTimeEquals(a, b)) { throw new ChatBackupException(ChatBackupFailure.Authentication); } }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }
    private async ValueTask<BackupLocalState> LoadStateAsync(CancellationToken token)
    {
        var bytes = await _workspace.ReadAsync("state", "backup", token).ConfigureAwait(false);
        if (bytes is null) { return new(); }
        try
        {
            var value = JsonSerializer.Deserialize(bytes, BackupStateJson.Default.BackupLocalState) ?? throw new ChatBackupFormatException(); value.Validate(); return value;
        }
        catch (JsonException) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private async ValueTask SaveStateAsync(BackupLocalState state, CancellationToken token)
    {
        state.Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(state, BackupStateJson.Default.BackupLocalState);
        try { await _workspace.WriteAsync("state", "backup", bytes, true, token).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private async ValueTask CleanupAsync(BackupLocalState state, CancellationToken token)
    {
        foreach (var group in state.Garbage)
        {
            if (group == RestoreGroup(state.RestoredId) || group == ProofGroup(state.RestoredId) || (state.Upload is not null && group == UploadGroup(state.Upload.Id)) ||
                (state.Restore is not null && (group == RestoreGroup(state.Restore.Id) || group == ProofGroup(state.Restore.Id)))) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
            await DeleteGroupAsync(group, token).ConfigureAwait(false);
        }
        if (state.Garbage.Count > 0) { state.Garbage.Clear(); await SaveStateAsync(state, token).ConfigureAwait(false); }
    }
    private async ValueTask DeleteGroupAsync(string group, CancellationToken token)
    {
        string? cursor = null;
        do
        {
            var page = await _workspace.ReadPageAsync(group, cursor, 50, token).ConfigureAwait(false);
            foreach (var id in page.Keys) { await _workspace.DeleteAsync(group, id, token).ConfigureAwait(false); }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }
    private async ValueTask<long> CountGroupAsync(string group, CancellationToken token)
    {
        long count = 0; string? cursor = null;
        do { var page = await _workspace.ReadPageAsync(group, cursor, 100, token).ConfigureAwait(false); count += page.Keys.Count; cursor = page.NextCursor; } while (cursor is not null);
        return count;
    }
    private async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        Check(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token); var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Check(); await using var lease = await _workspace.AcquireAsync(token).ConfigureAwait(false); return await action(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { if (!_closed) { Set(Status with { Phase = ChatBackupPhase.Ready, Failure = null }); } throw; }
        catch (ChatBackupException error) { if (!_closed) { Set(Status with { Phase = ChatBackupPhase.Failed, Failure = error.Failure }); } throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { if (!_closed) { Set(Status with { Phase = ChatBackupPhase.Failed, Failure = ChatBackupFailure.LocalStorage }); } throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
        finally { _gate.Release(); }
    }
    private static async ValueTask AsVoid<T>(ValueTask<T> operation) => _ = await operation.ConfigureAwait(false);
    private void Check() { if (_closed) { throw new ChatBackupException(ChatBackupFailure.Locked); } }
    private void Set(ChatBackupStatus status) { Status = status; Progress?.Report(status); }
    private static string UploadGroup(Guid id) => "upload-" + id.ToString("N");
    private static string RestoreGroup(Guid id) => "restore-" + id.ToString("N");
    private static string ProofGroup(Guid id) => "proof-" + id.ToString("N");
    private static string PartKey(int index) => index.ToString("D6", CultureInfo.InvariantCulture);
    /// <summary>Cancels/awaits operations and closes recovery-key/workspace handles without deleting records.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed) { return; }
        _closed = true; await _lifetime.CancelAsync().ConfigureAwait(false); await _gate.WaitAsync().ConfigureAwait(false);
        try { await _keys.DisposeAsync().ConfigureAwait(false); await _workspace.DisposeAsync().ConfigureAwait(false); Set(Status with { Phase = ChatBackupPhase.Locked, Failure = null }); }
        finally { _gate.Release(); }
    }
}

internal sealed class BackupLocalState
{
    [JsonRequired] public int Version { get; set; } = 1;
    public Guid ArchiveId { get; set; }
    public bool Confirmed { get; set; }
    public long ExportCount { get; set; }
    public byte[] ExportHash { get; set; } = new byte[32];
    public byte[]? Pin { get; set; }
    public BackupUploadState? Upload { get; set; }
    public BackupRestoreState? Restore { get; set; }
    public Guid RestoredId { get; set; }
    public byte[]? RestoredHead { get; set; }
    public long RestoredCount { get; set; }
    public List<string> Garbage { get; set; } = [];
    public void Validate()
    {
        if (Version != 1 || ExportCount < 0 || ExportHash?.Length != 32 || RestoredCount < 0 || Garbage is null || Garbage.Count > 8 ||
            (RestoredId == Guid.Empty) != (RestoredHead is null)) { throw new ChatBackupFormatException(); }
        if (Pin is not null) { _ = ChatBackupEncoding.DecodeVersion(Pin); }
        if (RestoredHead is not null) { _ = ChatBackupEncoding.DecodeVersion(RestoredHead); }
        foreach (var group in Garbage) { ChatBackupLocalValidation.Validate(group, "cleanup"); if (!group.StartsWith("upload-", StringComparison.Ordinal) && !group.StartsWith("restore-", StringComparison.Ordinal) && !group.StartsWith("proof-", StringComparison.Ordinal)) { throw new ChatBackupFormatException(); } }
        if (Upload is not null && (Upload.Id == Guid.Empty || Upload.Count is < 0 or > ChatBackupLimits.MaxParts || Upload.Bytes < 0 ||
            Upload.Hash?.Length != 32 || Upload.SourceHash?.Length != 32 || Upload.SourceCount < 0)) { throw new ChatBackupFormatException(); }
        if (Upload?.Seal is not null) { _ = ChatBackupEncoding.DecodeVersion(Upload.Seal); }
        if (Restore is not null)
        {
            if (Restore.Id == Guid.Empty || Restore.Head is null || Restore.CursorVersionId == Guid.Empty || Restore.NextIndex is < 0 or > ChatBackupLimits.MaxParts ||
                Restore.PreviousHash?.Length != 32 || Restore.ContributionBytes < 0 || Restore.ProcessedParts < 0 || Restore.ProcessedBytes < Restore.ContributionBytes ||
                Restore.ProcessedParts < Restore.NextIndex || (Restore.NextIndex == 0 && (Restore.ContributionBytes != 0 || Restore.PreviousHash.Any(value => value != 0)))) { throw new ChatBackupFormatException(); }
            _ = ChatBackupEncoding.DecodeVersion(Restore.Head);
        }
    }
}
internal sealed class BackupUploadState
{
    public Guid Id { get; set; }
    public bool Prepared { get; set; }
    public int Count { get; set; }
    public long Bytes { get; set; }
    public byte[] Hash { get; set; } = new byte[32];
    public long SourceCount { get; set; }
    public byte[] SourceHash { get; set; } = new byte[32];
    public byte[]? Seal { get; set; }
}
internal sealed class BackupRestoreState
{
    public Guid Id { get; set; }
    public byte[] Head { get; set; } = [];
    public Guid CursorVersionId { get; set; }
    public int NextIndex { get; set; }
    public byte[] PreviousHash { get; set; } = new byte[32];
    public long ContributionBytes { get; set; }
    public long ProcessedParts { get; set; }
    public long ProcessedBytes { get; set; }
}
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(BackupLocalState))]
internal sealed partial class BackupStateJson : JsonSerializerContext;
