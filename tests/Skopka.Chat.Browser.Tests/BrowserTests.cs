using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Browser;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Browser.Testing;

public sealed class BrowserTests(IJSRuntime js, Uri origin)
{
    private static readonly UserId User = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ConversationId Conversation = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new TestClock();
    private string _phase = "start";

    [JSInvokable]
    public async Task<string> Run(string action)
    {
        _phase = action;
        try
        {
            await using var crypto = await BrowserChatCryptography.CreateAsync(js);
            return action switch
            {
                "crypto" => await CryptoAsync(crypto),
                "bff" => await BffAsync(),
                "prepare" => await PrepareAsync(),
                "identity" => await IdentityAsync(crypto),
                "storage" => await StorageAsync(crypto),
                "event-race" => await ConcurrentEventAsync(crypto),
                "partial" => await PartialAsync(crypto, false),
                "retry" => await PartialAsync(crypto, true),
                "race-retry" => await PartialAsync(crypto, true, true),
                "no-ack" => await NoAckAsync(crypto),
                "quota-no-ack" => await QuotaNoAckAsync(crypto),
                "revoke" => await RevokeAsync(crypto),
                "load" => await LoadStateAsync(crypto),
                "probe" => await ProbeAsync(crypto),
                "pause-create" => await PauseCreateAsync(crypto, false),
                "pause-finalize" => await PauseCreateAsync(crypto, true),
                _ => throw new InvalidOperationException("Unknown test action.")
            };
        }
        catch (Exception error) { return "FAIL:" + _phase + ":" + error.GetType().Name; }
    }

    private static async Task<string> BffAsync()
    {
        var calls = 0;
        var csrf = new TestCsrf(request => { calls++; request.Headers.Add("X-CSRF", "synthetic-proof"); });
        var adapter = new BrowserBffAuthorization(new Uri("https://chat.example.test/"), csrf);
        foreach (var uri in new[] { "https://other.example.test/", "http://chat.example.test/", "https://chat.example.test:444/", "https://user:synthetic@chat.example.test/" })
        {
            using var invalid = new HttpRequestMessage(HttpMethod.Post, uri);
            await RejectBffAsync(adapter, invalid);
        }
        Check(calls == 0);
        using var bearer = new HttpRequestMessage(HttpMethod.Get, "https://chat.example.test/");
        bearer.Headers.Authorization = new("Bearer", "synthetic-token");
        await RejectBffAsync(adapter, bearer);
        using var valid = new HttpRequestMessage(HttpMethod.Post, "https://chat.example.test/skopka-chat/v1/envelopes");
        await adapter.AuthorizeAsync(valid);
        Check(calls == 1 && valid.Headers.Authorization is null && valid.Headers.Contains("X-CSRF"));
        using var changed = new HttpRequestMessage(HttpMethod.Post, "https://chat.example.test/");
        await RejectBffAsync(new BrowserBffAuthorization(new Uri("https://chat.example.test/"),
            new TestCsrf(request => request.RequestUri = new Uri("https://other.example.test/"))), changed);
        return "ok";
    }

    private static async Task RejectBffAsync(BrowserBffAuthorization adapter, HttpRequestMessage request)
    {
        try { await adapter.AuthorizeAsync(request); }
        catch (ChatHttpTransportException error) { Check(error.InnerException is null && !error.ToString().Contains("synthetic", StringComparison.Ordinal)); return; }
        throw new InvalidOperationException("Invalid BFF request was accepted.");
    }

