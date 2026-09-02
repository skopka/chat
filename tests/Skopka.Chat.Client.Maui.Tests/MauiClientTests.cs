using Microsoft.Maui.Storage;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Maui.Tests;

public sealed class MauiClientTests
{
    [Fact]
    public void Maui_client_package_preserves_platform_adapter_boundaries()
    {
        var references = typeof(SecureStorageDeviceKeyStore).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .ToArray();

        Assert.Contains("Skopka.Chat.Client", references);
        Assert.Contains("Skopka.Chat.Client.Storage", references);
        Assert.DoesNotContain("Skopka.Chat.Client.Http", references);
        Assert.DoesNotContain("Skopka.Chat.Client.Storage.Sqlite", references);
        Assert.DoesNotContain("Skopka.Chat.Media.FFmpeg", references);
        Assert.DoesNotContain("Skopka.Chat.Server", references);
    }

    [Fact]
    public async Task Secure_storage_key_store_roundtrips_and_isolates_user_and_device()
    {
        var storage = new FakeSecureStorage();
        var user = UserId.New();
        var device = DeviceId.New();
        var store = new SecureStorageDeviceKeyStore(storage, user);
        var identity = await new DeviceIdentityService(store).CreateAsync(
            user,
            device,
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));

        var loaded = await store.LoadAsync(device);
        var loadedPublic = await new DeviceIdentityService(store).LoadPublicAsync(
            user,
            device,
            identity.RegisteredAt);

        Assert.NotNull(loaded);
        Assert.Equal(identity.UserId, loaded.UserId);
        Assert.Equal(identity.DeviceId, loaded.DeviceId);
        Assert.Equal(identity.KeyId, loaded.KeyId);
        Assert.Equal(identity.EncryptionPublicKey.ToArray(), loadedPublic!.EncryptionPublicKey.ToArray());
        Assert.Equal(identity.SigningPublicKey.ToArray(), loadedPublic.SigningPublicKey.ToArray());
        Assert.Null(await store.LoadAsync(DeviceId.New()));
        Assert.Null(await new SecureStorageDeviceKeyStore(storage, UserId.New()).LoadAsync(device));
        Assert.Contains("PrivateKeys=[REDACTED]", loaded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secure_storage_absence_corruption_and_cancellation_fail_closed()
    {
        var storage = new FakeSecureStorage();
        var user = UserId.New();
        var device = DeviceId.New();
        var store = new SecureStorageDeviceKeyStore(storage, user);
        Assert.Null(await store.LoadAsync(device));

        storage.Values[$"skopka.chat.keys.v1.{user.Value:N}.{device.Value:N}"] = "remote-secret-not-base64";
        var corrupt = await Assert.ThrowsAsync<DeviceKeyStorageException>(async () => await store.LoadAsync(device));
        Assert.DoesNotContain("remote-secret", corrupt.ToString(), StringComparison.Ordinal);

        storage.BlockReads = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.LoadAsync(DeviceId.New(), cancellation.Token));
    }

    [Fact]
    public async Task Secure_storage_trust_store_never_auto_trusts_changed_key()
    {
        var storage = new FakeSecureStorage();
        var localUser = UserId.New();
        var remoteUser = UserId.New();
        var device = DeviceId.New();
        var store = new SecureStorageDeviceTrustStore(storage, localUser);
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var verified = Trust(remoteUser, device, 1, 1, ChatDeviceTrustState.Verified, now);
        await store.SaveAsync(verified);

        var loaded = await store.LoadAsync(remoteUser, device);
        var replacement = Device(remoteUser, device, 2, 2, now.AddMinutes(1));

        Assert.Equal(ChatDeviceTrustState.Verified, loaded!.State);
        Assert.Equal(ChatDeviceTrustState.Changed, ChatDeviceTrust.Evaluate(replacement, loaded));
        Assert.Equal(ChatDeviceTrustState.Verified, (await store.LoadAsync(remoteUser, device))!.State);
    }

    [Fact]
    public async Task Protected_decrypted_files_are_stream_bounded_and_cleaned_after_callbacks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skopka-maui-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = new MauiProtectedFileService(new NullFilePicker(), new FakeFileSystem(root));
            byte[]? observed = null;
            await service.UseDecryptedAsync(
                "../remote-name.txt",
                "text/plain",
                maximumBytes: 4,
                async (destination, token) => await destination.WriteAsync(new byte[] { 1, 2, 3, 4 }, token),
                async (file, token) =>
                {
                    Assert.Equal("remote-name.txt", file.FileName);
                    await using var source = await file.OpenReadAsync(token);
                    using var copy = new MemoryStream();
                    await source.CopyToAsync(copy, token);
                    observed = copy.ToArray();
                    Assert.Single(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
                });

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, observed);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            await Assert.ThrowsAsync<IOException>(async () =>
                await service.UseDecryptedAsync(
                    "file.bin",
                    "application/octet-stream",
                    maximumBytes: 3,
                    async (destination, token) =>
                        await destination.WriteAsync(new byte[] { 1, 2, 3, 4 }, token),
                    (_, _) => ValueTask.CompletedTask));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Lifecycle_coalesces_wakes_and_account_switch_cancels_old_session()
    {
        var firstTransport = new BlockingTransport();
        var first = await CreateSessionAsync(firstTransport);
        var secondTransport = new BlockingTransport();
        var second = await CreateSessionAsync(secondTransport);
        await using var sessions = new MauiChatSessionManager();

        await sessions.SwitchAsync(first);
        await first.Lifecycle.StartAsync();
        await firstTransport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Lifecycle.OnResume();
        first.Lifecycle.OnResume();

        var switching = sessions.SwitchAsync(second).AsTask();
        await firstTransport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await switching;

        Assert.Same(second, sessions.Current);
        Assert.Equal(1, firstTransport.MaximumConcurrentCalls);
        await second.Lifecycle.StartAsync();
        await secondTransport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sessions.LogoutAsync();
        await secondTransport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(sessions.Current);
    }

