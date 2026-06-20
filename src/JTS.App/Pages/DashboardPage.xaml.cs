using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace JTS_App.Pages;

public sealed partial class DashboardPage : Page, IRefreshablePage
{
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public async Task RefreshAsync() => await ViewModel.LoadAsync(forceSync: true);

    private async void GenerateTimesheetPreview_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ViewModel.BuildTimesheetPreviewAsync();
    }

    private async void CreateTimesheet_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ViewModel.RegisterTimesheetAsync();
    }
}