    private async Task<string> CryptoAsync(BrowserChatCryptography provider)
    {
        using var http = new HttpClient { BaseAddress = origin };
        var fixture = JsonSerializer.Deserialize(await http.GetByteArrayAsync("test-vectors.json"), InteropJson.Default.InteropFixture)!;
        var bob = fixture.Bob.ToDomain();
        var alice = fixture.Alice.ToDomain();
        var keys = new InMemoryDeviceKeyStore();
        await keys.TryCreateAsync(new DeviceKeyMaterial(bob.UserId, bob.DeviceId, bob.KeyId, fixture.BobEncryption, fixture.BobSigning));
        var engine = new ChatCryptoService(keys, provider);
        _phase = "native-decrypt";
        var content = await engine.DecryptContentAsync(fixture.Envelope.ToDomain(), alice);
        Check(content is ChatTextContent text && text.Text == "synthetic native/browser interop — 🔐");
        Check(CanonicalEnvelopeEncoding.EncodeEnvelope(fixture.Envelope.ToDomain()).AsSpan().SequenceEqual(fixture.EnvelopeCanonical));
        _phase = "binding-golden";
        var binding = DeviceBindingEncoding.Decode(fixture.BindingCanonical);
        Check(DeviceBindingEncoding.Encode(binding).AsSpan().SequenceEqual(fixture.BindingCanonical));
        var expected = new DeviceAuthorizationContext("browser.test", bob.UserId, "synthetic-session", Now.AddHours(1));
        var proof = await new DeviceBindingProofService(keys, Clock, provider).CreateProofAsync(binding, expected, bob, DeviceBindingOperation.Rebind);
        Check(proof.Signature.Span.SequenceEqual(fixture.BindingSignature));
        Check(provider.Verify(bob.SigningPublicKey.Span, fixture.BindingCanonical, proof.Signature.Span));
        await RejectAsync(async () => await new DeviceBindingProofService(keys, Clock, provider).CreateProofAsync(binding,
            new DeviceAuthorizationContext("other.service", bob.UserId, "synthetic-session", Now.AddHours(1)), bob, DeviceBindingOperation.Rebind));
        _phase = "tamper";
        var corrupted = fixture.Envelope.Ciphertext.ToArray();
        corrupted[0] ^= 1;
        await RejectAsync(async () => await engine.DecryptAsync((fixture.Envelope with { Ciphertext = corrupted }).ToDomain(), alice));
        await RejectAsync(async () => await engine.DecryptAsync((fixture.Envelope with { Signature = new byte[64] }).ToDomain(), alice));
        _phase = "browser-encrypt";
        var envelope = await engine.EncryptContentAsync(new ChatTextContent(ChatContentId.New(), "synthetic browser/native interop — 🔐"),
            Conversation, MessageId.New(), bob.DeviceId, alice, Now);
        return JsonSerializer.Serialize(new InteropResult(EncryptedEnvelopeDto.FromDomain(envelope), proof.Signature.ToArray()), InteropJson.Default.InteropResult);
    }

    private async Task<string> PrepareAsync()
    {
        await using var vault = await OpenAsync("browser-tests", true);
        return "ok:" + vault.Scope.StoragePartition;
    }
    private async Task<string> IdentityAsync(BrowserChatCryptography crypto)
    {
        await using var vault = await OpenAsync("browser-tests");
        var store = new BrowserDeviceIdentityStore(vault);
        var identities = new PersistentDeviceIdentityService(store, store, Clock, crypto);
        var created = await identities.CreateAsync(vault.Scope);
        _phase = "identity-create-" + created.State;
        Check(created.State == PersistentDeviceIdentityState.Ready);
        var again = await identities.LoadAsync(vault.Scope);
        Check(again.State == PersistentDeviceIdentityState.Ready && DeviceBindingEncoding.SameKeys(created.Metadata!.PublicDevice!, again.Metadata!.PublicDevice!));
        return "ok:" + created.Metadata!.DeviceId.Value.ToString("D");
    }

