using Skopka.Chat.Client;
using Skopka.Chat.Client.Browser;
using Skopka.Chat.Client.Storage;

namespace Skopka.Chat.Browser.Sample;

// Opt-in composition. The existing HTTP client implements IChatBackupTransport with the same cookie/BFF/CSRF authorizer.
internal static class BackupExample
{
    public static ChatBackupCoordinator Attach(BrowserChatSession session, BrowserVault vault,
        IChatBackupTransport authenticatedApi, IChatCryptographyProvider cryptography)
    {
        var backup = new ChatBackupCoordinator(new BrowserBackupKeyStore(vault), new BrowserBackupWorkspace(vault),
            session.Events, authenticatedApi, new ChatBackupCryptography(cryptography), TimeProvider.System);
        session.AttachBackup(backup);
        return backup;
    }
}
