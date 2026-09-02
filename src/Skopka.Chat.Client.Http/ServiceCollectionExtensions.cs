using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the typed Skopka.Chat HTTP client with safe handler defaults.</summary>
public static class SkopkaChatHttpClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers one transient typed client. The host must separately register
    /// <see cref="Skopka.Chat.Client.Http.IAccessTokenProvider"/>.
    /// </summary>
    /// <remarks>Native bearer registration only. WASM hosts compose their cookie/BFF client with a browser handler.</remarks>
    public static IHttpClientBuilder AddSkopkaChatHttpClient(
        this IServiceCollection services,
        Uri baseAddress,
        Action<Skopka.Chat.Client.Http.SkopkaChatHttpClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(configure);
        if (OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("Use an explicit browser cookie/BFF client.");
        }

        services.AddOptions<Skopka.Chat.Client.Http.SkopkaChatHttpClientOptions>()
            .Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<Skopka.Chat.Client.IDeviceBindingTransport>(provider =>
            provider.GetRequiredService<Skopka.Chat.Client.Http.SkopkaChatHttpClient>());
        services.TryAddTransient<Skopka.Chat.Client.IChatTransport>(provider =>
            provider.GetRequiredService<Skopka.Chat.Client.Http.SkopkaChatHttpClient>());
        services.TryAddTransient<Skopka.Chat.Client.IChatConversationDirectory>(provider =>
            provider.GetRequiredService<Skopka.Chat.Client.Http.SkopkaChatHttpClient>());
        services.TryAddTransient<Skopka.Chat.Client.IRecipientDeviceDirectory>(provider =>
            provider.GetRequiredService<Skopka.Chat.Client.Http.SkopkaChatHttpClient>());
        return services
            .AddHttpClient<Skopka.Chat.Client.Http.SkopkaChatHttpClient>(client =>
            {
                client.BaseAddress = baseAddress;
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                if (OperatingSystem.IsBrowser())
                {
                    throw new PlatformNotSupportedException("A native HTTP handler is required.");
                }
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                };
            });
    }
}
