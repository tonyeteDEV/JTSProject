using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using JTS_App.Pages;
using JTS_App.Services;
using JTS_App.ViewModels;
using Windows.ApplicationModel;

namespace JTS_App;

public sealed partial class MainWindow : Window
{
    public string AppVersionDisplay { get; } = $"v{GetAppVersion()}";
    public DataversePreloadService Preload { get; } = App.Services.GetRequiredService<DataversePreloadService>();
    private bool _closeAfterFocusFlush;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        MaximizeOnStartup();
        AppWindow.Closing += MainWindow_Closing;

        var appearance = App.Services.GetRequiredService<AppAppearanceService>();
        appearance.ApplyToFrameworkResources();
        ApplyFont(appearance.CurrentFontFamily);
        appearance.FontFamilyChanged += (_, fontFamily) =>
        {
            appearance.ApplyToFrameworkResources();
            ApplyFont(fontFamily);
        };

        NavFrame.Navigate(typeof(AssistantPage));
    }

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAfterFocusFlush) return;

        args.Cancel = true;
        _closeAfterFocusFlush = true;
        try
        {
            await App.Services.GetRequiredService<FocusViewModel>().FlushActiveTimeBeforeCloseAsync();
        }
        catch (Exception ex)
        {
            App.Log("[MainWindow] Focus flush before close failed: " + ex);
        }
        finally
        {
            Close();
        }
    }

    private void MaximizeOnStartup()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private static string GetAppVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "dev" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private void ApplyFont(string fontFamily)
    {
        var font = new FontFamily(fontFamily);
        NavView.FontFamily = font;
        NavFrame.FontFamily = font;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private async void RefreshCurrentPage_Click(object sender, RoutedEventArgs e)
    {
        if (NavFrame.Content is not IRefreshablePage page) return;

        RefreshCurrentPageButton.IsEnabled = false;
        try
        {
            await page.RefreshAsync();
        }
        finally
        {
            RefreshCurrentPageButton.IsEnabled = true;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item) return;

        switch (item.Tag)
        {
            case "assistant": NavFrame.Navigate(typeof(AssistantPage)); break;
            case "dashboard": NavFrame.Navigate(typeof(DashboardPage)); break;
            case "tasks":     NavFrame.Navigate(typeof(TasksPage));     break;
            case "planner":   NavFrame.Navigate(typeof(WeeklyPlannerPage)); break;
            case "focus":     NavFrame.Navigate(typeof(FocusPage));     break;
            case "streak":    NavFrame.Navigate(typeof(StreakPage));    break;
            case "video":     NavFrame.Navigate(typeof(VideoAnalysisPage)); break;
            case "customers": NavFrame.Navigate(typeof(CustomersPage)); break;
            case "projects":  NavFrame.Navigate(typeof(ProjectsPage));  break;
            default: throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
        }
    }
}
