using Microsoft.Extensions.Logging;

namespace Skopka.Chat.Maui.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<ISampleAuthenticationProvider, ConfigureAuthenticationProvider>();
        builder.Services.AddSingleton<SampleChatSessionFactory>();
        builder.Services.AddSingleton<MainPage>();
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
