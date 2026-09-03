using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;

namespace Skopka.Chat.Sample;

// Composition only: the host supplies normal Auth, IChatAuthorizationContextProvider, account/CSRF policies and rate limits.
internal static class BackupHostExample
{
    public static PostgreSqlBackupStorage Register(IServiceCollection services, string protectedConnectionString)
    {
        var storage = new PostgreSqlBackupStorage(protectedConnectionString);
        services.AddSingleton<IChatBackupStorage>(storage);
        services.AddSingleton(provider => new ChatBackupService(provider.GetRequiredService<IChatBackupStorage>(), TimeProvider.System,
            new ChatBackupServerOptions { MaximumBytes = 1L << 30, MaximumPendingUploads = 4, PendingLifetime = TimeSpan.FromDays(7) }));
        // Return for an explicitly authorized deployment migration: await storage.MigrateAsync(token).
        return storage;
    }
    public static void Map(WebApplication app, string configuredServiceId) =>
        app.MapSkopkaChatBackups(configuredServiceId, "YourAuthenticatedChatAccountPolicy", "YourBackupConcurrencyPolicy");
}
