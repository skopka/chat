using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.NSec;

namespace Skopka.Chat.Binding.Tests;

public sealed class BindingCoordinatorTests
{
    [Fact]
    public async Task Coordinator_loads_existing_identity_marks_registration_and_remembers_authenticated_revocation()
    {
        using var metadata = new Metadata();
        var keys = new InMemoryDeviceKeyStore();
        var clock = new BindingProtocolTests.Clock();
        var scope = new DeviceIdentityScope("example.test", UserId.New(), Guid.NewGuid());
        var identities = new PersistentDeviceIdentityService(keys, metadata, clock);
        var created = await identities.CreateAsync(scope);
        var store = new InMemoryServerStore();
        var server = new DeviceBindingService(store, store, new NSecDeviceProofVerifier(), clock);
        var first = new DeviceAuthorizationContext(scope.ServiceId, scope.UserId, "one", BindingProtocolTests.Now.AddHours(1));
        var transport = new Transport(server, first);
        var coordinator = new DeviceBindingCoordinator(identities, new DeviceBindingProofService(keys, clock), transport);
        await coordinator.BindAsync(scope, first, DeviceBindingOperation.Enrollment);
        Assert.True((await identities.LoadAsync(scope)).Metadata!.Registered);
        var second = new DeviceAuthorizationContext(scope.ServiceId, scope.UserId, "two", first.ExpiresAt);
        transport.Context = second;
        Assert.Equal(created.Metadata!.DeviceId, (await coordinator.BindAsync(scope, second, DeviceBindingOperation.Rebind)).Device.DeviceId);
        transport.Revoked = true;
        await Assert.ThrowsAsync<DeviceBindingRevokedException>(async () => await coordinator.BindAsync(scope, second, DeviceBindingOperation.Rebind));
        Assert.Equal(PersistentDeviceIdentityState.Revoked, (await identities.LoadAsync(scope)).State);
        Assert.NotNull(await keys.LoadAsync(created.Metadata.DeviceId));
    }

    [Fact]
    public async Task Coordinator_never_creates_keys_or_signs_a_challenge_for_an_unexpected_session()
    {
        using var metadata = new Metadata();
        var keys = new InMemoryDeviceKeyStore();
        var clock = new BindingProtocolTests.Clock();
        var scope = new DeviceIdentityScope("example.test", UserId.New(), Guid.NewGuid());
        var identities = new PersistentDeviceIdentityService(keys, metadata, clock);
        var store = new InMemoryServerStore();
        var server = new DeviceBindingService(store, store, new NSecDeviceProofVerifier(), clock);
        var account = new DeviceAuthorizationContext(scope.ServiceId, scope.UserId, "expected", BindingProtocolTests.Now.AddHours(1));
        var transport = new Transport(server, new DeviceAuthorizationContext(scope.ServiceId, scope.UserId, "other-session", account.ExpiresAt));
        var coordinator = new DeviceBindingCoordinator(identities, new DeviceBindingProofService(keys, clock), transport);
        var missing = await Assert.ThrowsAsync<DeviceIdentityStorageException>(async () => await coordinator.BindAsync(scope, account, DeviceBindingOperation.Enrollment));
        Assert.Equal(PersistentDeviceIdentityState.Absent, missing.State);
        Assert.Equal(0, transport.Issued);
        await identities.CreateAsync(scope);
        await Assert.ThrowsAsync<ChatCryptographicException>(async () => await coordinator.BindAsync(scope, account, DeviceBindingOperation.Enrollment));
        Assert.Equal(0, transport.Completed);
        Assert.False((await identities.LoadAsync(scope)).Metadata!.Registered);
    }

    private sealed class Transport(DeviceBindingService server, DeviceAuthorizationContext context) : IDeviceBindingTransport
    {
        public DeviceAuthorizationContext Context { get; set; } = context;
        public bool Revoked { get; set; }
        public int Issued { get; private set; }
        public int Completed { get; private set; }
        public ValueTask<DeviceBindingChallenge> IssueAsync(DeviceBindingOperation operation, PublicDevice device, CancellationToken cancellationToken = default)
        {
            if (Revoked) { throw new DeviceBindingRevokedException(); }
            Issued++;
            return server.IssueAsync(Context, operation, device, cancellationToken);
        }
        public ValueTask<DeviceSessionBinding> CompleteAsync(DeviceBindingProof proof, CancellationToken cancellationToken = default)
        { Completed++; return server.CompleteAsync(Context, proof, cancellationToken); }
    }

    private sealed class Metadata : IDeviceIdentityMetadataStore, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1);
        private DeviceIdentityMetadata? _record;
        public async ValueTask<IDeviceIdentityLease> AcquireAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
        { await _gate.WaitAsync(cancellationToken); return new Lease(this); }
        public void Dispose() => _gate.Dispose();
        private sealed class Lease(Metadata owner) : IDeviceIdentityLease
        {
            public ValueTask<DeviceIdentityMetadata?> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(owner._record);
            public ValueTask WriteAsync(DeviceIdentityMetadata metadata, CancellationToken cancellationToken = default)
            { owner._record = metadata; return ValueTask.CompletedTask; }
            public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
            { owner._record = null; return ValueTask.CompletedTask; }
            public ValueTask DisposeAsync() { owner._gate.Release(); return ValueTask.CompletedTask; }
        }
    }
}
