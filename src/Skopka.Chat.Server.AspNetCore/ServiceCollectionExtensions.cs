using Microsoft.Extensions.DependencyInjection.Extensions;
using Skopka.Chat.Transport.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the authenticated Skopka.Chat ASP.NET Core transport boundary.</summary>
public static class SkopkaChatServiceCollectionExtensions
{
    /// <summary>
    /// Registers claims mapping and server time. The host must separately register authentication,
    /// authorization, <see cref="Skopka.Chat.Server.ChatServerEngine"/> and its repositories.
    /// </summary>
    public static IServiceCollection AddSkopkaChatAspNetCore(
        this IServiceCollection services,
        Action<Skopka.Chat.Server.AspNetCore.SkopkaChatHttpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<Skopka.Chat.Server.AspNetCore.SkopkaChatHttpOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<Skopka.Chat.Server.AspNetCore.IChatPrincipalMapper,
            Skopka.Chat.Server.AspNetCore.ClaimsChatPrincipalMapper>();
        services.TryAddSingleton(TimeProvider.System);
        services.ConfigureHttpJsonOptions(options =>
        {
            if (!options.SerializerOptions.TypeInfoResolverChain.Contains(SkopkaChatHttpJsonContext.Default))
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, SkopkaChatHttpJsonContext.Default);
            }
        });
        return services;
    }
}