    private async Task<string> StorageAsync(BrowserChatCryptography crypto)
    {
        await using var vault = await OpenAsync("browser-tests");
        var store = new BrowserDeviceIdentityStore(vault);
        var identities = new PersistentDeviceIdentityService(store, store, Clock, crypto);
        var identity = await identities.LoadAsync(vault.Scope);
        Check(identity.State == PersistentDeviceIdentityState.Ready);
        var events = new BrowserChatEventStore(vault);
        var body = new ChatTextContent(new ChatContentId(Guid.Parse("33333333-3333-3333-3333-333333333333")), "synthetic protected history");
        var delivery = new ReceivedChatContent(new MessageId(Guid.Parse("44444444-4444-4444-4444-444444444444")), Conversation,
            User, identity.Metadata!.DeviceId, Now, body);
        _phase = "store-event";
        Check(await events.StoreAsync(delivery) == ChatEventStoreResult.Stored);
        _phase = "event-duplicate";
        Check(await events.StoreAsync(delivery) == ChatEventStoreResult.Duplicate);
        Check(await events.StoreAsync(new ReceivedChatContent(delivery.DeliveryMessageId, Conversation, User,
            identity.Metadata.DeviceId, Now.ToOffset(TimeSpan.FromHours(3)), body)) == ChatEventStoreResult.Duplicate);
        Check(await events.StoreAsync(new ReceivedChatContent(delivery.DeliveryMessageId, Conversation, User, identity.Metadata.DeviceId, Now,
            new ChatTextContent(body.ContentId, "synthetic conflicting history"))) == ChatEventStoreResult.Conflict);
        var page = await events.ReadPreviousPageAsync(Conversation, maximumCount: 1);
        Check(page.Items.Count == 1 && page.Items[0].Content is ChatTextContent text && text.Text == body.Text);
        Check((await events.ReadPreviousPageAsync(Conversation, page.PreviousCursor, 1)).Items.Count == 0);
        _phase = "account-service-isolation";
        await using var otherService = await OpenAsync("another-service", true);
        var other = new BrowserDeviceIdentityStore(otherService);
        Check(await other.LoadAsync(identity.Metadata.DeviceId) is null);
        await using var otherUser = await OpenAsync("browser-tests", true, new UserId(Guid.Parse("55555555-5555-5555-5555-555555555555")));
        Check(await new BrowserDeviceIdentityStore(otherUser).LoadAsync(identity.Metadata.DeviceId) is null);
        _phase = "wrong-phrase";
        try
        {
            await using var wrong = await BrowserVault.OpenAsync(js, vault.Scope, Encoding.UTF8.GetBytes("a different synthetic local phrase"));
            throw new InvalidOperationException("Wrong phrase accepted.");
        }
        catch (BrowserStorageException error) { Check(error.Code == "unlock-failed"); }
        return "ok";
    }

    private async Task<string> ConcurrentEventAsync(BrowserChatCryptography crypto)
    {
        await using var vault = await OpenAsync("browser-tests");
        var keys = new BrowserDeviceIdentityStore(vault);
        var identity = (await new PersistentDeviceIdentityService(keys, keys, Clock, crypto).LoadAsync(vault.Scope)).Metadata!;
        var delivery = new ReceivedChatContent(new MessageId(Guid.Parse("88888888-8888-8888-8888-888888888888")), Conversation, User, identity.DeviceId, Now,
            new ChatTextContent(new ChatContentId(Guid.Parse("99999999-9999-9999-9999-999999999999")), "synthetic concurrent event"));
        return "ok:" + await new BrowserChatEventStore(vault).StoreAsync(delivery);
    }

    private async Task<string> PartialAsync(BrowserChatCryptography crypto, bool retry, bool competing = false)
    {
        await using var vault = await OpenAsync("browser-tests");
        var keys = new BrowserDeviceIdentityStore(vault);
        var identity = (await new PersistentDeviceIdentityService(keys, keys, Clock, crypto).LoadAsync(vault.Scope)).Metadata!.PublicDevice!;
        var remoteKeys = new InMemoryDeviceKeyStore();
        var remoteIdentity = new DeviceIdentityService(remoteKeys, crypto);
        var peer = new UserId(Guid.Parse("66666666-6666-6666-6666-666666666666"));
        var peerOne = await remoteIdentity.CreateAsync(peer, DeviceId.New(), Now);
        var peerTwo = await remoteIdentity.CreateAsync(peer, DeviceId.New(), Now);
        var transport = new TestTransport([identity, peerOne, peerTwo]) { FailAfter = retry ? int.MaxValue : 1 };
        var projections = new ChatConversationProjectionRegistry();
        await using var session = new BrowserChatSession(vault, identity, crypto, transport, transport, projections, Clock);
        var content = new ChatTextContent(new ChatContentId(Guid.Parse("77777777-7777-7777-7777-777777777777")), "synthetic durable outgoing job");
        if (!retry) { await session.QueueAsync(Conversation, content); }
        var before = await session.Outbox.LoadAsync(Conversation, content.ContentId);
        _phase = "dispatch";
        var completed = await session.DispatchAsync();
        Check(competing ? completed is 0 or 1 : completed == (retry ? 1 : 0));
        var plan = await session.Outbox.LoadAsync(Conversation, content.ContentId);
        Check(plan is not null);
        if (!retry)
        {
            Check(plan.Envelopes.Count == 2 && plan.Envelopes.Count(item => item.IsAccepted) == 1 && plan.CompletedAt is null);
        }
        else
        {
            Check(before is not null && plan.CompletedAt is not null);
            if (completed == 1)
            {
                Check(before.Envelopes.Count(item => item.IsAccepted) == 1);
                var expected = before.Envelopes.Single(item => !item.IsAccepted).Envelope;
                Check(transport.Submitted.Count == 1 && CanonicalEnvelopeEncoding.EncodeEnvelope(expected).AsSpan()
                    .SequenceEqual(CanonicalEnvelopeEncoding.EncodeEnvelope(transport.Submitted[0])));
            }
            else { Check(transport.Submitted.Count == 0); }
            Check(await session.DispatchAsync() == 0);
            var page = await session.Events.ReadPreviousPageAsync(Conversation);
            Check(page.Items.Any(item => item.Content.ContentId == content.ContentId));
        }
        return competing ? "ok:" + completed : "ok";
    }

