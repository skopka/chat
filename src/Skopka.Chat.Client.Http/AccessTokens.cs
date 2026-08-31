namespace Skopka.Chat.Client.Http;

/// <summary>Bearer access token whose textual representation is always redacted.</summary>
public sealed class ChatAccessToken
{
    private readonly string _value;

    /// <summary>Creates a token optionally carrying a trusted expiry supplied by the host.</summary>
    public ChatAccessToken(string value, DateTimeOffset? expiresAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 8 * 1024 || !IsBearerToken(value))
        {
            throw new ArgumentException("The access token is not a bounded RFC 6750 bearer token.", nameof(value));
        }

        _value = value;
        ExpiresAt = expiresAt;
    }

    /// <summary>Trusted expiry associated with the token, when known by its provider.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    internal string Value => _value;

    /// <inheritdoc />
    public override string ToString() => "[REDACTED ACCESS TOKEN]";

    private static bool IsBearerToken(string value)
    {
        var paddingStarted = false;
        var hasTokenCharacter = false;
        foreach (var character in value)
        {
            if (character == '=')
            {
                if (!hasTokenCharacter)
                {
                    return false;
                }

                paddingStarted = true;
                continue;
            }

            if (paddingStarted ||
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }

            hasTokenCharacter = true;
        }

        return hasTokenCharacter;
    }
}

/// <summary>Host boundary that supplies a current access token for each HTTP attempt.</summary>
public interface IAccessTokenProvider
{
    /// <summary>Returns a token without logging, caching or parsing it in the transport package.</summary>
    ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown before network I/O when a token is missing, malformed or already expiring.</summary>
public sealed class ChatAccessTokenException : InvalidOperationException
{
    /// <summary>Creates a bounded failure that never embeds the token value.</summary>
    public ChatAccessTokenException(string message) : base(message)
    {
    }
}
