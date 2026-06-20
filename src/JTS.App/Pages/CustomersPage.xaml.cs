using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JTS_App.Pages;

public sealed partial class CustomersPage : Page, IRefreshablePage
{
    public CustomersViewModel ViewModel { get; }

    public CustomersPage()
    {
        ViewModel = App.Services.GetRequiredService<CustomersViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    private async void SyncD365_Click(object sender, RoutedEventArgs e) => await ViewModel.SyncFromD365Async();

    public async Task RefreshAsync() => await ViewModel.LoadAsync(forceSync: true);
}
