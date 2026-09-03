using Microsoft.Maui.Storage;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Maui;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Client.Storage.Sqlite;

namespace Skopka.Chat.Maui.Sample;

// Give the result to MauiChatSession(..., resources: null, asyncResources: [backup]); logout must await session disposal.
internal static class BackupExample
{
    public static ChatBackupCoordinator Create(DeviceIdentityScope scope, ISecureStorage secureStorage,
        IIdentityStorageLock identityLock, string protectedDedicatedDatabaseConnectionString, IChatEventStore verifiedEvents,
        IChatBackupTransport authenticatedApi) => new(
            new SecureStorageBackupKeyStore(scope, secureStorage, identityLock),
            new SqliteBackupWorkspace(scope, protectedDedicatedDatabaseConnectionString), verifiedEvents,
            authenticatedApi, new ChatBackupCryptography(), TimeProvider.System);
}
