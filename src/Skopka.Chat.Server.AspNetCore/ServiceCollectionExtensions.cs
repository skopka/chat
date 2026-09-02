using Microsoft.Extensions.DependencyInjection.Extensions;
using Skopka.Chat.Attachments;
using Skopka.Chat.Transport.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the authenticated Skopka.Chat ASP.NET Core transport boundary.</summary>
public static class SkopkaChatServiceCollectionExtensions
{
    /// <summary>
    /// Explicitly enables account bootstrap and device-bound chat authorization. The host supplies
    /// IChatAuthorizationContextProvider, IDeviceBindingRepository, IDeviceProofVerifier and named rate limits.
    /// Existing IChatPrincipalMapper implementations are unchanged but do not authorize bound-mode requests.
    /// </summary>
    public static IServiceCollection AddSkopkaChatDeviceBinding(this IServiceCollection services,
        Action<Skopka.Chat.Server.AspNetCore.SkopkaChatDeviceBindingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSkopkaChatAspNetCore();
        services.AddOptions<Skopka.Chat.Server.AspNetCore.SkopkaChatDeviceBindingOptions>().Configure(configure)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ServiceId) && options.ServiceId.Length <= 256 &&
                !string.IsNullOrWhiteSpace(options.ChallengeRateLimitPolicy) && !string.IsNullOrWhiteSpace(options.ProofRateLimitPolicy),
                "Device binding service and rate-limit policy names are required.").ValidateOnStart();
        services.AddScoped<Skopka.Chat.Server.DeviceBindingService>();
        services.AddScoped<Skopka.Chat.Server.AspNetCore.DeviceBindingRequestResolver>();
        services.AddScoped<Skopka.Chat.Server.AspNetCore.IChatRequestIdentityResolver>(provider =>
            provider.GetRequiredService<Skopka.Chat.Server.AspNetCore.DeviceBindingRequestResolver>());
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            Skopka.Chat.Server.AspNetCore.DeviceBindingAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Skopka.Chat.Server.AspNetCore.DeviceBindingPolicies.Account, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new Skopka.Chat.Server.AspNetCore.DeviceBindingRequirement(false)));
            options.AddPolicy(Skopka.Chat.Server.AspNetCore.DeviceBindingPolicies.Device, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new Skopka.Chat.Server.AspNetCore.DeviceBindingRequirement(true)));
        });
        return services;
    }

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
            SkopkaChatHttpJson.Configure(options.SerializerOptions);
        });
        return services;
    }

    /// <summary>
    /// Enables encrypted attachment HTTP endpoints. The host must register exactly one
    /// <see cref="IAttachmentStore"/> and one <see cref="IAttachmentAccessAuthorizer"/>.
    /// </summary>
    public static IServiceCollection AddSkopkaChatAttachmentStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<AttachmentStorageService>();
        return services;
    }
}
