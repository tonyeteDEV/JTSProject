using JTS.Mobile.Pages;
using JTS.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace JTS.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<DataverseMobileService>();
        builder.Services.AddSingleton<MobileAgentService>();
        builder.Services.AddSingleton<MobileVoiceService>();

        builder.Services.AddSingleton<TasksPage>();
        builder.Services.AddSingleton<FocusPage>();
        builder.Services.AddSingleton<AgentPage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<AppShell>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