    private static async Task<string> NoAckAsync(BrowserChatCryptography crypto)
    {
        var keys = new InMemoryDeviceKeyStore();
        var ids = new DeviceIdentityService(keys, crypto);
        var a = await ids.CreateAsync(UserId.New(), DeviceId.New(), Now);
        var b = await ids.CreateAsync(UserId.New(), DeviceId.New(), Now);
        var engine = new ChatCryptoService(keys, crypto);
        var envelope = await engine.EncryptContentAsync(new ChatTextContent(ChatContentId.New(), "synthetic no ack"), Conversation, MessageId.New(), a.DeviceId, b, Now);
        var transport = new TestTransport([a, b]) { Pending = [new TransportDelivery(envelope, Now)] };
        using var coordinator = new ChatSyncCoordinator(transport, engine, new FailedEvents(), new ChatConversationProjectionRegistry(), b.DeviceId, Clock, false);
        try { await coordinator.SynchronizeAsync(); throw new InvalidOperationException("Failed store was accepted."); }
        catch (ChatEventStorageException) { Check(transport.Acknowledged == 0); }
        return "ok";
    }

    private async Task<string> RevokeAsync(BrowserChatCryptography crypto)
    {
        await using var vault = await OpenAsync("browser-tests");
        var store = new BrowserDeviceIdentityStore(vault);
        var service = new PersistentDeviceIdentityService(store, store, Clock, crypto);
        await service.RememberRevokedAsync(vault.Scope);
        Check((await service.LoadAsync(vault.Scope)).State == PersistentDeviceIdentityState.Revoked);
        Check((await service.CreateAsync(vault.Scope)).State == PersistentDeviceIdentityState.Revoked);
        return "ok";
    }

    private async Task<string> QuotaNoAckAsync(BrowserChatCryptography crypto)
    {
        await using var vault = await OpenAsync("browser-tests");
        var recipientKeys = new BrowserDeviceIdentityStore(vault);
        var recipient = (await new PersistentDeviceIdentityService(recipientKeys, recipientKeys, Clock, crypto).LoadAsync(vault.Scope)).Metadata!.PublicDevice!;
        var senderKeys = new InMemoryDeviceKeyStore();
        var sender = await new DeviceIdentityService(senderKeys, crypto).CreateAsync(UserId.New(), DeviceId.New(), Now);
        var envelope = await new ChatCryptoService(senderKeys, crypto).EncryptContentAsync(new ChatTextContent(ChatContentId.New(), "synthetic quota no ack"),
            Conversation, MessageId.New(), sender.DeviceId, recipient, Now);
        var transport = new TestTransport([sender, recipient]) { Pending = [new TransportDelivery(envelope, Now)] };
        using var coordinator = new ChatSyncCoordinator(transport, new ChatCryptoService(recipientKeys, crypto), new BrowserChatEventStore(vault),
            new ChatConversationProjectionRegistry(), recipient.DeviceId, Clock, false);
        try { await coordinator.SynchronizeAsync(); throw new InvalidOperationException("Quota failure not observed."); }
        catch (BrowserStorageException error) { Check(error.Code == "quota" && transport.Acknowledged == 0 && !error.ToString().Contains("synthetic-marker", StringComparison.Ordinal)); }
        return "ok";
    }
    private async Task<string> LoadStateAsync(BrowserChatCryptography crypto)
    {
        await using var vault = await OpenAsync("browser-tests");
        var keys = new BrowserDeviceIdentityStore(vault);
        var loaded = await new PersistentDeviceIdentityService(keys, keys, Clock, crypto).LoadAsync(vault.Scope);
        return "ok:" + loaded.State;
    }

