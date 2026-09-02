using System.Collections.Concurrent;
using Microsoft.Maui.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Maui.Tests;

public sealed class PersistentIdentityTests
{
    [Fact]
    public async Task Independent_initializers_create_one_identity_and_relogin_retains_both_keys()
    {
        using var fixture = new Fixture();
        var attempts = Enumerable.Range(0, 12).Select(_ => fixture.Service().CreateAsync(fixture.Scope).AsTask()).ToArray();
        var results = await Task.WhenAll(attempts);
        Assert.All(results, result => Assert.Equal(PersistentDeviceIdentityState.Ready, result.State));
        Assert.Single(results.Select(result => result.Metadata!.DeviceId).Distinct());
        Assert.Equal(2, fixture.Storage.Values.Count);
        var original = results[0].Metadata!;
        var before = await fixture.Keys().LoadAsync(original.DeviceId);
        // Dispose of session services only. New service/key/lock instances simulate a later login/startup.
        var loaded = await fixture.Service().LoadAsync(fixture.Scope);
        var after = await fixture.Keys().LoadAsync(loaded.Metadata!.DeviceId);
        Assert.Equal(original.DeviceId, loaded.Metadata.DeviceId);
        Assert.Equal(before!.ExportSigningPrivateKey(), after!.ExportSigningPrivateKey());
        Assert.Equal(before.ExportEncryptionPrivateKey(), after.ExportEncryptionPrivateKey());
        Assert.Equal(original.Scope.StoragePartition, loaded.Metadata.Scope.StoragePartition);
    }

    [Fact]
    public async Task Identity_is_isolated_by_service_account_and_installation_not_session()
    {
        using var fixture = new Fixture();
        var scopes = new[] { fixture.Scope,
            new DeviceIdentityScope("different-service", fixture.Scope.UserId, fixture.Scope.InstallationId),
            new DeviceIdentityScope(fixture.Scope.ServiceId, UserId.New(), fixture.Scope.InstallationId),
            new DeviceIdentityScope(fixture.Scope.ServiceId, fixture.Scope.UserId, Guid.NewGuid()) };
        var devices = new List<DeviceId>();
        foreach (var scope in scopes)
        {
            var service = fixture.Service(scope);
            Assert.Equal(PersistentDeviceIdentityState.Absent, (await service.LoadAsync(scope)).State);
            devices.Add((await service.CreateAsync(scope)).Metadata!.DeviceId);
        }
        Assert.Equal(4, devices.Distinct().Count());
        Assert.Equal(8, fixture.Storage.Values.Count);
        Assert.Null(await fixture.Keys(scopes[1]).LoadAsync(devices[0]));
    }

    [Fact]
    public async Task Crash_after_keys_before_metadata_recovers_the_reserved_identity_without_replacement()
    {
        using var fixture = new Fixture();
        fixture.Storage.FailMetadataWriteNumber = 2;
        var interrupted = await fixture.Service().CreateAsync(fixture.Scope);
        Assert.Equal(PersistentDeviceIdentityState.Unavailable, interrupted.State);
        Assert.Equal(2, fixture.Storage.Values.Count);
        var keyBefore = fixture.Storage.Values.Single(pair => pair.Key.Contains(".keys.", StringComparison.Ordinal)).Value;
        fixture.Storage.FailMetadataWriteNumber = 0;
        var resumed = await fixture.Service().LoadAsync(fixture.Scope);
        Assert.Equal(PersistentDeviceIdentityState.Ready, resumed.State);
        Assert.Equal(keyBefore, fixture.Storage.Values.Single(pair => pair.Key.Contains(".keys.", StringComparison.Ordinal)).Value);
        Assert.Equal(resumed.Metadata!.DeviceId, (await fixture.Service().CreateAsync(fixture.Scope)).Metadata!.DeviceId);
    }

