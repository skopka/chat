using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Tests;

public sealed class CryptographyProviderTests
{
    [Fact]
    public void Explicit_portable_export_preserves_legacy_public_keys_and_signatures()
    {
        var crypto = new NSecChatCryptography();
        foreach (var algorithm in new[] { ChatKeyAlgorithm.X25519, ChatKeyAlgorithm.Ed25519 })
        {
            var legacy = crypto.CreatePrivateKey(algorithm);
            var portable = NSecChatCryptography.ExportPortablePrivateKey(algorithm, legacy);
            try
            {
                Assert.False(PortableChatPrivateKey.IsPortable(legacy));
                Assert.True(PortableChatPrivateKey.IsPortable(portable));
                Assert.Equal(crypto.GetPublicKey(algorithm, legacy), crypto.GetPublicKey(algorithm, portable));
                if (algorithm == ChatKeyAlgorithm.Ed25519)
                {
                    Assert.Equal(crypto.Sign(legacy, "synthetic canonical bytes"u8), crypto.Sign(portable, "synthetic canonical bytes"u8));
                }
            }
            finally { CryptographicOperations.ZeroMemory(legacy); CryptographicOperations.ZeroMemory(portable); }
        }
    }

    [Fact]
    public void Portable_container_rejects_wrong_purpose_unknown_version_and_truncation()
    {
        var key = PortableChatPrivateKey.Encode(ChatKeyAlgorithm.X25519, new byte[32]);
        Assert.Throws<ChatCryptographicException>(() => PortableChatPrivateKey.Decode(ChatKeyAlgorithm.Ed25519, key));
        Assert.Throws<ChatCryptographicException>(() => PortableChatPrivateKey.Decode(ChatKeyAlgorithm.X25519, key.AsSpan(0, key.Length - 1)));
        key[key.Length - 34] = 99;
        Assert.Throws<ChatCryptographicException>(() => PortableChatPrivateKey.Decode(ChatKeyAlgorithm.X25519, key));
    }

    [Fact]
    public async Task Existing_native_api_reads_explicitly_migrated_keys_without_identity_change()
    {
        var native = new InMemoryDeviceKeyStore();
        var portable = new InMemoryDeviceKeyStore();
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var device = await new DeviceIdentityService(native).CreateAsync(UserId.New(), DeviceId.New(), now);
        var original = (await native.LoadAsync(device.DeviceId))!;
        var encryption = NSecChatCryptography.ExportPortablePrivateKey(ChatKeyAlgorithm.X25519, original.ExportEncryptionPrivateKey());
        var signing = NSecChatCryptography.ExportPortablePrivateKey(ChatKeyAlgorithm.Ed25519, original.ExportSigningPrivateKey());
        try
        {
            await portable.TryCreateAsync(new DeviceKeyMaterial(device.UserId, device.DeviceId, device.KeyId, encryption, signing));
            var loaded = await new DeviceIdentityService(portable).LoadPublicAsync(device.UserId, device.DeviceId, now);
            Assert.NotNull(loaded);
            Assert.True(DeviceBindingEncoding.SameKeys(device, loaded));
            var sender = await new DeviceIdentityService(native).CreateAsync(UserId.New(), DeviceId.New(), now);
            var envelope = await new ChatCryptoService(native).EncryptTextAsync("synthetic key migration", ConversationId.New(), MessageId.New(), sender.DeviceId, device, now);
            Assert.Equal("synthetic key migration"u8.ToArray(), await new ChatCryptoService(portable).DecryptAsync(envelope, sender));
        }
        finally { CryptographicOperations.ZeroMemory(encryption); CryptographicOperations.ZeroMemory(signing); }
    }
}
