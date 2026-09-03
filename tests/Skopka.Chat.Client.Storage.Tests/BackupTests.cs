using System.Security.Cryptography;
using System.Text;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Client.Storage.Tests;

public sealed class BackupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public void Canonical_archive_and_recovery_checksum_match_frozen_v1_vectors()
    {
        var archive = new ChatBackupArchive(new("s", new(Guid.Parse("11111111-1111-1111-1111-111111111111"))),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var expected = "536B6F706B612E436861742E4261636B75700001410000000173" + new string('1', 32) + new string('2', 32) + new string('3', 32);
        Assert.Equal(expected, Convert.ToHexString(ChatBackupEncoding.EncodeArchive(archive)));
        Assert.Equal(archive, ChatBackupEncoding.DecodeArchive(Convert.FromHexString(expected)));
        using var recovery = ChatBackupRecoveryKey.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        Assert.Equal("SCB1-00010203-04050607-08090A0B-0C0D0E0F-10111213-14151617-18191A1B-1C1D1E1F-D4F520D8", recovery.ExportRecoveryCode());
    }

    [Fact]
    public async Task Restores_after_original_delivery_envelope_is_physically_removed_by_TTL()
    {
        var senderKeys = new InMemoryDeviceKeyStore(); var recipientKeys = new InMemoryDeviceKeyStore();
        var sender = await new DeviceIdentityService(senderKeys).CreateAsync(UserId.New(), DeviceId.New(), Now);
        var recipient = await new DeviceIdentityService(recipientKeys).CreateAsync(UserId.New(), DeviceId.New(), Now);
        var queue = new InMemoryServerStore(); var engine = new ChatServerEngine(queue, queue, queue);
        await engine.RegisterDeviceAsync(sender); await engine.RegisterDeviceAsync(recipient); var conversation = ConversationId.New();
        await engine.CreateConversationAsync(sender.UserId, recipient.UserId, conversation, Now);
        var content = new ChatTextContent(ChatContentId.New(), "synthetic expired delivery");
        var envelope = await new ChatCryptoService(senderKeys).EncryptContentAsync(content, conversation, MessageId.New(), sender.DeviceId, recipient, Now, Now.AddMinutes(5));
        await engine.SubmitAsync(envelope, Now.AddSeconds(1));
        var verified = await new ChatCryptoService(recipientKeys).DecryptContentAsync(envelope, sender);
        var scope = new ChatBackupScope("ttl-test", recipient.UserId); using var storage = new TestBackupStorage();
        var transport = new TestBackupTransport(new ChatBackupService(storage, TimeProvider.System), scope);
        await using var source = new TestDevice(scope, transport);
        await source.Events.StoreAsync(new ReceivedChatContent(envelope.MessageId, conversation, sender.UserId, sender.DeviceId, envelope.SentAt, verified));
        var code = await source.Backup.BeginEnableAsync(); await source.Backup.ConfirmRecoveryKeyAsync(code); await source.Backup.BackupAsync();
        Assert.Equal(1, await ((IEnvelopeRepository)queue).DeleteExpiredAsync(Now.AddDays(1))); Assert.Empty(queue.SnapshotEnvelopes());
        await using var fresh = new TestDevice(scope, transport); await fresh.Backup.UnlockAsync(code); Assert.Equal(1, await fresh.Backup.RestoreAsync());
        Assert.Equal(content.ContentId, Assert.Single(await All(fresh.Backup.ReadRestoredAsync())).Content.ContentId);
    }
    [Fact]
    public async Task Clean_device_restores_union_edits_and_replies_without_old_identity_delivery_or_outbox()
    {
        var storage = new TestBackupStorage(); var scope = new ChatBackupScope("test-service", UserId.New());
        var transport = new TestBackupTransport(new ChatBackupService(storage, TimeProvider.System), scope);
        var conversation = ConversationId.New(); var original = Event(conversation, new ChatTextContent(ChatContentId.New(), "synthetic-secret-186dfd"));
        var reply = Event(conversation, new ChatTextContent(ChatContentId.New(), "synthetic reply", original.Content.ContentId));
        var edit = new ReceivedChatContent(MessageId.New(), conversation, original.SenderUserId, original.SenderDeviceId, Now.AddMinutes(1),
            new ChatEditContent(ChatContentId.New(), original.Content.ContentId, ChatEditField.Text, "synthetic edited"));
        await using var first = new TestDevice(scope, transport); await first.Events.StoreAsync(original); await first.Events.StoreAsync(reply);
        Assert.Equal(ChatBackupPhase.Disabled, (await first.Backup.RefreshAsync()).Phase);
        var code = await first.Backup.BeginEnableAsync();
        Assert.Equal(ChatBackupFailure.ConfirmationRequired, (await Assert.ThrowsAsync<ChatBackupException>(() => first.Backup.BackupAsync().AsTask())).Failure);
        await first.Backup.ConfirmRecoveryKeyAsync(code); var firstHead = await first.Backup.BackupAsync();
        await using var second = new TestDevice(scope, transport);
        await second.Events.StoreAsync(new ReceivedChatContent(MessageId.New(), conversation, original.SenderUserId, original.SenderDeviceId, original.SentAt, original.Content));
        await second.Events.StoreAsync(edit); await second.Backup.UnlockAsync(code); var secondHead = await second.Backup.BackupAsync();
        Assert.Equal(firstHead.VersionId, secondHead.ParentId);
        await using var fresh = new TestDevice(scope, transport);
        var deviceKeys = new InMemoryDeviceKeyStore(); var identity = await new DeviceIdentityService(deviceKeys).CreateAsync(scope.UserId, DeviceId.New(), Now);
        await fresh.Backup.UnlockAsync(code); Assert.Equal(3, await fresh.Backup.RestoreAsync()); Assert.Equal(3, await fresh.Backup.RestoreAsync());
        Assert.Empty(await All(fresh.Events.ReadAllAsync()));
        var restored = await All(fresh.Backup.ReadRestoredAsync()); var projection = new ChatConversationProjection(conversation);
        foreach (var item in restored) { projection.ApplyRestored(item); }
        Assert.Equal(2, projection.Snapshot().Count); Assert.True(projection.ContainsBackupHistory);
        Assert.Contains(projection.Snapshot(), item => item.Text == "synthetic edited" && item.IsEdited);
        Assert.Contains(projection.Snapshot(), item => item.ReplyToContentId == original.Content.ContentId);
        Assert.All(projection.Snapshot(), item => Assert.True(item.ContainsBackupHistory));
        Assert.NotEqual(original.SenderDeviceId, identity.DeviceId);
        Assert.DoesNotContain(storage.Data.Values, bytes => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("synthetic-secret-186dfd")) >= 0);
        using var recovery = ChatBackupRecoveryKey.Parse(code); var secret = recovery.ExportBytes();
        try { Assert.DoesNotContain(storage.Data.Values, bytes => bytes.AsSpan().IndexOf(secret) >= 0); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        Assert.Equal(code, await first.Backup.BeginEnableAsync());
        await fresh.Backup.DisposeAsync(); Assert.Equal(ChatBackupPhase.Locked, fresh.Backup.Status.Phase);
        Assert.Equal(ChatBackupFailure.Locked, (await Assert.ThrowsAsync<ChatBackupException>(() => fresh.Backup.RestoreAsync().AsTask())).Failure);
    }

    [Fact]
    public async Task Interrupted_upload_commit_and_restore_resume_after_reopening_with_no_partial_visibility()
    {
        var scope = new ChatBackupScope("retry-service", UserId.New()); var storage = new TestBackupStorage();
        var transport = new TestBackupTransport(new ChatBackupService(storage, TimeProvider.System), scope);
        await using var source = new TestDevice(scope, transport); var conversation = ConversationId.New();
        for (var index = 0; index < 4; index++) { await source.Events.StoreAsync(Event(conversation, new ChatTextContent(ChatContentId.New(), "synthetic-" + index))); }
        var code = await source.Backup.BeginEnableAsync(); await source.Backup.ConfirmRecoveryKeyAsync(code);
        transport.FailAfterPut = true; await Assert.ThrowsAsync<ChatBackupException>(() => source.Backup.BackupAsync().AsTask());
        var archive = (await transport.GetArchiveAsync())!; Assert.Null(await transport.GetHeadAsync(archive.ArchiveId));
        await source.Reopen(); transport.FailAfterCommit = true; await Assert.ThrowsAsync<ChatBackupException>(() => source.Backup.BackupAsync().AsTask());
        var committed = (await transport.GetHeadAsync(archive.ArchiveId))!; await source.Reopen(); Assert.Equal(committed.VersionId, (await source.Backup.BackupAsync()).VersionId);
        var localFaults = new TestWorkspaceFaults();
        await using var destination = new TestDevice(scope, transport, localFaults); await destination.Backup.UnlockAsync(code);
        transport.FailReadIndex = 2; await Assert.ThrowsAsync<ChatBackupException>(() => destination.Backup.RestoreAsync().AsTask());
        Assert.Empty(await All(destination.Backup.ReadRestoredAsync())); await destination.Reopen();
        transport.FailReadIndex = -1;
        localFaults.UnreadableRestoreRow = true;
        Assert.Equal(ChatBackupFailure.LocalStorage, (await Assert.ThrowsAsync<ChatBackupException>(() => destination.Backup.RestoreAsync().AsTask())).Failure);
        Assert.Empty(await All(destination.Backup.ReadRestoredAsync())); localFaults.UnreadableRestoreRow = false;
        Assert.Equal(4, await destination.Backup.RestoreAsync());
        Assert.Equal(4, (await All(destination.Backup.ReadRestoredAsync())).Count);
        using var wrong = ChatBackupRecoveryKey.Create(); await using var wrongDevice = new TestDevice(scope, transport);
        Assert.Equal(ChatBackupFailure.Authentication, (await Assert.ThrowsAsync<ChatBackupException>(() => wrongDevice.Backup.UnlockAsync(wrong.ExportRecoveryCode()).AsTask())).Failure);
        await source.Events.StoreAsync(Event(conversation, new ChatTextContent(ChatContentId.New(), "later"))); await source.Backup.BackupAsync();
        transport.Tamper = true; await Assert.ThrowsAsync<ChatBackupException>(() => destination.Backup.RestoreAsync().AsTask());
        Assert.Equal(4, (await All(destination.Backup.ReadRestoredAsync())).Count);
    }

    [Fact]
    public async Task Concurrent_incomplete_devices_rebase_instead_of_replacing_history_and_quota_preserves_head()
    {
        var scope = new ChatBackupScope("race-service", UserId.New()); var storage = new TestBackupStorage();
        var service = new ChatBackupService(storage, TimeProvider.System, new() { MaximumVersions = 3 });
        var one = new TestBackupTransport(service, scope); var two = new TestBackupTransport(service, scope);
        await using var a = new TestDevice(scope, one); var code = await a.Backup.BeginEnableAsync(); await a.Backup.ConfirmRecoveryKeyAsync(code); await a.Backup.BackupAsync();
        await using var b = new TestDevice(scope, two); await b.Backup.UnlockAsync(code);
        var arrived = 0; var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Wait() { if (Interlocked.Increment(ref arrived) == 2) { barrier.SetResult(); } await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        one.BeforeCommit = Wait; two.BeforeCommit = Wait;
        await a.Events.StoreAsync(Event(ConversationId.New(), new ChatTextContent(ChatContentId.New(), "only A")));
        await b.Events.StoreAsync(Event(ConversationId.New(), new ChatTextContent(ChatContentId.New(), "only B")));
        await Task.WhenAll(a.Backup.BackupAsync().AsTask(), b.Backup.BackupAsync().AsTask());
        Assert.Equal(2, await a.Backup.RestoreAsync());
        Assert.Equal(ChatBackupFailure.Quota, (await Assert.ThrowsAsync<ChatBackupException>(() => a.Backup.BackupAsync().AsTask())).Failure);
        Assert.Equal(2, await a.Backup.RestoreAsync());
        var foreign = new TestBackupTransport(service, new(scope.ServiceId, UserId.New()));
        Assert.Null(await foreign.GetArchiveAsync());
        var archive = (await one.GetArchiveAsync())!;
        Assert.Equal(ChatBackupFailure.NotFound, (await Assert.ThrowsAsync<ChatBackupException>(() => foreign.GetHeadAsync(archive.ArchiveId).AsTask())).Failure);
        await Assert.ThrowsAsync<ChatBackupException>(() => service.TryCreateArchiveAsync(new("other-service", scope.UserId), archive).AsTask());
    }

    [Fact]
    public void Backup_key_checksum_canonical_bounds_context_and_trust_are_fail_closed()
    {
        using var key = ChatBackupRecoveryKey.Create(); var code = key.ExportRecoveryCode(); using var parsed = ChatBackupRecoveryKey.Parse(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal));
        Assert.Equal(key.ExportBytes(), parsed.ExportBytes());
        Assert.Throws<ChatBackupFormatException>(() => ChatBackupRecoveryKey.Parse(code[..^1] + (code[^1] == '0' ? '1' : '0')));
        var scope = new ChatBackupScope("test", UserId.New()); var archive = new ChatBackupArchive(scope, Guid.NewGuid(), Guid.NewGuid());
        var crypto = new ChatBackupCryptography(); var item = Event(ConversationId.New(), new ChatTextContent(ChatContentId.New(), "synthetic"));
        var bytes = ChatBackupEventEncoding.Encode(item); var part = crypto.Encrypt(key, archive, Guid.NewGuid(), 0, new byte[32], bytes);
        Assert.Equal(bytes, crypto.Decrypt(key, archive, part)); Assert.Throws<ChatBackupException>(() => crypto.Decrypt(key, new(new("other", scope.UserId), archive.ArchiveId, archive.KeyGeneration), part));
        var encoded = ChatBackupEncoding.EncodePart(part);
        for (var i = 0; i < encoded.Length; i++) { var length = i; Assert.Throws<ChatBackupFormatException>(() => ChatBackupEncoding.DecodePart(encoded.AsSpan(0, length))); }
        Assert.Throws<ChatBackupFormatException>(() => ChatBackupEncoding.DecodePart([.. encoded, 0]));
        var projection = new ChatConversationProjection(item.ConversationId); var restored = ChatBackupEventEncoding.Decode(bytes); projection.ApplyRestored(restored);
        Assert.True(projection.ContainsBackupHistory); projection.Apply(item); Assert.False(projection.ContainsBackupHistory);
        var forged = new RestoredChatContent(item.ConversationId, UserId.New(), DeviceId.New(), item.SentAt, item.Content);
        Assert.Equal(ChatProjectionApplyResult.Conflict, projection.ApplyRestored(forged)); Assert.False(projection.ContainsBackupHistory);
    }

    private static ReceivedChatContent Event(ConversationId conversation, ChatContent content) => new(MessageId.New(), conversation, UserId.New(), DeviceId.New(), Now, content);
    [Fact]
    public async Task Independent_workspace_writers_use_one_lease_and_immutable_rows_are_exactly_compared()
    {
        var directory = Path.Combine(Path.GetTempPath(), "skopka-backup-lease-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var scope = new DeviceIdentityScope("workspace", UserId.New(), Guid.NewGuid()); var connection = $"Data Source={Path.Combine(directory, "backup.db")};Pooling=false";
            await using var first = new SqliteBackupWorkspace(scope, connection); await using var second = new SqliteBackupWorkspace(scope, connection);
            var firstLease = await first.AcquireAsync();
            try
            {
                Assert.True(await first.WriteAsync("group", "key", new byte[] { 1, 2, 3 }));
                var waiting = second.AcquireAsync().AsTask(); Assert.False(waiting.IsCompleted);
                await firstLease.DisposeAsync(); firstLease = null!;
                await using var secondLease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(await second.WriteAsync("group", "key", new byte[] { 1, 2, 3 }));
                Assert.Equal(ChatBackupFailure.Conflict, (await Assert.ThrowsAsync<ChatBackupException>(() => second.WriteAsync("group", "key", new byte[] { 9 }).AsTask())).Failure);
                Assert.Equal(new byte[] { 1, 2, 3 }, await second.ReadAsync("group", "key"));
            }
            finally { if (firstLease is not null) { await firstLease.DisposeAsync(); } }
            await using var foreign = new SqliteBackupWorkspace(new(scope.ServiceId, UserId.New(), scope.InstallationId), connection);
            await using var foreignLease = await foreign.AcquireAsync(); Assert.Null(await foreign.ReadAsync("group", "key"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Retained_head_pin_rejects_valid_old_head_but_new_device_has_no_external_freshness_anchor()
    {
        var scope = new ChatBackupScope("rollback", UserId.New()); using var storage = new TestBackupStorage();
        var transport = new TestBackupTransport(new ChatBackupService(storage, TimeProvider.System), scope);
        await using var device = new TestDevice(scope, transport); var code = await device.Backup.BeginEnableAsync(); await device.Backup.ConfirmRecoveryKeyAsync(code);
        var old = await device.Backup.BackupAsync();
        await device.Events.StoreAsync(Event(ConversationId.New(), new ChatTextContent(ChatContentId.New(), "newer"))); await device.Backup.BackupAsync();
        Assert.Equal(1, await device.Backup.RestoreAsync()); transport.HeadOverride = old;
        Assert.Equal(ChatBackupFailure.Rollback, (await Assert.ThrowsAsync<ChatBackupException>(() => device.Backup.RestoreAsync().AsTask())).Failure);
        Assert.Single(await All(device.Backup.ReadRestoredAsync()));
        await using var fresh = new TestDevice(scope, transport); await fresh.Backup.UnlockAsync(code); Assert.Equal(0, await fresh.Backup.RestoreAsync());
    }
    [Fact]
    public async Task Pending_cleanup_never_expires_committed_ancestors_and_quota_is_atomic()
    {
        using var storage = new TestBackupStorage(); var clock = new MutableClock { Now = Now };
        var service = new ChatBackupService(storage, clock, new() { MaximumPendingUploads = 1, PendingLifetime = TimeSpan.FromMinutes(1), MaximumBytes = 66_000 });
        var scope = new ChatBackupScope("retention", UserId.New()); var archive = new ChatBackupArchive(scope, Guid.NewGuid(), Guid.NewGuid());
        await service.TryCreateArchiveAsync(scope, archive); using var key = ChatBackupRecoveryKey.Create(); var crypto = new ChatBackupCryptography();
        var id = Guid.NewGuid(); await service.BeginUploadAsync(scope, archive.ArchiveId, id);
        Assert.Equal(ChatBackupFailure.Quota, (await Assert.ThrowsAsync<ChatBackupException>(() => service.BeginUploadAsync(scope, archive.ArchiveId, Guid.NewGuid()).AsTask())).Failure);
        var original = Event(ConversationId.New(), new ChatTextContent(ChatContentId.New(), new string('a', 40_000)));
        var part = crypto.Encrypt(key, archive, id, 0, new byte[32], ChatBackupEventEncoding.Encode(original));
        var bytes = ChatBackupEncoding.EncodePart(part); await service.PutPartAsync(scope, archive.ArchiveId, part);
        var second = crypto.Encrypt(key, archive, id, 1, SHA256.HashData(bytes), ChatBackupEventEncoding.Encode(original));
        Assert.Equal(ChatBackupFailure.Quota, (await Assert.ThrowsAsync<ChatBackupException>(() => service.PutPartAsync(scope, archive.ArchiveId, second).AsTask())).Failure);
        var seal = crypto.Seal(key, archive, id, null, 1, bytes.Length, SHA256.HashData(bytes), Now);
        Assert.Equal(ChatBackupCommitResult.Committed, await service.CommitAsync(scope, seal));
        var pendingId = Guid.NewGuid(); await service.BeginUploadAsync(scope, archive.ArchiveId, pendingId);
        clock.Now = Now.AddSeconds(30); await service.BeginUploadAsync(scope, archive.ArchiveId, pendingId);
        clock.Now = Now.AddSeconds(61); await service.CleanupAsync(scope);
        Assert.Equal(id, (await service.GetHeadAsync(scope, archive.ArchiveId))!.VersionId);
        Assert.Equal(bytes, ChatBackupEncoding.EncodePart(await service.GetPartAsync(scope, archive.ArchiveId, id, 0)));
        Assert.DoesNotContain(storage.Data.Keys, item => item.Group == "pending");
    }

    [Fact]
    public async Task Write_failure_cancel_and_bounded_quota_preserve_previous_visible_snapshot()
    {
        var scope = new ChatBackupScope("failures", UserId.New()); using var storage = new TestBackupStorage();
        var transport = new TestBackupTransport(new ChatBackupService(storage, TimeProvider.System), scope);
        await using var source = new TestDevice(scope, transport);
        var conversation = ConversationId.New(); await source.Events.StoreAsync(Event(conversation, new ChatTextContent(ChatContentId.New(), "original")));
        var code = await source.Backup.BeginEnableAsync(); await source.Backup.ConfirmRecoveryKeyAsync(code); await source.Backup.BackupAsync();
        var faults = new TestWorkspaceFaults(); await using var destination = new TestDevice(scope, transport, faults);
        await destination.Backup.UnlockAsync(code); Assert.Equal(1, await destination.Backup.RestoreAsync());
        await source.Events.StoreAsync(Event(conversation, new ChatTextContent(ChatContentId.New(), new string('x', 40_000))));
        await source.Events.StoreAsync(Event(conversation, new ChatTextContent(ChatContentId.New(), new string('y', 40_000)))); await source.Backup.BackupAsync();
        faults.FailWrites = true; var error = await Assert.ThrowsAsync<ChatBackupException>(() => destination.Backup.RestoreAsync().AsTask());
        Assert.DoesNotContain("synthetic-provider-secret", error.ToString()); faults.FailWrites = false;
        Assert.Single(await All(destination.Backup.ReadRestoredAsync()));
        using var cancellation = new CancellationTokenSource();
        destination.Backup.Progress = new InlineProgress(status => { if (status.Phase == ChatBackupPhase.Restoring && status.ProcessedParts == 1) { cancellation.Cancel(); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destination.Backup.RestoreAsync(cancellation.Token).AsTask());
        Assert.Single(await All(destination.Backup.ReadRestoredAsync())); await destination.Reopen();
        Assert.Equal(3, await destination.Backup.RestoreAsync()); Assert.True(faults.LargestWrite <= ChatBackupLimits.MaxPartBytes); Assert.True(faults.LargestPage <= 100);
        await using var bounded = new TestDevice(scope, transport, options: new() { MaximumBytes = 66_000 });
        await bounded.Backup.UnlockAsync(code); Assert.Equal(ChatBackupFailure.Quota, (await Assert.ThrowsAsync<ChatBackupException>(() => bounded.Backup.RestoreAsync().AsTask())).Failure);
        Assert.Empty(await All(bounded.Backup.ReadRestoredAsync()));
    }
    private sealed class MutableClock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class InlineProgress(Action<ChatBackupStatus> action) : IProgress<ChatBackupStatus> { public void Report(ChatBackupStatus value) => action(value); }
    private static async Task<List<T>> All<T>(IAsyncEnumerable<T> source) { var result = new List<T>(); await foreach (var item in source) { result.Add(item); } return result; }
}

internal sealed class TestDevice : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "skopka-backup-test-" + Guid.NewGuid().ToString("N"));
    private readonly DeviceIdentityScope _scope;
    private readonly TestBackupTransport _transport;
    private readonly TestKeyData _key = new();
    private readonly TestWorkspaceFaults _faults;
    private readonly ChatBackupClientOptions? _options;
    public InMemoryChatEventStore Events { get; } = new();
    public ChatBackupCoordinator Backup { get; private set; }
    public TestDevice(ChatBackupScope scope, TestBackupTransport transport, TestWorkspaceFaults? faults = null, ChatBackupClientOptions? options = null)
    { _scope = new(scope.ServiceId, scope.UserId, Guid.NewGuid()); _transport = transport; _faults = faults ?? new(); _options = options; Directory.CreateDirectory(_directory); Backup = Create(); }
    private ChatBackupCoordinator Create() => new(new TestBackupKeyStore(_scope, _key), new TestWorkspace(new SqliteBackupWorkspace(_scope, $"Data Source={Path.Combine(_directory, "backup.db")};Pooling=false"), _faults), Events, _transport, new(), TimeProvider.System, _options);
    public async Task Reopen() { await Backup.DisposeAsync(); Backup = Create(); }
    public async ValueTask DisposeAsync() { await Backup.DisposeAsync(); if (_key.Bytes is not null) { CryptographicOperations.ZeroMemory(_key.Bytes); } Directory.Delete(_directory, true); }
}
internal sealed class TestWorkspaceFaults { public bool FailWrites { get; set; } public bool UnreadableRestoreRow { get; set; } public int LargestWrite { get; set; } public int LargestPage { get; set; } }
internal sealed class TestWorkspace(IChatBackupWorkspace inner, TestWorkspaceFaults faults) : IChatBackupWorkspace
{
    public DeviceIdentityScope Scope => inner.Scope;
    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => inner.AcquireAsync(cancellationToken);
    public ValueTask<byte[]?> ReadAsync(string group, string key, CancellationToken cancellationToken = default) =>
        faults.UnreadableRestoreRow && group.StartsWith("restore-", StringComparison.Ordinal) ? ValueTask.FromResult<byte[]?>(null) : inner.ReadAsync(group, key, cancellationToken);
    public ValueTask<bool> WriteAsync(string group, string key, ReadOnlyMemory<byte> data, bool replace = false, CancellationToken cancellationToken = default)
    { if (faults.FailWrites) { throw new IOException("synthetic-provider-secret"); } faults.LargestWrite = Math.Max(faults.LargestWrite, data.Length); return inner.WriteAsync(group, key, data, replace, cancellationToken); }
    public ValueTask<ChatBackupLocalPage> ReadPageAsync(string group, string? cursor = null, int maximumCount = 50, CancellationToken cancellationToken = default)
    { faults.LargestPage = Math.Max(faults.LargestPage, maximumCount); return inner.ReadPageAsync(group, cursor, maximumCount, cancellationToken); }
    public ValueTask DeleteAsync(string group, string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(group, key, cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
internal sealed class TestKeyData { public byte[]? Bytes { get; set; } }
internal sealed class TestBackupKeyStore(DeviceIdentityScope scope, TestKeyData data) : IChatBackupKeyStore
{
    public DeviceIdentityScope Scope => scope;
    public ValueTask<ChatBackupCredential?> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(data.Bytes is null ? null : ChatBackupCredential.DecodeProtectedStorage(data.Bytes, new(scope.ServiceId, scope.UserId)));
    public ValueTask<bool> TryCreateAsync(ChatBackupCredential credential, CancellationToken cancellationToken = default)
    { lock (data) { if (data.Bytes is not null) { return ValueTask.FromResult(false); } data.Bytes = credential.EncodeForProtectedStorage(); return ValueTask.FromResult(true); } }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
internal sealed class TestBackupTransport(ChatBackupService service, ChatBackupScope scope) : IChatBackupTransport
{
    public bool FailAfterPut { get; set; }
    public bool FailAfterCommit { get; set; }
    public int FailReadIndex { get; set; } = -1;
    public bool Tamper { get; set; }
    public ChatBackupVersion? HeadOverride { get; set; }
    public Func<Task>? BeforeCommit { get; set; }
    public ValueTask<ChatBackupArchive?> GetArchiveAsync(CancellationToken cancellationToken = default) => service.GetArchiveAsync(scope, cancellationToken);
    public ValueTask<bool> TryCreateArchiveAsync(ChatBackupArchive archive, CancellationToken cancellationToken = default) => service.TryCreateArchiveAsync(scope, archive, cancellationToken);
    public ValueTask<ChatBackupVersion?> GetHeadAsync(Guid archiveId, CancellationToken cancellationToken = default) => HeadOverride is null ? service.GetHeadAsync(scope, archiveId, cancellationToken) : ValueTask.FromResult<ChatBackupVersion?>(HeadOverride);
    public ValueTask BeginUploadAsync(Guid archiveId, Guid uploadId, CancellationToken cancellationToken = default) => service.BeginUploadAsync(scope, archiveId, uploadId, cancellationToken);
    public async ValueTask PutPartAsync(Guid archiveId, ChatBackupPart part, CancellationToken cancellationToken = default)
    { await service.PutPartAsync(scope, archiveId, part, cancellationToken); if (FailAfterPut) { FailAfterPut = false; throw new ChatBackupException(ChatBackupFailure.Unavailable); } }
    public async ValueTask<ChatBackupCommitResult> CommitAsync(ChatBackupVersion version, CancellationToken cancellationToken = default)
    {
        if (BeforeCommit is { } wait) { BeforeCommit = null; await wait(); }
        var result = await service.CommitAsync(scope, version, cancellationToken); if (FailAfterCommit) { FailAfterCommit = false; throw new ChatBackupException(ChatBackupFailure.Unavailable); }
        return result;
    }
    public ValueTask<ChatBackupVersion?> GetVersionAsync(Guid archiveId, Guid versionId, CancellationToken cancellationToken = default) => service.GetVersionAsync(scope, archiveId, versionId, cancellationToken);
    public async ValueTask<ChatBackupPart?> GetPartAsync(Guid archiveId, Guid uploadId, int index, CancellationToken cancellationToken = default)
    {
        if (index == FailReadIndex) { throw new ChatBackupException(ChatBackupFailure.Unavailable); }
        var part = await service.GetPartAsync(scope, archiveId, uploadId, index, cancellationToken);
        if (!Tamper) { return part; }
        var bytes = part.Ciphertext.ToArray(); bytes[0] ^= 1; return new(part.UploadId, part.Index, part.PreviousHash.Span, part.Nonce.Span, bytes);
    }
}

// Whole-state copies are deliberately confined to the test backend; production adapters page records within a database transaction.
internal sealed class TestBackupStorage : IChatBackupStorage, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Dictionary<(ChatBackupScope Scope, string Group, string Key), byte[]> Data { get; private set; } = [];
    public void Dispose() => _gate.Dispose();
    public async ValueTask<IChatBackupTransaction> BeginAsync(ChatBackupScope scope, CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken); return new Transaction(this, scope, new(Data)); }
    private sealed class Transaction(TestBackupStorage owner, ChatBackupScope scope, Dictionary<(ChatBackupScope Scope, string Group, string Key), byte[]> data) : IChatBackupTransaction
    {
        public ValueTask<byte[]?> ReadAsync(string group, string key, CancellationToken cancellationToken) => ValueTask.FromResult(data.GetValueOrDefault((scope, group, key))?.ToArray());
        public ValueTask WriteAsync(string group, string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) { data[(scope, group, key)] = bytes.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string group, string? after, int count, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(data.Keys.Where(key => key.Scope == scope && key.Group == group && string.CompareOrdinal(key.Key, after) > 0).Select(key => key.Key).Order(StringComparer.Ordinal).Take(count).ToArray());
        public ValueTask DeleteAsync(string group, string key, CancellationToken cancellationToken) { data.Remove((scope, group, key)); return ValueTask.CompletedTask; }
        public ValueTask CommitAsync(CancellationToken cancellationToken) { owner.Data = data; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { owner._gate.Release(); return ValueTask.CompletedTask; }
    }
}