    [Fact]
    public async Task Reservation_without_keys_requires_recovery_even_on_explicit_create_retry()
    {
        using var fixture = new Fixture();
        fixture.Storage.FailKeyWrites = true;
        Assert.Equal(PersistentDeviceIdentityState.Unavailable, (await fixture.Service().CreateAsync(fixture.Scope)).State);
        fixture.Storage.FailKeyWrites = false;
        var loaded = await fixture.Service().LoadAsync(fixture.Scope);
        Assert.Equal(PersistentDeviceIdentityState.RecoveryRequired, loaded.State);
        var retry = await fixture.Service().CreateAsync(fixture.Scope);
        Assert.Equal(PersistentDeviceIdentityState.RecoveryRequired, retry.State);
        Assert.Equal(loaded.Metadata!.DeviceId, retry.Metadata!.DeviceId);
        Assert.Single(fixture.Storage.Values);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("unavailable")]
    public async Task Missing_corrupt_or_unavailable_keys_never_generate_a_replacement(string failure)
    {
        using var fixture = new Fixture();
        var identity = (await fixture.Service().CreateAsync(fixture.Scope)).Metadata!;
        var key = fixture.Storage.Values.Keys.Single(value => value.Contains(".keys.", StringComparison.Ordinal));
        if (failure == "missing") { fixture.Storage.Values.TryRemove(key, out _); }
        else if (failure == "corrupt") { fixture.Storage.Values[key] = "private-token-secret-invalid-record"; }
        else { fixture.Storage.FailReads = true; }
        var result = await fixture.Service().LoadAsync(fixture.Scope);
        Assert.Equal(failure switch
        {
            "missing" => PersistentDeviceIdentityState.RecoveryRequired,
            "corrupt" => PersistentDeviceIdentityState.Corrupt,
            _ => PersistentDeviceIdentityState.Unavailable
        }, result.State);
        Assert.DoesNotContain("private-token-secret", result.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(PersistentDeviceIdentityState.Ready, (await fixture.Service().CreateAsync(fixture.Scope)).State);
        Assert.NotEqual(Guid.Empty, identity.DeviceId.Value);
    }

    [Fact]
    public async Task Corrupt_metadata_is_not_absence_and_generic_errors_have_no_platform_details()
    {
        using var fixture = new Fixture();
        fixture.Storage.Values["skopka.chat.identity.v1." + fixture.Scope.StoragePartition] = "private-marker";
        Assert.Equal(PersistentDeviceIdentityState.Corrupt, (await fixture.Service().LoadAsync(fixture.Scope)).State);
        Assert.Equal(PersistentDeviceIdentityState.Corrupt, (await fixture.Service().CreateAsync(fixture.Scope)).State);
        fixture.Storage.FailReads = true;
        var error = await Assert.ThrowsAsync<DeviceKeyStorageException>(async () => await fixture.Keys().LoadAsync(DeviceId.New()));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_sid_shaped_device_can_be_explicitly_adopted_without_new_keys()
    {
        using var fixture = new Fixture();
        var legacyKeys = new SecureStorageDeviceKeyStore(fixture.Storage, fixture.Scope.UserId);
        var oldSessionId = DeviceId.New();
        var legacy = await new DeviceIdentityService(legacyKeys).CreateAsync(fixture.Scope.UserId, oldSessionId, fixture.Clock.GetUtcNow());
        var service = fixture.Service();
        var result = await service.ImportLegacyAsync(fixture.Scope, legacy, legacyKeys);
        Assert.Equal(PersistentDeviceIdentityState.Ready, result.State);
        Assert.Equal(oldSessionId, result.Metadata!.DeviceId);
        Assert.True(result.Metadata.Registered);
        Assert.True(DeviceBindingEncoding.SameKeys(legacy, (await service.LoadAsync(fixture.Scope)).Metadata!.PublicDevice!));
        Assert.NotNull(await legacyKeys.LoadAsync(oldSessionId));
        Assert.NotNull(await fixture.Keys().LoadAsync(oldSessionId));
        Assert.Null(await fixture.Keys(new DeviceIdentityScope("other-service", fixture.Scope.UserId,
            fixture.Scope.InstallationId)).LoadAsync(oldSessionId));
    }

    [Fact]
    public async Task Interrupted_import_can_only_resume_with_retained_matching_keys()
    {
        using var fixture = new Fixture();
        var source = new InMemoryDeviceKeyStore();
        var legacy = await new DeviceIdentityService(source).CreateAsync(fixture.Scope.UserId, DeviceId.New(), fixture.Clock.GetUtcNow());
        fixture.Storage.FailKeyWrites = true;
        Assert.Equal(PersistentDeviceIdentityState.Unavailable,
            (await fixture.Service().ImportLegacyAsync(fixture.Scope, legacy, source)).State);
        fixture.Storage.FailKeyWrites = false;
        Assert.Equal(PersistentDeviceIdentityState.RecoveryRequired, (await fixture.Service().CreateAsync(fixture.Scope)).State);
        Assert.Equal(PersistentDeviceIdentityState.RecoveryRequired,
            (await fixture.Service().ImportLegacyAsync(fixture.Scope, legacy, new InMemoryDeviceKeyStore())).State);
        var resumed = await fixture.Service().ImportLegacyAsync(fixture.Scope, legacy, source);
        Assert.Equal(PersistentDeviceIdentityState.Ready, resumed.State);
        Assert.Equal(legacy.DeviceId, resumed.Metadata!.DeviceId);
    }

    [Fact]
    public async Task Revocation_is_sticky_and_local_forgetting_is_explicit_and_not_remote_revocation()
    {
        using var fixture = new Fixture();
        var original = (await fixture.Service().CreateAsync(fixture.Scope)).Metadata!;
        await fixture.Service().RememberRevokedAsync(fixture.Scope);
        Assert.Equal(PersistentDeviceIdentityState.Revoked, (await fixture.Service().LoadAsync(fixture.Scope)).State);
        Assert.Equal(PersistentDeviceIdentityState.Revoked, (await fixture.Service().CreateAsync(fixture.Scope)).State);
        Assert.NotNull(await fixture.Keys().LoadAsync(original.DeviceId));
        await fixture.Service().ForgetLocalAsync(fixture.Scope);
        Assert.Empty(fixture.Storage.Values);
        Assert.Equal(PersistentDeviceIdentityState.Absent, (await fixture.Service().LoadAsync(fixture.Scope)).State);
        Assert.NotEqual(original.DeviceId, (await fixture.Service().CreateAsync(fixture.Scope)).Metadata!.DeviceId);
    }

    [Fact]
    public async Task Cancellation_during_secure_write_waits_for_completion_before_releasing_identity_lease()
    {
        using var fixture = new Fixture();
        fixture.Storage.PauseFirstWrite = true;
        using var cancellation = new CancellationTokenSource();
        var creating = fixture.Service().CreateAsync(fixture.Scope, cancellation.Token).AsTask();
        await fixture.Storage.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var other = fixture.Service().CreateAsync(fixture.Scope).AsTask();
        Assert.False(creating.IsCompleted);
        Assert.False(other.IsCompleted);
        fixture.Storage.AllowWrite.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await creating);
        Assert.Equal(PersistentDeviceIdentityState.RecoveryRequired, (await other).State);
        Assert.Single(fixture.Storage.Values);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "skopka-identity-tests-" + Guid.NewGuid().ToString("N"));
        public Storage Storage { get; } = new();
        public DeviceIdentityScope Scope { get; } = new("chat.example.test", UserId.New(), Guid.NewGuid());
        public TimeProvider Clock { get; } = new FixedClock();
        public FileIdentityStorageLock Lock() => new(_root);
        public SecureStorageDeviceKeyStore Keys(DeviceIdentityScope? scope = null) => new(Storage, scope ?? Scope, Lock());
        public PersistentDeviceIdentityService Service(DeviceIdentityScope? scope = null) =>
            new(Keys(scope), new SecureStorageDeviceIdentityStore(Storage, Lock()), Clock);
        public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class Storage : ISecureStorage
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public bool FailReads { get; set; }
        public bool FailKeyWrites { get; set; }
        public int FailMetadataWriteNumber { get; set; }
        public bool PauseFirstWrite { get; set; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _metadataWrites;
        public Task<string?> GetAsync(string key) => FailReads ? throw new IOException("private-marker") : Task.FromResult(Values.GetValueOrDefault(key));
        public async Task SetAsync(string key, string value)
        {
            if (key.Contains(".keys.", StringComparison.Ordinal) && FailKeyWrites) { throw new IOException("private-marker"); }
            if (key.Contains(".identity.", StringComparison.Ordinal) && Interlocked.Increment(ref _metadataWrites) == FailMetadataWriteNumber)
            {
                throw new IOException("private-marker");
            }
            if (PauseFirstWrite)
            {
                PauseFirstWrite = false;
                WriteStarted.TrySetResult();
                await AllowWrite.Task;
            }
            Values[key] = value;
        }
        public bool Remove(string key) => Values.TryRemove(key, out _);
        public void RemoveAll() => Values.Clear();
    }
}
