using System.Buffers.Binary;
using System.Text;

namespace Skopka.Chat.Protocol;

/// <summary>Explicit purpose of a device ownership proof.</summary>
public enum DeviceBindingOperation
{
    /// <summary>Register new immutable public keys and bind the authenticated session.</summary>
    Enrollment = 1,
    /// <summary>Bind a new session using existing directory keys.</summary>
    Rebind = 2
}

/// <summary>Host-authenticated, non-secret authorization context. Never contains bearer credentials.</summary>
public sealed class DeviceAuthorizationContext
{
    /// <summary>Creates context only after host authentication; session references must never be reused.</summary>
    public DeviceAuthorizationContext(string serviceId, UserId userId, string sessionReference, DateTimeOffset expiresAt)
    {
        DeviceBindingEncoding.ValidateReference(serviceId);
        DeviceBindingEncoding.ValidateReference(sessionReference);
        if (userId.Value == Guid.Empty || expiresAt == default)
        {
            throw new ArgumentException("Invalid device authorization context.");
        }

        ServiceId = serviceId;
        UserId = userId;
        SessionReference = sessionReference;
        ExpiresAt = DeviceBindingEncoding.NormalizeTime(expiresAt);
    }

    /// <summary>Exact configuration-owned service/authority identifier.</summary>
    public string ServiceId { get; }
    /// <summary>Authenticated account mapped to the chat user.</summary>
    public UserId UserId { get; }
    /// <summary>Opaque, non-secret session reference, never an access or refresh token.</summary>
    public string SessionReference { get; }
    /// <summary>Absolute upper bound on authorization; binding cannot extend it.</summary>
    public DateTimeOffset ExpiresAt { get; }
    /// <summary>Compares all authenticated fields, including the absolute deadline.</summary>
    public bool Matches(DeviceAuthorizationContext other) => other is not null &&
        ServiceId == other.ServiceId && UserId == other.UserId &&
        SessionReference == other.SessionReference && ExpiresAt == other.ExpiresAt;
    /// <inheritdoc />
    public override string ToString() => "DeviceAuthorizationContext([REDACTED])";
}

/// <summary>Immutable, purpose-bound ownership challenge; envelope/content protocols are unaffected.</summary>
public sealed class DeviceBindingChallenge
{
    private readonly byte[] _nonce;

    /// <summary>Creates a strictly validated binding-v1 challenge.</summary>
    public DeviceBindingChallenge(int version, DeviceBindingOperation operation, DeviceAuthorizationContext context,
        PublicDevice device, Guid challengeId, ReadOnlySpan<byte> nonce, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(device);
        ProtocolValidator.Validate(device);
        issuedAt = DeviceBindingEncoding.NormalizeTime(issuedAt);
        expiresAt = DeviceBindingEncoding.NormalizeTime(expiresAt);
        if (version != 1 || operation is not (DeviceBindingOperation.Enrollment or DeviceBindingOperation.Rebind) ||
            device.UserId != context.UserId || device.IsRevoked || challengeId == Guid.Empty || nonce.Length != 32 ||
            issuedAt == default || expiresAt <= issuedAt || expiresAt > context.ExpiresAt ||
            expiresAt - issuedAt > TimeSpan.FromMinutes(5) || device.RegisteredAt > issuedAt)
        {
            throw new ArgumentException("Invalid device binding challenge.");
        }

        Version = version;
        Operation = operation;
        Context = context;
        Device = new PublicDevice(device.UserId, device.DeviceId, device.KeyId, device.EncryptionPublicKey.Span,
            device.SigningPublicKey.Span, DeviceBindingEncoding.NormalizeTime(device.RegisteredAt));
        ChallengeId = challengeId;
        _nonce = nonce.ToArray();
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Binding protocol version, independent of envelope version.</summary>
    public int Version { get; }
    /// <summary>Enrollment or rebind.</summary>
    public DeviceBindingOperation Operation { get; }
    /// <summary>Expected authenticated context.</summary>
    public DeviceAuthorizationContext Context { get; }
    /// <summary>Both authoritative public keys covered by the proof.</summary>
    public PublicDevice Device { get; }
    /// <summary>One-time challenge identifier.</summary>
    public Guid ChallengeId { get; }
    /// <summary>32-byte server-generated cryptographic nonce.</summary>
    public ReadOnlyMemory<byte> Nonce => _nonce;
    /// <summary>Server issue time.</summary>
    public DateTimeOffset IssuedAt { get; }
    /// <summary>Deadline for the first successful completion.</summary>
    public DateTimeOffset ExpiresAt { get; }
    /// <inheritdoc />
    public override string ToString() => "DeviceBindingChallenge([REDACTED])";
}

/// <summary>Typed Ed25519 proof over a stored canonical challenge, not arbitrary caller bytes.</summary>
public sealed class DeviceBindingProof
{
    private readonly byte[] _signature;
    /// <summary>Creates a bounded completion request.</summary>
    public DeviceBindingProof(Guid challengeId, ReadOnlySpan<byte> signature)
    {
        if (challengeId == Guid.Empty || signature.Length != 64)
        {
            throw new ArgumentException("Invalid device binding proof.");
        }

        ChallengeId = challengeId;
        _signature = signature.ToArray();
    }
    /// <summary>Stored challenge being completed.</summary>
    public Guid ChallengeId { get; }
    /// <summary>Signature of the canonical challenge bytes.</summary>
    public ReadOnlyMemory<byte> Signature => _signature;
    /// <inheritdoc />
    public override string ToString() => "DeviceBindingProof([REDACTED])";
}

/// <summary>Immutable result returned for the first completion and every permitted exact retry.</summary>
public sealed record DeviceSessionBinding(DeviceAuthorizationContext Context, PublicDevice Device, DateTimeOffset BoundAt)
{
    /// <inheritdoc />
    public override string ToString() => "DeviceSessionBinding([REDACTED])";
}

/// <summary>Bounded canonical signing/storage encoding for binding-v1, never JSON.</summary>
public static class DeviceBindingEncoding
{
    /// <summary>Maximum canonical challenge size.</summary>
    public const int MaximumBytes = 1024;
    /// <summary>Maximum UTF-8 bytes in either context reference.</summary>
    public const int MaximumReferenceBytes = 256;
    private static readonly byte[] Domain = "Skopka.Chat.DeviceBinding.v1\0"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Encodes all proof fields with explicit field boundaries and network byte order.</summary>
    public static byte[] Encode(DeviceBindingChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        using var stream = new MemoryStream(MaximumBytes);
        stream.Write(Domain);
        WriteInt32(stream, challenge.Version);
        WriteInt32(stream, (int)challenge.Operation);
        WriteString(stream, challenge.Context.ServiceId);
        WriteString(stream, challenge.Context.SessionReference);
        WriteGuid(stream, challenge.Context.UserId.Value);
        WriteGuid(stream, challenge.Device.DeviceId.Value);
        WriteGuid(stream, challenge.Device.KeyId.Value);
        WriteGuid(stream, challenge.ChallengeId);
        stream.Write(challenge.Device.EncryptionPublicKey.Span);
        stream.Write(challenge.Device.SigningPublicKey.Span);
        stream.Write(challenge.Nonce.Span);
        WriteTime(stream, challenge.Device.RegisteredAt);
        WriteTime(stream, challenge.IssuedAt);
        WriteTime(stream, challenge.ExpiresAt);
        WriteTime(stream, challenge.Context.ExpiresAt);
        return stream.ToArray();
    }

