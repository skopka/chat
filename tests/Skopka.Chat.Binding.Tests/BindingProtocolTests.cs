using System.Text;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.NSec;

namespace Skopka.Chat.Binding.Tests;

public sealed class BindingProtocolTests
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Canonical_binding_bytes_have_independent_version_and_fixed_golden_vector()
    {
        var user = new UserId(Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"));
        var device = new PublicDevice(user, new DeviceId(Guid.Parse("11121314-1516-1718-191a-1b1c1d1e1f20")),
            new KeyId(Guid.Parse("21222324-2526-2728-292a-2b2c2d2e2f30")), new byte[32], Enumerable.Repeat((byte)1, 32).ToArray(), DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        var challenge = new DeviceBindingChallenge(1, DeviceBindingOperation.Enrollment,
            new DeviceAuthorizationContext("svc", user, "session", DateTimeOffset.UnixEpoch.AddMilliseconds(4)), device,
            Guid.Parse("31323334-3536-3738-393a-3b3c3d3e3f40"), Enumerable.Repeat((byte)2, 32).ToArray(),
            DateTimeOffset.UnixEpoch.AddMilliseconds(2), DateTimeOffset.UnixEpoch.AddMilliseconds(3));
        var expected = "536B6F706B612E436861742E44657669636542696E64696E672E763100" +
            "0000000100000001000000037376630000000773657373696F6E" +
            "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20" +
            "2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40" +
            new string('0', 64) + string.Concat(Enumerable.Repeat("01", 32)) + string.Concat(Enumerable.Repeat("02", 32)) +
            "0000000000000001000000000000000200000000000000030000000000000004";
        Assert.Equal(expected, Convert.ToHexString(DeviceBindingEncoding.Encode(challenge)));
        Assert.Equal(expected, Convert.ToHexString(DeviceBindingEncoding.Encode(DeviceBindingEncoding.Decode(Convert.FromHexString(expected)))));
    }

    [Fact]
    public async Task Enrollment_and_relogin_bind_multiple_sessions_to_unchanged_keys()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("login-one", DeviceBindingOperation.Enrollment);
        Assert.Null(await ((IDeviceRepository)scenario.Store).GetAsync(scenario.Device.DeviceId));
        var first = await scenario.Service.CompleteAsync(challenge.Context, proof);
        var (secondChallenge, secondProof) = await scenario.ProofAsync("login-two", DeviceBindingOperation.Rebind);
        var second = await scenario.Service.CompleteAsync(secondChallenge.Context, secondProof);
        Assert.True(DeviceBindingEncoding.SameKeys(first.Device, second.Device));
        Assert.NotNull(await scenario.Store.ResolveAsync(challenge.Context, Now));
        Assert.NotNull(await scenario.Store.ResolveAsync(second.Context, Now));
        Assert.NotEqual(first.Context.SessionReference, second.Context.SessionReference);
    }

    [Fact]
    public async Task Distinct_installations_of_one_account_enroll_distinct_devices()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("first-installation", DeviceBindingOperation.Enrollment);
        await scenario.Service.CompleteAsync(challenge.Context, proof);
        var other = await new DeviceIdentityService(scenario.Keys).CreateAsync(scenario.Device.UserId, DeviceId.New(), Now);
        var account = new DeviceAuthorizationContext(challenge.Context.ServiceId, other.UserId, "second-installation", challenge.Context.ExpiresAt);
        var next = await scenario.Service.IssueAsync(account, DeviceBindingOperation.Enrollment, other);
        var nextProof = await new DeviceBindingProofService(scenario.Keys, scenario.Clock).CreateProofAsync(next, account, other, next.Operation);
        var result = await scenario.Service.CompleteAsync(account, nextProof);
        Assert.Equal(other.DeviceId, result.Device.DeviceId);
        Assert.NotEqual(scenario.Device.DeviceId, result.Device.DeviceId);
        Assert.NotNull(await scenario.Store.ResolveAsync(challenge.Context, Now));
        Assert.NotNull(await scenario.Store.ResolveAsync(account, Now));
    }

    [Fact]
    public async Task Exact_retry_after_lost_response_is_identical_but_never_bypasses_revocation()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("retry", DeviceBindingOperation.Enrollment);
        var first = await scenario.Service.CompleteAsync(challenge.Context, proof);
        scenario.Clock.Now = Now.AddMinutes(4); // consumed retry may outlive the short challenge
        var retry = await scenario.Service.CompleteAsync(challenge.Context, proof);
        Assert.Equal(first.BoundAt, retry.BoundAt);
        Assert.True(DeviceBindingEncoding.SameKeys(first.Device, retry.Device));
        await ((IDeviceRepository)scenario.Store).RevokeAsync(scenario.Device.DeviceId, scenario.Clock.Now);
        var failure = await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.CompleteAsync(challenge.Context, proof));
        Assert.Equal(DeviceBindingFailure.Revoked, failure.Failure);
        Assert.Null(await scenario.Store.ResolveAsync(challenge.Context, scenario.Clock.Now));
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.ProofAsync("after-revoke", DeviceBindingOperation.Rebind));
    }

    [Fact]
    public async Task Concurrent_completion_has_one_effect_and_session_cannot_switch_device()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("parallel", DeviceBindingOperation.Enrollment);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => scenario.Service.CompleteAsync(challenge.Context, proof).AsTask()));
        Assert.Single(results.Select(result => result.BoundAt).Distinct());
        var other = await new DeviceIdentityService(scenario.Keys).CreateAsync(scenario.Device.UserId, DeviceId.New(), Now);
        var next = await scenario.Service.IssueAsync(challenge.Context, DeviceBindingOperation.Enrollment, other);
        var otherProof = await new DeviceBindingProofService(scenario.Keys, scenario.Clock).CreateProofAsync(next, next.Context, other, next.Operation);
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.CompleteAsync(next.Context, otherProof));
        Assert.Null(await ((IDeviceRepository)scenario.Store).GetAsync(other.DeviceId));
    }

    [Fact]
    public async Task Invalid_signature_and_changed_completed_signature_have_no_effect()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("signature", DeviceBindingOperation.Enrollment);
        var bad = proof.Signature.ToArray(); bad[0] ^= 1;
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.CompleteAsync(challenge.Context, new(proof.ChallengeId, bad)));
        Assert.Null(await ((IDeviceRepository)scenario.Store).GetAsync(scenario.Device.DeviceId));
        Assert.Null(await scenario.Store.ResolveAsync(challenge.Context, Now));
        await scenario.Service.CompleteAsync(challenge.Context, proof);
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.CompleteAsync(challenge.Context, new(proof.ChallengeId, bad)));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("deadline")]
    public async Task Foreign_context_fails_on_client_and_server_without_reflection(string field)
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("context", DeviceBindingOperation.Enrollment);
        var expected = challenge.Context;
        var wrong = new DeviceAuthorizationContext(field == "service" ? "attacker-secret" : expected.ServiceId,
            field == "user" ? UserId.New() : expected.UserId, field == "session" ? "attacker-secret" : expected.SessionReference,
            field == "deadline" ? expected.ExpiresAt.AddHours(1) : expected.ExpiresAt);
        var serverError = await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.CompleteAsync(wrong, proof));
        var clientError = await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await new DeviceBindingProofService(scenario.Keys, scenario.Clock).CreateProofAsync(challenge, wrong, scenario.Device, challenge.Operation));
        Assert.DoesNotContain("attacker-secret", serverError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("attacker-secret", clientError.ToString(), StringComparison.Ordinal);
        Assert.Null(serverError.InnerException);
        Assert.Null(await scenario.Store.ResolveAsync(expected, Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_public_keys_are_covered_and_directory_keys_cannot_be_replaced(bool encryption)
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("keys", DeviceBindingOperation.Enrollment);
        var foreignKeys = await new DeviceIdentityService(new InMemoryDeviceKeyStore()).CreateAsync(scenario.Device.UserId, DeviceId.New(), Now);
        var changed = new PublicDevice(scenario.Device.UserId, scenario.Device.DeviceId, scenario.Device.KeyId,
            encryption ? foreignKeys.EncryptionPublicKey.Span : scenario.Device.EncryptionPublicKey.Span,
            encryption ? scenario.Device.SigningPublicKey.Span : foreignKeys.SigningPublicKey.Span, Now);
        var tampered = new DeviceBindingChallenge(1, challenge.Operation, challenge.Context, changed, challenge.ChallengeId,
            challenge.Nonce.Span, challenge.IssuedAt, challenge.ExpiresAt);
        Assert.False(new NSecDeviceProofVerifier().Verify(tampered, proof));
        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await new DeviceBindingProofService(scenario.Keys, scenario.Clock).CreateProofAsync(tampered, challenge.Context, scenario.Device, challenge.Operation));
        await scenario.Service.CompleteAsync(challenge.Context, proof);
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.IssueAsync(scenario.Context("new"), DeviceBindingOperation.Rebind, changed));
        Assert.True(DeviceBindingEncoding.SameKeys(scenario.Device, (await ((IDeviceRepository)scenario.Store).GetAsync(scenario.Device.DeviceId))!));
    }

    [Fact]
    public async Task Expired_pending_challenge_and_expired_authorization_never_bind()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("expiry", DeviceBindingOperation.Enrollment);
        scenario.Clock.Now = challenge.ExpiresAt;
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.CompleteAsync(challenge.Context, proof));
        Assert.Null(await ((IDeviceRepository)scenario.Store).GetAsync(scenario.Device.DeviceId));
        scenario.Clock.Now = challenge.Context.ExpiresAt;
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await scenario.Service.IssueAsync(challenge.Context, challenge.Operation, scenario.Device));
        Assert.Equal(1, await scenario.Store.CleanupAsync(scenario.Clock.Now, 1));
        Assert.Null(await scenario.Store.GetChallengeAsync(proof.ChallengeId));
    }

    [Fact]
    public async Task Creation_cannot_replace_keys_and_proof_rejects_missing_or_corrupt_private_keys()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, _) = await scenario.ProofAsync("missing", DeviceBindingOperation.Enrollment);
        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await new DeviceIdentityService(scenario.Keys).CreateAsync(scenario.Device.UserId, scenario.Device.DeviceId, Now));
        Assert.True(DeviceBindingEncoding.SameKeys(scenario.Device,
            (await new DeviceIdentityService(scenario.Keys).LoadPublicAsync(scenario.Device.UserId, scenario.Device.DeviceId, Now))!));
        await scenario.Keys.DeleteAsync(scenario.Device.DeviceId);
        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await new DeviceBindingProofService(scenario.Keys, scenario.Clock).CreateProofAsync(challenge, challenge.Context, scenario.Device, challenge.Operation));
        await scenario.Keys.SaveAsync(new DeviceKeyMaterial(scenario.Device.UserId, scenario.Device.DeviceId, scenario.Device.KeyId, [1], [2]));
        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await new DeviceBindingProofService(scenario.Keys, scenario.Clock).CreateProofAsync(challenge, challenge.Context, scenario.Device, challenge.Operation));
    }

    [Fact]
    public async Task Canonical_parser_rejects_all_truncations_trailing_data_and_signatures_bind_every_byte()
    {
        var scenario = await Scenario.CreateAsync();
        var (challenge, proof) = await scenario.ProofAsync("parser", DeviceBindingOperation.Enrollment);
        var bytes = DeviceBindingEncoding.Encode(challenge);
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.Throws<ArgumentException>(() => DeviceBindingEncoding.Decode(bytes.AsSpan(0, length)));
        }
        Assert.Throws<ArgumentException>(() => DeviceBindingEncoding.Decode([.. bytes, 0]));
        for (var index = 0; index < bytes.Length; index++)
        {
            var mutation = bytes.ToArray(); mutation[index] ^= 0x80;
            try { Assert.False(new NSecDeviceProofVerifier().Verify(DeviceBindingEncoding.Decode(mutation), proof)); }
            catch (ArgumentException) { }
        }
        Assert.Throws<ArgumentException>(() => DeviceBindingEncoding.Decode(Encoding.UTF8.GetBytes("private-marker")));
    }

    [Fact]
    public void Proof_verifier_and_protocol_preserve_assembly_boundaries()
    {
        Assert.DoesNotContain(typeof(DeviceBindingService).Assembly.GetReferencedAssemblies(), item => item.Name!.Contains("Client", StringComparison.Ordinal) || item.Name.Contains("NSec", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(DeviceBindingChallenge).Assembly.GetReferencedAssemblies(), item => item.Name!.Contains("NSec", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(NSecDeviceProofVerifier).Assembly.GetReferencedAssemblies(), item => item.Name!.Contains("Client", StringComparison.Ordinal));
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = BindingProtocolTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Scenario
    {
        public InMemoryDeviceKeyStore Keys { get; } = new();
        public InMemoryServerStore Store { get; } = new();
        public Clock Clock { get; } = new();
        public PublicDevice Device { get; private set; } = null!;
        public DeviceBindingService Service => new(Store, Store, new NSecDeviceProofVerifier(), Clock);
        public DeviceAuthorizationContext Context(string session) => new("https://chat.example.test", Device.UserId, session, Now.AddHours(1));
        public static async Task<Scenario> CreateAsync()
        {
            var scenario = new Scenario();
            scenario.Device = await new DeviceIdentityService(scenario.Keys).CreateAsync(UserId.New(), DeviceId.New(), Now);
            return scenario;
        }
        public async Task<(DeviceBindingChallenge Challenge, DeviceBindingProof Proof)> ProofAsync(string session, DeviceBindingOperation operation)
        {
            var challenge = await Service.IssueAsync(Context(session), operation, Device);
            var proof = await new DeviceBindingProofService(Keys, Clock).CreateProofAsync(challenge, challenge.Context, Device, operation);
            return (challenge, proof);
        }
    }
}
