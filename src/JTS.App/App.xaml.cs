using JTS.Data;
using JTS.AI;
using JTS_App.Services;
using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace JTS_App;

public partial class App : Application
{
    private Window? _window;

    public static IServiceProvider Services { get; private set; } = null!;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        Log("App ctor start");
        ConfigureAppCulture();
        InitializeComponent();
        Log("XAML initialized");
        Services = ConfigureServices();
        Log("Services configured");
        UnhandledException += OnUnhandledException;
        Log("App ctor complete");
    }

    private static void ConfigureAppCulture()
    {
        // English UI with European date order (dd/MM/yyyy) and Monday-first weeks.
        var culture = new CultureInfo("en-GB");
        culture.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Monday;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Log("OnLaunched start");
            await Services.GetRequiredService<AppAppearanceService>().LoadAsync();
            Log("Appearance loaded");
            _window = new MainWindow();
            MainWindow = _window;
            Log("MainWindow created");
            _window.Activate();
            Log("MainWindow activated");
            TryStartBackgroundFeatures();
            Log("Background features started");
            _ = Services.GetRequiredService<DataversePreloadService>().StartAsync();
            Log("Dataverse preload started");
            _ = TryRestoreFocusBarAsync();
        }
        catch (Exception ex)
        {
            Log("OnLaunched failed: " + ex);
            throw;
        }
    }

    private void TryStartBackgroundFeatures()
    {
        try
        {
            Services.GetRequiredService<NotificationService>().Initialize();
            var hotkey = Services.GetRequiredService<GlobalHotkeyService>();
            hotkey.QuickCaptureRequested += (_, _) => ShowQuickCapture();
            hotkey.Start();
            Services.GetRequiredService<DueTaskReminderService>().Start();
        }
        catch
        {
            // Startup must stay resilient; tray/hotkey/notifications are nice-to-have.
            Log("Background feature startup failed");
        }
    }

    private void ShowQuickCapture()
    {
        var quickCaptureWindow = new QuickCaptureWindow();
        quickCaptureWindow.Activate();
    }

    private FocusBarWindow? _focusBarWindow;

    public void ToggleFocusBar()
    {
        if (_focusBarWindow is not null)
        {
            _focusBarWindow.Close();
            return;
        }

        _focusBarWindow = new FocusBarWindow();
        _focusBarWindow.Closed += (_, _) => _focusBarWindow = null;
        _focusBarWindow.Activate();
    }

    private async Task TryRestoreFocusBarAsync()
    {
        try
        {
            var visible = await Services.GetRequiredService<AppSettingsService>().GetFocusBarVisibleAsync();
            if (string.Equals(visible, "true", StringComparison.OrdinalIgnoreCase) && _focusBarWindow is null)
                ToggleFocusBar();
        }
        catch (Exception ex)
        {
            Log("Focus bar restore failed: " + ex);
        }
    }

    private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log("UnhandledException: " + e.Exception);
        try
        {
            var content = _window?.Content as FrameworkElement;
            if (content?.XamlRoot is null) return;
            await new ContentDialog
            {
                XamlRoot = content.XamlRoot,
                Title = "Unexpected error",
                Content = e.Exception.ToString(),
                CloseButtonText = "OK"
            }.ShowAsync();
        }
        catch { /* swallow nested errors */ }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(new DeepSeekClient(new HttpClient()));
        services.AddSingleton<WhisperTranscriber>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<DataverseAppDataService>();
        services.AddSingleton<DataversePreloadService>();
        services.AddSingleton<DataverseTimesheetService>();
        services.AddSingleton<DataverseVideoAnalysisService>();
        services.AddSingleton<VideoProcessingService>();
        services.AddSingleton<AppAppearanceService>();
        services.AddSingleton<AppDataContextService>();
        services.AddSingleton<AudioCaptureService>();
        services.AddSingleton<SpeechPlaybackService>();
        services.AddSingleton<WindowsSpeechDictationService>();
        services.AddSingleton<LocalVoiceAgentService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<DueTaskReminderService>();
        services.AddSingleton<PomodoroAlarmService>();
        services.AddSingleton<PomodoroService>();
        services.AddTransient<CustomersViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ProjectsViewModel>();
        services.AddTransient<TasksViewModel>();
        services.AddTransient<WeeklyPlannerViewModel>();
        services.AddSingleton<FocusViewModel>();
        services.AddTransient<StreakViewModel>();
        services.AddTransient<VideoAnalysisViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<AssistantViewModel>();

        return services.BuildServiceProvider();
    }

    public static void Log(string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.AppDataRoot, "jts-app.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never affect startup.
        }
    }
}
