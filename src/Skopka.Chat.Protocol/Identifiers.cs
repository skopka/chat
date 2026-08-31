namespace Skopka.Chat.Protocol;

/// <summary>Identifies an application user without assuming a single device.</summary>
public readonly record struct UserId(Guid Value)
{
    /// <summary>Creates a new opaque user identifier.</summary>
    public static UserId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies one cryptographic device identity.</summary>
public readonly record struct DeviceId(Guid Value)
{
    /// <summary>Creates a new opaque device identifier.</summary>
    public static DeviceId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies a personal conversation.</summary>
public readonly record struct ConversationId(Guid Value)
{
    /// <summary>Creates a new opaque conversation identifier.</summary>
    public static ConversationId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies one logical message across retries and duplicate deliveries.</summary>
public readonly record struct MessageId(Guid Value)
{
    /// <summary>Creates a new opaque message identifier.</summary>
    public static MessageId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies a version of a device key pair.</summary>
public readonly record struct KeyId(Guid Value)
{
    /// <summary>Creates a new opaque key identifier.</summary>
    public static KeyId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
