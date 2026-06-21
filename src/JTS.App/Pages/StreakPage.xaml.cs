using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JTS_App.Pages;

public sealed partial class StreakPage : Page, IRefreshablePage
{
    public StreakViewModel ViewModel { get; }

    public StreakPage()
    {
        ViewModel = App.Services.GetRequiredService<StreakViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public async Task RefreshAsync() => await ViewModel.LoadAsync(forceSync: true);

    private async void PrevYear_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.GoToPreviousYearAsync();

    private async void NextYear_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.GoToNextYearAsync();

    private void DayCell_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StreakDay day })
            ViewModel.SelectDay(day);
    }
}