    /// <summary>Strictly decodes bounded canonical bytes; malformed data is never reflected in errors.</summary>
    public static DeviceBindingChallenge Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length > MaximumBytes || bytes.Length < Domain.Length || !bytes[..Domain.Length].SequenceEqual(Domain))
            {
                throw new ArgumentException("Invalid binding data.");
            }

            var reader = new Reader(bytes[Domain.Length..]);
            var version = reader.Int32();
            var operation = (DeviceBindingOperation)reader.Int32();
            var service = reader.Text();
            var session = reader.Text();
            var user = new UserId(reader.Id());
            var deviceId = new DeviceId(reader.Id());
            var keyId = new KeyId(reader.Id());
            var challengeId = reader.Id();
            var encryption = reader.Take(32).ToArray();
            var signing = reader.Take(32).ToArray();
            var nonce = reader.Take(32).ToArray();
            var registered = reader.Time();
            var issued = reader.Time();
            var expires = reader.Time();
            var sessionExpires = reader.Time();
            if (!reader.AtEnd)
            {
                throw new ArgumentException("Invalid binding data.");
            }

            return new DeviceBindingChallenge(version, operation,
                new DeviceAuthorizationContext(service, user, session, sessionExpires),
                new PublicDevice(user, deviceId, keyId, encryption, signing, registered), challengeId, nonce, issued, expires);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new ArgumentException("Invalid canonical device binding data.");
        }
    }

    /// <summary>Exact immutable identity comparison, excluding lifecycle timestamps.</summary>
    public static bool SameKeys(PublicDevice left, PublicDevice right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.UserId == right.UserId && left.DeviceId == right.DeviceId && left.KeyId == right.KeyId &&
            left.EncryptionPublicKey.Span.SequenceEqual(right.EncryptionPublicKey.Span) &&
            left.SigningPublicKey.Span.SequenceEqual(right.SigningPublicKey.Span);
    }

    /// <summary>Rejects empty, excessive or invalid context references without reflecting their contents.</summary>
    public static void ValidateReference(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumReferenceBytes ||
                value.Any(char.IsControl) || Utf8.GetByteCount(value) > MaximumReferenceBytes)
            {
                throw new ArgumentException("Invalid binding data.");
            }
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("Invalid device binding context reference.");
        }
    }

    /// <summary>Canonical binding timestamps have millisecond precision.</summary>
    public static DateTimeOffset NormalizeTime(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
    private static void WriteString(Stream stream, string value)
    {
        var bytes = Utf8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }
    private static void WriteGuid(Stream stream, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        stream.Write(bytes);
    }
    private static void WriteTime(Stream stream, DateTimeOffset value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value.ToUnixTimeMilliseconds());
        stream.Write(bytes);
    }
    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;
        public readonly bool AtEnd => _remaining.IsEmpty;
        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length) { throw new ArgumentException("Invalid binding data."); }
            var result = _remaining[..count];
            _remaining = _remaining[count..];
            return result;
        }
        public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
        public Guid Id() => new(Take(16), bigEndian: true);
        public DateTimeOffset Time() => DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(Take(8)));
        public string Text()
        {
            var length = Int32();
            if (length is < 1 or > MaximumReferenceBytes) { throw new ArgumentException("Invalid binding data."); }
            return Utf8.GetString(Take(length));
        }
    }
}
