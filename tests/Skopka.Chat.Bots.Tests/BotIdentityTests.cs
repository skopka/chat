using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Skopka.Chat.Bots.AspNetCore;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots.Tests;

public sealed class BotIdentityTests
{
    [Fact]
    public async Task Protected_identity_survives_independent_writers_and_new_key_ring_provider()
    {
        var directory = Directory.CreateTempSubdirectory("skopka-bot-identity-").FullName;
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic-bot-storage", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(BotFixture.Now.AddDays(-1), BotFixture.Now.AddDays(1));
            var ring = new DirectoryInfo(Path.Combine(directory, "ring"));
            IDataProtectionProvider Protection() => DataProtectionProvider.Create(ring, options =>
                options.SetApplicationName("synthetic-bot").ProtectKeysWithCertificate(certificate));
            var scope = new DeviceIdentityScope("synthetic.example", UserId.New(), Guid.NewGuid());
            ProtectedFileBotIdentityStore Store(IDataProtectionProvider provider) => new(Path.Combine(directory, "identity"), scope, provider);
            var protection = Protection();
            var states = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                var store = Store(protection);
                return await new PersistentDeviceIdentityService(store, store, TimeProvider.System).CreateAsync(scope);
            })));
            Assert.All(states, state => Assert.Equal(PersistentDeviceIdentityState.Ready, state.State));
            var device = states[0].Metadata!.PublicDevice!;
            Assert.All(states, state => Assert.Equal(device.DeviceId, state.Metadata!.DeviceId));
            var reopened = Store(Protection());
            var loaded = await new PersistentDeviceIdentityService(reopened, reopened, TimeProvider.System).LoadAsync(scope);
            Assert.Equal(device.DeviceId, loaded.Metadata!.DeviceId);
            var material = (await reopened.LoadAsync(device.DeviceId))!;
            var privateKey = material.ExportEncryptionPrivateKey();
            try
            {
                var protectedBytes = await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*.keys", SearchOption.AllDirectories).Single());
                Assert.True(protectedBytes.AsSpan().IndexOf(privateKey) < 0);
            }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
            Assert.False(await reopened.TryCreateAsync(material));
            await Assert.ThrowsAsync<NotSupportedException>(() => reopened.SaveAsync(material).AsTask());
            await reopened.DeleteAsync(device.DeviceId);
            var missing = await new PersistentDeviceIdentityService(reopened, reopened, TimeProvider.System).CreateAsync(scope);
            Assert.Equal(PersistentDeviceIdentityState.RecoveryRequired, missing.State);
            Assert.Equal(device.DeviceId, missing.Metadata!.DeviceId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Corruption_revocation_and_wrong_protection_do_not_regenerate_identity()
    {
        var directory = Directory.CreateTempSubdirectory("skopka-bot-corrupt-").FullName;
        try
        {
            var scope = new DeviceIdentityScope("synthetic.example", UserId.New(), Guid.NewGuid());
            var protection = new EphemeralDataProtectionProvider();
            var store = new ProtectedFileBotIdentityStore(directory, scope, protection);
            var service = new PersistentDeviceIdentityService(store, store, TimeProvider.System);
            var created = await service.CreateAsync(scope);
            var wrong = new ProtectedFileBotIdentityStore(directory, scope, new EphemeralDataProtectionProvider());
            Assert.Equal(PersistentDeviceIdentityState.Corrupt, (await new PersistentDeviceIdentityService(wrong, wrong, TimeProvider.System).CreateAsync(scope)).State);
            await service.RememberRevokedAsync(scope);
            Assert.Equal(PersistentDeviceIdentityState.Revoked, (await service.CreateAsync(scope)).State);
            var file = Directory.GetFiles(directory, "identity.metadata", SearchOption.AllDirectories).Single();
            await File.WriteAllBytesAsync(file, [0, 1, 2, 3]);
            Assert.Equal(PersistentDeviceIdentityState.Corrupt, (await service.CreateAsync(scope)).State);
            Assert.NotNull(await store.LoadAsync(created.Metadata!.DeviceId));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireAsync(new("other.example", scope.UserId, scope.InstallationId)).AsTask());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Interrupted_metadata_reservation_without_keys_requires_recovery()
    {
        var directory = Directory.CreateTempSubdirectory("skopka-bot-reservation-").FullName;
        try
        {
            var scope = new DeviceIdentityScope("synthetic.example", UserId.New(), Guid.NewGuid());
            var protection = new EphemeralDataProtectionProvider();
            var store = new ProtectedFileBotIdentityStore(directory, scope, protection);
            var reserved = new DeviceIdentityMetadata(1, scope, DeviceId.New(), KeyId.New(), BotFixture.Now, null, false, false);
            await using (var lease = await store.AcquireAsync(scope)) { await lease.WriteAsync(reserved); }
            var encoded = await File.ReadAllBytesAsync(Path.Combine(directory, scope.StoragePartition, "identity.metadata"));
            using var document = System.Text.Json.JsonDocument.Parse(protection.CreateProtector("Skopka.Chat.BotIdentity.v1", scope.StoragePartition)
                .CreateProtector("identity.metadata").Unprotect(encoded));
            Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("Encryption").ValueKind);
            await using (var lease = await store.AcquireAsync(scope)) { Assert.Equal(reserved, await lease.ReadAsync()); }
            var result = await new PersistentDeviceIdentityService(store, store, TimeProvider.System).CreateAsync(scope);
            Assert.Equal(PersistentDeviceIdentityState.RecoveryRequired, result.State);
            Assert.Equal(reserved.DeviceId, result.Metadata!.DeviceId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