    [Fact]
    public async Task Lifecycle_retries_expected_failures_with_a_bounded_single_sync()
    {
        var transport = new TransientTransport(failures: 2);
        var user = UserId.New();
        var device = DeviceId.New();
        var keys = new InMemoryDeviceKeyStore();
        await new DeviceIdentityService(keys).CreateAsync(user, device, DateTimeOffset.UtcNow);
        var sync = new ChatSyncCoordinator(
            transport,
            new ChatCryptoService(keys),
            new InMemoryChatEventStore(),
            new ChatConversationProjectionRegistry(),
            device);
        await using var lifecycle = new MauiChatLifecycleCoordinator(
            sync,
            options: new MauiChatLifecycleOptions
            {
                MaximumAttempts = 3,
                InitialRetryDelay = TimeSpan.Zero,
                MaximumRetryDelay = TimeSpan.Zero,
            },
            nextJitter: static () => 0.5);

        await lifecycle.StartAsync();
        await transport.Succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, transport.Calls);
        Assert.Equal(1, transport.MaximumConcurrentCalls);
    }

    private static async Task<MauiChatSession> CreateSessionAsync(BlockingTransport transport)
    {
        var user = UserId.New();
        var device = DeviceId.New();
        var keys = new InMemoryDeviceKeyStore();
        await new DeviceIdentityService(keys).CreateAsync(user, device, DateTimeOffset.UtcNow);
        var sync = new ChatSyncCoordinator(
            transport,
            new ChatCryptoService(keys),
            new InMemoryChatEventStore(),
            new ChatConversationProjectionRegistry(),
            device);
        var lifecycle = new MauiChatLifecycleCoordinator(
            sync,
            options: new MauiChatLifecycleOptions { MaximumAttempts = 1 });
        return new MauiChatSession(new MauiChatSessionIdentity(user, device), lifecycle);
    }

    private static ChatDeviceTrustRecord Trust(
        UserId user,
        DeviceId device,
        byte keySeed,
        byte publicSeed,
        ChatDeviceTrustState state,
        DateTimeOffset at) => new(
            user,
            device,
            new KeyId(GuidFrom(keySeed)),
            Enumerable.Repeat(publicSeed, ProtocolLimits.X25519PublicKeyBytes).Select(static value => (byte)value).ToArray(),
            Enumerable.Repeat(publicSeed, ProtocolLimits.Ed25519PublicKeyBytes).Select(static value => (byte)value).ToArray(),
            state,
            at);

    private static PublicDevice Device(UserId user, DeviceId device, byte keySeed, byte publicSeed, DateTimeOffset at) => new(
        user,
        device,
        new KeyId(GuidFrom(keySeed)),
        Enumerable.Repeat(publicSeed, ProtocolLimits.X25519PublicKeyBytes).Select(static value => (byte)value).ToArray(),
        Enumerable.Repeat(publicSeed, ProtocolLimits.Ed25519PublicKeyBytes).Select(static value => (byte)value).ToArray(),
        at);

    private static Guid GuidFrom(byte value) => new($"00000000-0000-0000-0000-{value:X12}");

    private sealed class FakeSecureStorage : ISecureStorage
    {
        internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        internal bool BlockReads { get; set; }

        public Task<string?> GetAsync(string key) => BlockReads
            ? new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously).Task
            : Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public bool Remove(string key) => Values.Remove(key);
        public void RemoveAll() => Values.Clear();
    }

    private sealed class NullFilePicker : IFilePicker
    {
        public Task<FileResult?> PickAsync(PickOptions? options = null) => Task.FromResult<FileResult?>(null);
        public Task<IEnumerable<FileResult?>> PickMultipleAsync(PickOptions? options = null) =>
            Task.FromResult<IEnumerable<FileResult?>>([]);
    }

    private sealed class FakeFileSystem(string appDataDirectory) : IFileSystem
    {
        public string CacheDirectory => appDataDirectory;
        public string AppDataDirectory => appDataDirectory;
        public Task<Stream> OpenAppPackageFileAsync(string filename) => throw new NotSupportedException();
        public Task<bool> AppPackageFileExistsAsync(string filename) => Task.FromResult(false);
    }

    private sealed class BlockingTransport : IChatTransport
    {
        private int _concurrent;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int MaximumConcurrentCalls { get; private set; }

        public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PublicDevice?>(null);

        public ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(
            DeviceId recipientDeviceId,
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, concurrent);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<TransportDelivery>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public ValueTask AcknowledgeAsync(DeviceId recipientDeviceId, MessageId messageId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TransientTransport(int failures) : IChatTransport
    {
        private int _concurrent;
        internal int Calls { get; private set; }
        internal int MaximumConcurrentCalls { get; private set; }
        internal TaskCompletionSource Succeeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PublicDevice?>(null);

        public ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(
            DeviceId recipientDeviceId,
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, concurrent);
            try
            {
                Calls++;
                if (Calls <= failures)
                {
                    throw new HttpRequestException("Synthetic transient failure.");
                }

                Succeeded.TrySetResult();
                return ValueTask.FromResult<IReadOnlyList<TransportDelivery>>([]);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public ValueTask AcknowledgeAsync(
            DeviceId recipientDeviceId,
            MessageId messageId,
            DateTimeOffset acknowledgedAt,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
