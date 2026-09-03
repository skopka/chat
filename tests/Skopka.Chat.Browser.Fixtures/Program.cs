using System.Security.Cryptography;
using System.Text.Json;
using Skopka.Chat.Browser.Testing;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

if (args.Length < 2) { throw new ArgumentException("Expected generate|verify and an artifact path."); }
var fixturePath = args[1];
if (args[0] == "generate")
{
    var keys = new InMemoryDeviceKeyStore();
    var identity = new DeviceIdentityService(keys);
    var time = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    var alice = await identity.CreateAsync(new UserId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")), DeviceId.New(), time);
    var bob = await identity.CreateAsync(new UserId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")), DeviceId.New(), time);
    var content = new ChatTextContent(ChatContentId.New(), "synthetic native/browser interop — 🔐");
    var envelope = await new ChatCryptoService(keys).EncryptContentAsync(content, ConversationId.New(), MessageId.New(), alice.DeviceId, bob, time);
    var aliceKeys = (await keys.LoadAsync(alice.DeviceId))!;
    var bobKeys = (await keys.LoadAsync(bob.DeviceId))!;
    var binding = new DeviceBindingChallenge(1, DeviceBindingOperation.Rebind,
        new DeviceAuthorizationContext("browser.test", bob.UserId, "synthetic-session", time.AddHours(1)), bob, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), time, time.AddMinutes(2));
    var canonical = DeviceBindingEncoding.Encode(binding);
    using var recovery = ChatBackupRecoveryKey.Create();
    var archive = new ChatBackupArchive(new("interop-backup", bob.UserId), Guid.NewGuid(), Guid.NewGuid());
    var backupCrypto = new ChatBackupCryptography();
    var backupPart = backupCrypto.Encrypt(recovery, archive, Guid.NewGuid(), 0, new byte[32], ChatBackupEventEncoding.Encode(new ReceivedChatContent(
        envelope.MessageId, envelope.ConversationId, alice.UserId, alice.DeviceId, time, content)));
    var backupBytes = ChatBackupEncoding.EncodePart(backupPart);
    var backupSeal = backupCrypto.Seal(recovery, archive, backupPart.UploadId, null, 1, backupBytes.Length, SHA256.HashData(backupBytes), time);
    var fixture = new InteropFixture(PublicDeviceResponse.FromDomain(alice), PublicDeviceResponse.FromDomain(bob),
        ConvertKey(aliceKeys.ExportEncryptionPrivateKey(), ChatKeyAlgorithm.X25519), ConvertKey(aliceKeys.ExportSigningPrivateKey(), ChatKeyAlgorithm.Ed25519),
        ConvertKey(bobKeys.ExportEncryptionPrivateKey(), ChatKeyAlgorithm.X25519), ConvertKey(bobKeys.ExportSigningPrivateKey(), ChatKeyAlgorithm.Ed25519),
        EncryptedEnvelopeDto.FromDomain(envelope), CanonicalEnvelopeEncoding.EncodeEnvelope(envelope), canonical,
        new NSecChatCryptography().Sign(bobKeys.ExportSigningPrivateKey(), canonical), recovery.ExportBytes(), ChatBackupEncoding.EncodeArchive(archive), backupBytes, ChatBackupEncoding.EncodeVersion(backupSeal));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
    await File.WriteAllBytesAsync(fixturePath, JsonSerializer.SerializeToUtf8Bytes(fixture, InteropJson.Default.InteropFixture));
    Console.WriteLine("Synthetic native interoperability vectors generated.");
}
else if (args[0] == "verify" && args.Length >= 3)
{
    var fixture = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(fixturePath), InteropJson.Default.InteropFixture)!;
    var result = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(args[2]), InteropJson.Default.InteropResult)!;
    var keys = new InMemoryDeviceKeyStore();
    var alice = fixture.Alice.ToDomain();
    await keys.TryCreateAsync(new DeviceKeyMaterial(alice.UserId, alice.DeviceId, alice.KeyId, fixture.AliceEncryption, fixture.AliceSigning));
    var decoded = await new ChatCryptoService(keys).DecryptContentAsync(result.Envelope.ToDomain(), fixture.Bob.ToDomain());
    using var recovery = ChatBackupRecoveryKey.FromBytes(fixture.BackupKey);
    var archive = ChatBackupEncoding.DecodeArchive(fixture.BackupArchive); var backupCrypto = new ChatBackupCryptography();
    backupCrypto.Verify(recovery, archive, ChatBackupEncoding.DecodeVersion(result.BackupSeal));
    var restored = ChatBackupEventEncoding.Decode(backupCrypto.Decrypt(recovery, archive, ChatBackupEncoding.DecodePart(result.BackupPart)));
    if (restored.Content is not ChatTextContent backupText || backupText.Text != "synthetic native/browser interop — 🔐") { throw new InvalidOperationException("Backup interoperability failed."); }
    if (decoded is not ChatTextContent text || text.Text != "synthetic browser/native interop — 🔐" ||
        !new NSecChatCryptography().Verify(fixture.Bob.SigningPublicKey, fixture.BindingCanonical, result.BindingSignature) ||
        !fixture.BindingSignature.AsSpan().SequenceEqual(result.BindingSignature))
    { throw new InvalidOperationException("Browser/native interoperability failed."); }
    Console.WriteLine("Native decryption and binding-v1 signature verification passed.");
}
else { throw new ArgumentException("Invalid fixture command."); }

static byte[] ConvertKey(byte[] key, ChatKeyAlgorithm algorithm)
{
    try { return NSecChatCryptography.ExportPortablePrivateKey(algorithm, key); }
    finally { CryptographicOperations.ZeroMemory(key); }
}
