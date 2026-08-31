using System.Text;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Tests;

public sealed class ClientCryptoTests
{
    [Fact]
    public async Task Alice_encrypts_and_Bob_decrypts()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.AliceCrypto.EncryptTextAsync(
            "hello Bob",
            fixture.ConversationId,
            MessageId.New(),
            fixture.Alice.DeviceId,
            fixture.Bob,
            fixture.Now);

        var plaintext = await fixture.BobCrypto.DecryptAsync(envelope, fixture.Alice);

        Assert.Equal("hello Bob", Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public async Task Wrong_recipient_cannot_decrypt()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.AliceCrypto.EncryptTextAsync(
            "recipient secret",
            fixture.ConversationId,
            MessageId.New(),
            fixture.Alice.DeviceId,
            fixture.Bob,
            fixture.Now);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.CharlieCrypto.DecryptAsync(envelope, fixture.Alice));
    }

    [Fact]
    public async Task Ciphertext_change_is_detected()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync("tamper target");
        var ciphertext = envelope.Ciphertext.ToArray();
        ciphertext[0] ^= 0x01;

        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await fixture.BobCrypto.DecryptAsync(Clone(envelope, ciphertext: ciphertext), fixture.Alice));
    }

    [Fact]
    public async Task Header_change_is_detected()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync("tamper target");

        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await fixture.BobCrypto.DecryptAsync(Clone(envelope, conversationId: ConversationId.New()), fixture.Alice));
    }

    [Fact]
    public async Task Signature_change_is_detected()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync("tamper target");
        var signature = envelope.Signature.ToArray();
        signature[^1] ^= 0x80;

        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await fixture.BobCrypto.DecryptAsync(Clone(envelope, signature: signature), fixture.Alice));
    }

    [Fact]
    public async Task Repeated_delivery_creates_one_local_message()
    {
        var fixture = await CryptoFixture.CreateAsync();
        var envelope = await fixture.CreateEnvelopeAsync("deliver once");
        var local = new InMemoryReceivedMessageStore();
        var receiver = new ChatReceiver(fixture.BobCrypto, local);

        var first = await receiver.ReceiveAsync(envelope, fixture.Alice);
        var second = await receiver.ReceiveAsync(envelope, fixture.Alice);

        Assert.True(first.Added);
        Assert.False(second.Added);
        Assert.Equal(1, local.Count);
    }

    [Fact]
    public async Task Oversized_plaintext_is_rejected_before_encryption()
    {
        var fixture = await CryptoFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await fixture.AliceCrypto.EncryptAsync(
                new byte[ProtocolLimits.MaxPlaintextBytes + 1],
                fixture.ConversationId,
                MessageId.New(),
                fixture.Alice.DeviceId,
                fixture.Bob,
                fixture.Now));
    }

    [Fact]
    public async Task Security_code_is_order_independent_and_key_specific()
    {
        var fixture = await CryptoFixture.CreateAsync();

        Assert.Equal(SecurityCodes.Between(fixture.Alice, fixture.Bob), SecurityCodes.Between(fixture.Bob, fixture.Alice));
        Assert.NotEqual(SecurityCodes.Between(fixture.Alice, fixture.Bob), SecurityCodes.Between(fixture.Alice, fixture.Charlie));
    }

    private static EncryptedEnvelope Clone(
        EncryptedEnvelope source,
        ConversationId? conversationId = null,
        byte[]? ciphertext = null,
        byte[]? signature = null) => new(
        source.ProtocolVersion,
        source.MessageId,
        conversationId ?? source.ConversationId,
        source.SenderDeviceId,
        source.RecipientDeviceId,
        source.SenderSigningKeyId,
        source.RecipientEncryptionKeyId,
        source.SentAt,
        source.ExpiresAt,
        source.EphemeralPublicKey.Span,
        source.Nonce.Span,
        ciphertext ?? source.Ciphertext.ToArray(),
        source.AuthenticationTag.Span,
        signature ?? source.Signature.ToArray());

    private sealed class CryptoFixture
    {
        private CryptoFixture(
            PublicDevice alice,
            PublicDevice bob,
            PublicDevice charlie,
            ChatCryptoService aliceCrypto,
            ChatCryptoService bobCrypto,
            ChatCryptoService charlieCrypto)
        {
            Alice = alice;
            Bob = bob;
            Charlie = charlie;
            AliceCrypto = aliceCrypto;
            BobCrypto = bobCrypto;
            CharlieCrypto = charlieCrypto;
        }

        public DateTimeOffset Now { get; } = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        public ConversationId ConversationId { get; } = ConversationId.New();
        public PublicDevice Alice { get; }
        public PublicDevice Bob { get; }
        public PublicDevice Charlie { get; }
        public ChatCryptoService AliceCrypto { get; }
        public ChatCryptoService BobCrypto { get; }
        public ChatCryptoService CharlieCrypto { get; }

        public static async Task<CryptoFixture> CreateAsync()
        {
            var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
            var aliceStore = new InMemoryDeviceKeyStore();
            var bobStore = new InMemoryDeviceKeyStore();
            var charlieStore = new InMemoryDeviceKeyStore();
            var alice = await new DeviceIdentityService(aliceStore).CreateAsync(UserId.New(), DeviceId.New(), now);
            var bob = await new DeviceIdentityService(bobStore).CreateAsync(UserId.New(), DeviceId.New(), now);
            var charlie = await new DeviceIdentityService(charlieStore).CreateAsync(UserId.New(), DeviceId.New(), now);
            return new CryptoFixture(
                alice,
                bob,
                charlie,
                new ChatCryptoService(aliceStore),
                new ChatCryptoService(bobStore),
                new ChatCryptoService(charlieStore));
        }

        public ValueTask<EncryptedEnvelope> CreateEnvelopeAsync(string plaintext) =>
            AliceCrypto.EncryptTextAsync(plaintext, ConversationId, MessageId.New(), Alice.DeviceId, Bob, Now);
    }
}
