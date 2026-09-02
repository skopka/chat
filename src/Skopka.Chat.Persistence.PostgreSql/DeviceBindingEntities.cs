namespace Skopka.Chat.Persistence.PostgreSql;

internal sealed class DeviceChallengeEntity
{
    public Guid ChallengeId { get; set; }
    public byte[] Payload { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset SessionExpiresAt { get; set; }
    public byte[]? Signature { get; set; }
    public DateTimeOffset? BoundAt { get; set; }
}

internal sealed class DeviceSessionEntity
{
    public string ServiceId { get; set; } = "";
    public Guid UserId { get; set; }
    public string SessionReference { get; set; } = "";
    public Guid DeviceId { get; set; }
    public Guid KeyId { get; set; }
    public DateTimeOffset BoundAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
