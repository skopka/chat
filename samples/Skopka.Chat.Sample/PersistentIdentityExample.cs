using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.NSec;

namespace Skopka.Chat.Sample;

internal static class PersistentIdentityExample
{
    internal static async Task RunAsync()
    {
        // Synthetic in-process demonstration, not production storage/authentication.
        var scope = new DeviceIdentityScope("sample.example.test", UserId.New(), Guid.NewGuid());
        var keys = new InMemoryDeviceKeyStore();
        using var metadata = new SampleMetadata();
        var identities = new PersistentDeviceIdentityService(keys, metadata, TimeProvider.System);
        var created = await identities.CreateAsync(scope); // explicit first-use choice
        var store = new InMemoryServerStore();
        var server = new DeviceBindingService(store, store, new NSecDeviceProofVerifier(), TimeProvider.System);
        var deadline = TimeProvider.System.GetUtcNow().AddHours(1);
        var first = new DeviceAuthorizationContext(scope.ServiceId, scope.UserId, "synthetic-login-one", deadline);
        var coordinator = new DeviceBindingCoordinator(identities, new DeviceBindingProofService(keys, TimeProvider.System),
            new InProcessBootstrap(server, first));
        await coordinator.BindAsync(scope, first, DeviceBindingOperation.Enrollment);
        // Logout drops session services, not keys, metadata, SQLite history or outbox.
        var second = new DeviceAuthorizationContext(scope.ServiceId, scope.UserId, "synthetic-login-two", deadline);
        coordinator = new DeviceBindingCoordinator(new PersistentDeviceIdentityService(keys, metadata, TimeProvider.System),
            new DeviceBindingProofService(keys, TimeProvider.System), new InProcessBootstrap(server, second));
        var rebound = await coordinator.BindAsync(scope, second, DeviceBindingOperation.Rebind);
        if (rebound.Device.DeviceId != created.Metadata!.DeviceId) { throw new InvalidOperationException("Identity changed."); }
        Console.WriteLine("New login proved ownership of the same persistent device; no token was created by this example.");
    }

    private sealed class InProcessBootstrap(DeviceBindingService server, DeviceAuthorizationContext context) : IDeviceBindingTransport
    {
        public ValueTask<DeviceBindingChallenge> IssueAsync(DeviceBindingOperation operation, PublicDevice device, CancellationToken cancellationToken = default) =>
            server.IssueAsync(context, operation, device, cancellationToken);
        public ValueTask<DeviceSessionBinding> CompleteAsync(DeviceBindingProof proof, CancellationToken cancellationToken = default) =>
            server.CompleteAsync(context, proof, cancellationToken);
    }

    // Test/sample only. Real hosts implement atomic protected persistence or select the MAUI adapter.
    private sealed class SampleMetadata : IDeviceIdentityMetadataStore, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1);
        private readonly Dictionary<string, DeviceIdentityMetadata> _records = new(StringComparer.Ordinal);
        public async ValueTask<IDeviceIdentityLease> AcquireAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, scope.StoragePartition);
        }
        public void Dispose() => _gate.Dispose();
        private sealed class Lease(SampleMetadata owner, string partition) : IDeviceIdentityLease
        {
            public ValueTask<DeviceIdentityMetadata?> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(owner._records.GetValueOrDefault(partition));
            public ValueTask WriteAsync(DeviceIdentityMetadata metadata, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); owner._records[partition] = metadata; return ValueTask.CompletedTask; }
            public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); owner._records.Remove(partition); return ValueTask.CompletedTask; }
            public ValueTask DisposeAsync() { owner._gate.Release(); return ValueTask.CompletedTask; }
        }
    }
}
