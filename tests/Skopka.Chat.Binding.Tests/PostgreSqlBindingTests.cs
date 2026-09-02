using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Skopka.Chat.Client;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.NSec;
using Skopka.Chat.Testing;

namespace Skopka.Chat.Binding.Tests;

public sealed class PostgreSqlBindingTests
{
    [Fact]
    public async Task Independent_postgres_transactions_consume_once_and_retry_after_restart()
    {
        var scenario = await SetupAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task;
            await using var db = Context(scenario.Connection);
            return await Service(db, scenario.Clock).CompleteAsync(scenario.Challenge.Context, scenario.Proof);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Single(results.Select(item => item.BoundAt).Distinct());
        var fixture = await TestContext.Current.GetFixture<PostgreSqlTestDatabase>();
        Assert.NotNull(fixture);
        var restarted = await fixture.RestartOwnedContainerAsync();
        // External disposable databases are never stopped by this test; reopening still tests persistence.
        if (Environment.GetEnvironmentVariable(PostgreSqlTestDatabase.TestcontainersVariable) == "true" &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PostgreSqlTestDatabase.ConnectionStringVariable))) { Assert.True(restarted); }
        var connection = await PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync();
        await using var reopened = Context(connection);
        var store = new PostgreSqlDeviceBindingStore(reopened);
        var restoredChallenge = await store.GetChallengeAsync(scenario.Proof.ChallengeId);
        Assert.Equal(DeviceBindingEncoding.Encode(scenario.Challenge), DeviceBindingEncoding.Encode(restoredChallenge!));
        var resolved = await store.ResolveAsync(scenario.Challenge.Context, scenario.Clock.GetUtcNow());
        Assert.True(DeviceBindingEncoding.SameKeys(scenario.Device, resolved!.Device));
        scenario.Clock.Now = BindingProtocolTests.Now.AddMinutes(4);
        var retry = await Service(reopened, scenario.Clock).CompleteAsync(scenario.Challenge.Context, scenario.Proof);
        Assert.Equal(results[0].BoundAt, retry.BoundAt);
    }

    [Fact]
    public async Task Failure_between_enrollment_and_binding_rolls_back_registration_and_consumption()
    {
        var scenario = await SetupAsync();
        await using (var failing = Context(scenario.Connection, new FailBindingInsert()))
        {
            var error = await Assert.ThrowsAsync<DeviceBindingStorageException>(async () =>
                await Service(failing, scenario.Clock).CompleteAsync(scenario.Challenge.Context, scenario.Proof));
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("synthetic-private-marker", error.ToString(), StringComparison.Ordinal);
        }
        await using var db = Context(scenario.Connection);
        Assert.Null(await ((IDeviceRepository)new PostgreSqlChatStore(db)).GetAsync(scenario.Device.DeviceId));
        Assert.Null(await new PostgreSqlDeviceBindingStore(db).ResolveAsync(scenario.Challenge.Context, scenario.Clock.Now));
        var result = await Service(db, scenario.Clock).CompleteAsync(scenario.Challenge.Context, scenario.Proof);
        Assert.Equal(scenario.Device.DeviceId, result.Device.DeviceId);
    }

    [Fact]
    public async Task Concurrent_session_switch_enrolls_only_one_device_and_cleanup_is_bounded()
    {
        var scenario = await SetupAsync();
        var otherKeys = new InMemoryDeviceKeyStore();
        var other = await new DeviceIdentityService(otherKeys).CreateAsync(scenario.Device.UserId, DeviceId.New(), BindingProtocolTests.Now);
        DeviceBindingChallenge otherChallenge;
        await using (var db = Context(scenario.Connection))
        {
            otherChallenge = await Service(db, scenario.Clock).IssueAsync(scenario.Challenge.Context, DeviceBindingOperation.Enrollment, other);
        }
        var otherProof = await new DeviceBindingProofService(otherKeys, scenario.Clock).CreateProofAsync(otherChallenge,
            otherChallenge.Context, other, otherChallenge.Operation);
        async Task<bool> Complete(DeviceBindingChallenge challenge, DeviceBindingProof proof)
        {
            await using var db = Context(scenario.Connection);
            try { await Service(db, scenario.Clock).CompleteAsync(challenge.Context, proof); return true; }
            catch (DeviceBindingException) { return false; }
        }
        var result = await Task.WhenAll(Complete(scenario.Challenge, scenario.Proof), Complete(otherChallenge, otherProof));
        Assert.Equal(1, result.Count(value => value));
        await using var verify = Context(scenario.Connection);
        var devices = (IDeviceRepository)new PostgreSqlChatStore(verify);
        Assert.Equal(1, new[] { await devices.GetAsync(scenario.Device.DeviceId), await devices.GetAsync(other.DeviceId) }.Count(value => value is not null));
        var store = new PostgreSqlDeviceBindingStore(verify);
        Assert.InRange(await store.CleanupAsync(BindingProtocolTests.Now.AddHours(3), 1), 0, 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.CleanupAsync(BindingProtocolTests.Now, 1001));
    }

    [Fact]
    public async Task Revocation_racing_completion_invalidates_every_binding_and_exact_retry()
    {
        var scenario = await SetupAsync();
        await using (var db = Context(scenario.Connection))
        {
            await Service(db, scenario.Clock).CompleteAsync(scenario.Challenge.Context, scenario.Proof);
        }
        DeviceBindingChallenge next;
        await using (var db = Context(scenario.Connection))
        {
            next = await Service(db, scenario.Clock).IssueAsync(new DeviceAuthorizationContext(scenario.Challenge.Context.ServiceId,
                scenario.Device.UserId, "new-" + Guid.NewGuid().ToString("N"), scenario.Challenge.Context.ExpiresAt), DeviceBindingOperation.Rebind, scenario.Device);
        }
        var proof = await new DeviceBindingProofService(scenario.Keys, scenario.Clock).CreateProofAsync(next, next.Context, scenario.Device, next.Operation);
        async Task Complete()
        {
            await using var db = Context(scenario.Connection);
            try { await Service(db, scenario.Clock).CompleteAsync(next.Context, proof); }
            catch (DeviceBindingException) { }
        }
        async Task Revoke()
        {
            await using var db = Context(scenario.Connection);
            await ((IDeviceRepository)new PostgreSqlChatStore(db)).RevokeAsync(scenario.Device.DeviceId, scenario.Clock.Now);
        }
        await Task.WhenAll(Complete(), Revoke());
        await using var verify = Context(scenario.Connection);
        var store = new PostgreSqlDeviceBindingStore(verify);
        Assert.Null(await store.ResolveAsync(scenario.Challenge.Context, scenario.Clock.Now));
        Assert.Null(await store.ResolveAsync(next.Context, scenario.Clock.Now));
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await Service(verify, scenario.Clock).CompleteAsync(next.Context, proof));
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await Service(verify, scenario.Clock).CompleteAsync(scenario.Challenge.Context, scenario.Proof));
    }

    [Fact]
    public async Task Pending_expiry_is_rechecked_after_waiting_for_transaction_locks()
    {
        var scenario = await SetupAsync();
        // Advance the server clock while the database command runs, after service-level validation.
        await using var db = Context(scenario.Connection, new AdvanceClockAfterLock(scenario.Clock, scenario.Challenge.ExpiresAt));
        await Assert.ThrowsAsync<DeviceBindingException>(async () => await Service(db, scenario.Clock).CompleteAsync(scenario.Challenge.Context, scenario.Proof));
        await using var verification = Context(scenario.Connection);
        Assert.Null(await ((IDeviceRepository)new PostgreSqlChatStore(verification)).GetAsync(scenario.Device.DeviceId));
    }

    private static async Task<Scenario> SetupAsync()
    {
        var connection = await PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync();
        await using var db = Context(connection);
        await db.Database.MigrateAsync();
        var clock = new BindingProtocolTests.Clock();
        var keys = new InMemoryDeviceKeyStore();
        var device = await new DeviceIdentityService(keys).CreateAsync(UserId.New(), DeviceId.New(), clock.Now);
        var context = new DeviceAuthorizationContext("chat.example.test", device.UserId, Guid.NewGuid().ToString("N"), clock.Now.AddHours(1));
        var challenge = await Service(db, clock).IssueAsync(context, DeviceBindingOperation.Enrollment, device);
        var proof = await new DeviceBindingProofService(keys, clock).CreateProofAsync(challenge, context, device, challenge.Operation);
        return new Scenario(connection, keys, device, challenge, proof, clock);
    }
    private static ChatDbContext Context(string connection, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options);
    private static DeviceBindingService Service(ChatDbContext db, TimeProvider clock) =>
        new(new PostgreSqlChatStore(db), new PostgreSqlDeviceBindingStore(db), new NSecDeviceProofVerifier(), clock);
    private sealed record Scenario(string Connection, InMemoryDeviceKeyStore Keys, PublicDevice Device,
        DeviceBindingChallenge Challenge, DeviceBindingProof Proof, BindingProtocolTests.Clock Clock);
    private sealed class FailBindingInsert : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO device_session_bindings", StringComparison.Ordinal))
            {
                throw new PostgresException("synthetic-private-marker", "ERROR", "ERROR", "P0001");
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class AdvanceClockAfterLock(BindingProtocolTests.Clock clock, DateTimeOffset time) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)) { clock.Now = time; }
            return ValueTask.FromResult(result);
        }
    }
}
