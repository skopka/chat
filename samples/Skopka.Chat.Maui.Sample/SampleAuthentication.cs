using Skopka.Chat.Client.Http;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Maui.Sample;

public sealed record SampleAuthentication(
    Uri ServerBaseAddress,
    UserId UserId,
    DeviceId DeviceId,
    UserId PeerUserId,
    ChatAccessToken AccessToken);

public interface ISampleAuthenticationProvider
{
    ValueTask<SampleAuthentication> AuthenticateAsync(CancellationToken cancellationToken = default);
}

public sealed class ConfigureAuthenticationProvider : ISampleAuthenticationProvider
{
    public ValueTask<SampleAuthentication> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "Replace ConfigureAuthenticationProvider with host authentication that validates token user/device claims.");
    }
}

internal sealed class FixedAccessTokenProvider(ChatAccessToken token) : IAccessTokenProvider
{
    public ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(token);
    }
}
