using Skopka.Chat.Client.Maui;
using Skopka.Chat.UI.Maui;

namespace Skopka.Chat.Maui.PackageConsumer;

public static class PackageConsumer
{
    public static IReadOnlyList<string> PublicTypes() =>
    [
        typeof(SecureStorageDeviceKeyStore).FullName!,
        typeof(SecureStorageDeviceIdentityStore).FullName!,
        typeof(SecureStorageBackupKeyStore).FullName!,
        typeof(Skopka.Chat.Client.Storage.ChatBackupCoordinator).FullName!,
        typeof(FileIdentityStorageLock).FullName!,
        typeof(MauiChatLifecycleCoordinator).FullName!,
        typeof(SkopkaChatView).FullName!,
        typeof(MauiChatPresentation).FullName!,
    ];
}
