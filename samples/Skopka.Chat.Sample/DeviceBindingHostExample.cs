using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Server.NSec;

namespace Skopka.Chat.Sample;

// Compiled integration seam, NOT a token issuer or an authentication scheme.
// The consuming host keeps its normal issuer/audience/signature/lifetime validation.
public static class DeviceBindingHostExample
{
    public static void AddChatBinding(IServiceCollection services, string serviceId,
        Action<DbContextOptionsBuilder> configureDatabase, IValidatedChatSessionCatalog sessions)
    {
        services.AddDbContext<ChatDbContext>(configureDatabase);
        services.AddScoped<PostgreSqlChatStore>();
        services.AddScoped<IDeviceRepository>(provider => provider.GetRequiredService<PostgreSqlChatStore>());
        services.AddScoped<IConversationRepository>(provider => provider.GetRequiredService<PostgreSqlChatStore>());
        services.AddScoped<IEnvelopeRepository>(provider => provider.GetRequiredService<PostgreSqlChatStore>());
        services.AddScoped<IDeviceBindingRepository, PostgreSqlDeviceBindingStore>();
        services.AddSingleton<IDeviceProofVerifier, NSecDeviceProofVerifier>();
        services.AddScoped<ChatServerEngine>();
        services.AddSingleton<IChatAuthorizationContextProvider>(new ValidatedSubSidContext(serviceId, sessions));
        services.AddSkopkaChatDeviceBinding(options => options.ServiceId = serviceId);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("skopka-chat-challenges", http => Limit(http, 10));
            options.AddPolicy("skopka-chat-proofs", http => Limit(http, 20));
        });
    }

    private static RateLimitPartition<string> Limit(HttpContext context, int count)
    {
        var claims = AuthenticatedClaims(context);
        var subject = One(claims, "sub");
        // Bound by authenticated account rather than attacker-controlled device/body values.
        return RateLimitPartition.GetFixedWindowLimiter(subject ?? "unauthenticated", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = count, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    private static Claim[] AuthenticatedClaims(HttpContext http) => http.User.Identities
        .Where(identity => identity.IsAuthenticated).SelectMany(identity => identity.Claims).ToArray();

    private static string? One(Claim[] claims, string name)
    {
        var values = claims.Where(claim => claim.Type == name).Take(2).ToArray();
        return values.Length == 1 ? values[0].Value : null;
    }

    private sealed class ValidatedSubSidContext(string serviceId, IValidatedChatSessionCatalog sessions) : IChatAuthorizationContextProvider
    {
        public async ValueTask<DeviceAuthorizationContext?> GetContextAsync(HttpContext context, CancellationToken cancellationToken = default)
        {
            var claims = AuthenticatedClaims(context);
            var subject = One(claims, "sub");
            var session = One(claims, "sid");
            if (subject is null || session is null) { return null; }
            // Host lookup maps sub to Chat UserId and returns a stable absolute binding deadline.
            // It may additionally check live session revocation. Neither token nor token exp is stored here.
            var account = await sessions.ResolveAsync(subject, session, cancellationToken).ConfigureAwait(false);
            if (account is null) { return null; }
            try { return new DeviceAuthorizationContext(serviceId, account.UserId, session, account.SessionExpiresAt); }
            catch (ArgumentException) { return null; }
        }
    }
}

public sealed record ValidatedChatSession(UserId UserId, DateTimeOffset SessionExpiresAt);

// Implement in the CHAT host using its trusted session policy/catalog, not unvalidated request headers.
// The deadline must remain unchanged across access-token refresh. No Auth claim additions are required.
public interface IValidatedChatSessionCatalog
{
    ValueTask<ValidatedChatSession?> ResolveAsync(string authenticatedSubject, string authenticatedSessionReference,
        CancellationToken cancellationToken = default);
}