    private async Task<string> ProbeAsync(BrowserChatCryptography crypto)
    {
        try { return await LoadStateAsync(crypto); }
        catch (BrowserStorageException error) { return "ok:vault-" + error.Code; }
    }
    private async Task<string> PauseCreateAsync(BrowserChatCryptography crypto, bool afterKeys)
    {
        await using var vault = await OpenAsync("browser-tests");
        var keys = new BrowserDeviceIdentityStore(vault);
        var service = new PersistentDeviceIdentityService(keys, new PausingMetadata(keys, js, afterKeys), Clock, crypto);
        await service.CreateAsync(vault.Scope);
        throw new InvalidOperationException("Crash test was not interrupted.");
    }

    private async Task<BrowserVault> OpenAsync(string service, bool create = false, UserId? user = null)
    {
        _phase = "installation";
        var installation = await BrowserVault.GetInstallationIdAsync(js, create: true);
        var scope = new DeviceIdentityScope(service, user ?? User, installation!.Value);
        _phase = "vault-open";
        return await BrowserVault.OpenAsync(js, scope, Encoding.UTF8.GetBytes("synthetic separate local vault passphrase"), create);
    }
    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition) { if (!condition) { throw new InvalidOperationException("Synthetic browser assertion failed."); } }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (ChatCryptographicException error)
        { Check(!error.ToString().Contains("synthetic native/browser", StringComparison.Ordinal)); return; }
        throw new InvalidOperationException("Tampered input was accepted.");
    }
    private sealed class TestClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class TestTransport(IReadOnlyList<PublicDevice> devices) : IChatTransport, IRecipientDeviceDirectory
    {
        public List<EncryptedEnvelope> Submitted { get; } = [];
        public int FailAfter { get; init; } = int.MaxValue;
        public IReadOnlyList<TransportDelivery> Pending { get; init; } = [];
        public int Acknowledged { get; private set; }
        public ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(devices.SingleOrDefault(device => device.DeviceId == deviceId));
        public ValueTask<ChatDevicePage> ListConversationDevicesAsync(ConversationId conversationId, string? cursor = null, int maximumCount = 50, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ChatDevicePage(devices, null));
        public ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (Submitted.Count >= FailAfter) { throw new HttpRequestException("Synthetic offline failure."); }
            Submitted.Add(envelope);
            return ValueTask.FromResult(TransportSendStatus.Accepted);
        }
        public ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(DeviceId recipientDeviceId, int maximumCount, CancellationToken cancellationToken = default) => ValueTask.FromResult(Pending);
        public ValueTask AcknowledgeAsync(DeviceId recipientDeviceId, MessageId messageId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default)
        { Acknowledged++; return ValueTask.CompletedTask; }
    }
    private sealed class FailedEvents : IChatEventStore
    {
        public ValueTask<ChatEventStoreResult> StoreAsync(ReceivedChatContent delivery, CancellationToken cancellationToken = default) => throw new ChatEventStorageException("Synthetic storage unavailable.");
        public IAsyncEnumerable<ReceivedChatContent> ReadAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ReceivedChatContent> ReadConversationAsync(ConversationId conversationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class TestCsrf(Action<HttpRequestMessage> apply) : IBrowserChatCsrfProvider
    {
        public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); apply(request); return ValueTask.CompletedTask; }
    }
    private sealed class PausingMetadata(IDeviceIdentityMetadataStore inner, IJSRuntime runtime, bool afterKeys) : IDeviceIdentityMetadataStore
    {
        public async ValueTask<IDeviceIdentityLease> AcquireAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default) =>
            new PausingLease(await inner.AcquireAsync(scope, cancellationToken), runtime, afterKeys);
        private sealed class PausingLease(IDeviceIdentityLease inner, IJSRuntime runtime, bool afterKeys) : IDeviceIdentityLease
        {
            public ValueTask<DeviceIdentityMetadata?> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
            public async ValueTask WriteAsync(DeviceIdentityMetadata metadata, CancellationToken cancellationToken = default)
            {
                if (afterKeys && metadata.PublicDevice is not null) { await runtime.InvokeVoidAsync("pauseChatCreation", metadata.DeviceId.Value.ToString("D")); }
                await inner.WriteAsync(metadata, cancellationToken);
                if (!afterKeys && metadata.PublicDevice is null) { await runtime.InvokeVoidAsync("pauseChatCreation", metadata.DeviceId.Value.ToString("D")); }
            }
            public ValueTask DeleteAsync(CancellationToken cancellationToken = default) => inner.DeleteAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
