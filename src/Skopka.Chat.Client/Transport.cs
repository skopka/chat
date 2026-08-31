using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Result of a transport-neutral send attempt.</summary>
public enum TransportSendStatus
{
    /// <summary>The server accepted a new message ID.</summary>
    Accepted,
    /// <summary>The identical envelope was already accepted.</summary>
    Duplicate
}

/// <summary>Transport-neutral encrypted delivery record.</summary>
public sealed record TransportDelivery(EncryptedEnvelope Envelope, DateTimeOffset AcceptedAt);

/// <summary>Client boundary implemented by HTTP, SignalR, WebSocket or an in-process sample adapter.</summary>
public interface IChatTransport
{
    /// <summary>Obtains current public data for one device.</summary>
    ValueTask<PublicDevice?> GetDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default);

    /// <summary>Sends one already-encrypted envelope.</summary>
    ValueTask<TransportSendStatus> SendAsync(EncryptedEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>Receives a bounded batch addressed to one device.</summary>
    ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(
        DeviceId recipientDeviceId,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges delivery without revealing plaintext.</summary>
    ValueTask AcknowledgeAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default);
}
