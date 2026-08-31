using System.Net;

namespace Skopka.Chat.Client.Http;

/// <summary>Bounded HTTP status, network or response-contract failure.</summary>
public sealed class ChatHttpTransportException : HttpRequestException
{
    /// <summary>Creates a transport failure without copying a response body into the message.</summary>
    public ChatHttpTransportException(
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
    }
}
