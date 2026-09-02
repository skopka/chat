using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Persistence.PostgreSql;

/// <summary>Generic persistence failure; database statements/parameters are never attached.</summary>
public sealed class DeviceBindingStorageException() : Exception("Device binding storage is unavailable or inconsistent.");

/// <summary>PostgreSQL transaction boundary for enrollment, challenge consumption, retries and session bindings.</summary>
public sealed class PostgreSqlDeviceBindingStore(ChatDbContext context) : IDeviceBindingRepository
{
    private readonly ChatDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public ValueTask<bool> TryAddChallengeAsync(DeviceBindingChallenge challenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        return SafeAsync(async () =>
        {
            var payload = DeviceBindingEncoding.Encode(challenge);
            return await _context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO device_binding_challenges(challenge_id, payload, expires_at, session_expires_at)
                VALUES ({challenge.ChallengeId}, {payload}, {challenge.ExpiresAt}, {challenge.Context.ExpiresAt})
                ON CONFLICT (challenge_id) DO NOTHING
                """, cancellationToken).ConfigureAwait(false) == 1;
        });
    }

    /// <inheritdoc />
    public ValueTask<DeviceBindingChallenge?> GetChallengeAsync(Guid challengeId, CancellationToken cancellationToken = default) =>
        SafeAsync<DeviceBindingChallenge?>(async () =>
        {
            var row = await _context.DeviceChallenges.AsNoTracking().SingleOrDefaultAsync(item => item.ChallengeId == challengeId, cancellationToken).ConfigureAwait(false);
            return row is null ? null : DeviceBindingEncoding.Decode(row.Payload);
        });

    /// <inheritdoc />
    public ValueTask<DeviceSessionBinding?> CompleteAsync(DeviceBindingChallenge verifiedChallenge, DeviceBindingProof proof,
        TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedChallenge);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return SafeAsync<DeviceSessionBinding?>(async () =>
        {
            var challenge = DeviceBindingEncoding.Decode(DeviceBindingEncoding.Encode(verifiedChallenge));
            var auth = challenge.Context;
            var device = challenge.Device;
            if (proof.ChallengeId != challenge.ChallengeId) { return null; }
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Hash collisions only serialize unrelated sessions; identity equality is still checked in full.
            var sessionKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"skopka.chat.binding.v1\0{auth.ServiceId}\0{auth.UserId.Value:N}\0{auth.SessionReference}")));
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({sessionKey})", cancellationToken).ConfigureAwait(false);
            var devices = await _context.Devices.FromSqlInterpolated($"SELECT * FROM devices WHERE device_id = {device.DeviceId.Value} FOR UPDATE")
                .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var current = devices.SingleOrDefault()?.ToDomain();
            var rows = await _context.DeviceChallenges.FromSqlInterpolated($"SELECT * FROM device_binding_challenges WHERE challenge_id = {proof.ChallengeId} FOR UPDATE")
                .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var row = rows.SingleOrDefault();
            var now = DeviceBindingEncoding.NormalizeTime(timeProvider.GetUtcNow());
            if (row is null || auth.ExpiresAt <= now || !row.Payload.AsSpan().SequenceEqual(DeviceBindingEncoding.Encode(challenge)) ||
                (current is not null && (current.IsRevoked || !DeviceBindingEncoding.SameKeys(current, device)))) { return null; }
            var binding = await FindBindingAsync(auth, cancellationToken).ConfigureAwait(false);
            if (binding is not null && (binding.DeviceId != device.DeviceId.Value || binding.KeyId != device.KeyId.Value || binding.ExpiresAt != auth.ExpiresAt)) { return null; }
            if (row.Signature is not null)
            {
                if (binding is null || current is null || row.BoundAt != binding.BoundAt || !row.Signature.AsSpan().SequenceEqual(proof.Signature.Span)) { return null; }
                return new DeviceSessionBinding(auth, current, binding.BoundAt);
            }
            if (challenge.ExpiresAt <= now || challenge.IssuedAt > now ||
                (challenge.Operation == DeviceBindingOperation.Rebind && current is null) ||
                (challenge.Operation == DeviceBindingOperation.Enrollment && current is not null)) { return null; }
            if (current is null)
            {
                var encryption = device.EncryptionPublicKey.ToArray();
                var signing = device.SigningPublicKey.ToArray();
                var added = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO devices(device_id, user_id, key_id, encryption_public_key, signing_public_key, registered_at)
                    VALUES ({device.DeviceId.Value}, {device.UserId.Value}, {device.KeyId.Value}, {encryption}, {signing}, {device.RegisteredAt})
                    ON CONFLICT (device_id) DO NOTHING
                    """, cancellationToken).ConfigureAwait(false);
                if (added != 1) { return null; }
                current = device;
            }
            var boundAt = binding?.BoundAt ?? now;
            if (binding is null)
            {
                await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO device_session_bindings(service_id, user_id, session_reference, device_id, key_id, bound_at, expires_at)
                    VALUES ({auth.ServiceId}, {auth.UserId.Value}, {auth.SessionReference}, {device.DeviceId.Value}, {device.KeyId.Value}, {boundAt}, {auth.ExpiresAt})
                    """, cancellationToken).ConfigureAwait(false);
            }
            var signature = proof.Signature.ToArray();
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE device_binding_challenges SET signature = {signature}, bound_at = {boundAt}
                WHERE challenge_id = {proof.ChallengeId} AND signature IS NULL
                """, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DeviceSessionBinding(auth, current, boundAt);
        });
    }

    /// <inheritdoc />
    public ValueTask<DeviceSessionBinding?> ResolveAsync(DeviceAuthorizationContext context, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var auth = context;
        return SafeAsync<DeviceSessionBinding?>(async () =>
        {
            if (auth.ExpiresAt <= now) { return null; }
            var result = await (from binding in _context.DeviceSessions.AsNoTracking()
                                join device in _context.Devices.AsNoTracking() on binding.DeviceId equals device.DeviceId
                                where binding.ServiceId == auth.ServiceId && binding.UserId == auth.UserId.Value &&
                                    binding.SessionReference == auth.SessionReference && binding.ExpiresAt == auth.ExpiresAt &&
                                    binding.ExpiresAt > now && device.RevokedAt == null && device.UserId == auth.UserId.Value && device.KeyId == binding.KeyId
                                select new { Device = device, binding.BoundAt }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return result is null ? null : new DeviceSessionBinding(auth, result.Device.ToDomain(), result.BoundAt);
        });
    }

    /// <inheritdoc />
    public ValueTask<int> CleanupAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        return SafeAsync(async () =>
        {
            var count = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM device_binding_challenges WHERE challenge_id IN (
                    SELECT challenge_id FROM device_binding_challenges
                    WHERE (signature IS NULL AND expires_at <= {now}) OR session_expires_at <= {now}
                    ORDER BY expires_at, challenge_id LIMIT {maximumCount} FOR UPDATE SKIP LOCKED)
                """, cancellationToken).ConfigureAwait(false);
            var remaining = maximumCount - count;
            if (remaining > 0)
            {
                count += await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM device_session_bindings WHERE (service_id, user_id, session_reference) IN (
                        SELECT service_id, user_id, session_reference FROM device_session_bindings
                        WHERE expires_at <= {now} ORDER BY expires_at, service_id, user_id, session_reference
                        LIMIT {remaining} FOR UPDATE SKIP LOCKED)
                    """, cancellationToken).ConfigureAwait(false);
            }
            return count;
        });
    }

    private Task<DeviceSessionEntity?> FindBindingAsync(DeviceAuthorizationContext auth, CancellationToken cancellationToken) =>
        _context.DeviceSessions.AsNoTracking().SingleOrDefaultAsync(item => item.ServiceId == auth.ServiceId &&
            item.UserId == auth.UserId.Value && item.SessionReference == auth.SessionReference, cancellationToken);

    private static async ValueTask<T> SafeAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception exception) when (exception is NpgsqlException or DbUpdateException)
        {
            throw new DeviceBindingStorageException();
        }
    }
}
