using JTS_App.Dialogs;
using JTS_App.Services;
using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JTS_App.Pages;

public sealed partial class ProjectsPage : Page, IRefreshablePage
{
    public ProjectsViewModel ViewModel { get; }

    public ProjectsPage()
    {
        ViewModel = App.Services.GetRequiredService<ProjectsViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            SyncColorPickerWithSelection();
        };
    }

    private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        var selected = args.AddedItems.FirstOrDefault() ?? sender.SelectedItem;
        ViewModel.SelectedNode = selected switch
        {
            ProjectTreeNode node => node,
            TreeViewNode { Content: ProjectTreeNode node } => node,
            TreeViewItem { DataContext: ProjectTreeNode node } => node,
            _ => ViewModel.SelectedNode
        };
        SyncColorPickerWithSelection();
    }

    public async Task RefreshAsync()
    {
        await ViewModel.LoadAsync(forceSync: true);
        SyncColorPickerWithSelection();
    }

    private async void SyncD365_Click(object sender, RoutedEventArgs e) => await ViewModel.SyncFromD365Async();

    private async void AddRelation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedNode is not { } node) return;
        var dialog = new ProjectRelationDialog { XamlRoot = XamlRoot };
        dialog.SetCandidates(ViewModel.AllProjectsFlat.Where(p => p.Id != node.Project.Id));
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.SelectedProject is { } target)
        {
            await ViewModel.AddRelationAsync(node.Project.Id, target.Id, dialog.RelationType, dialog.Note);
            await ViewModel.LoadSelectedDetailsAsync();
        }
    }

    private async void RemoveRelation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
        {
            await ViewModel.RemoveRelationAsync(id);
            await ViewModel.LoadSelectedDetailsAsync();
        }
    }

    private async void SaveProjectColor_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveSelectedProjectColorAsync(ToHex(ProjectColorPicker.Color));
        SyncColorPickerWithSelection();
    }

    private void ProjectColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        SelectedProjectColorPreview.Background = new SolidColorBrush(args.NewColor);
    }

    private void SyncColorPickerWithSelection()
    {
        if (ViewModel.SelectedNode?.Project is not { } project) return;
        var color = ParseColor(ProjectColorService.CardColor(project.ColorHex));
        ProjectColorPicker.Color = color;
        SelectedProjectColorPreview.Background = new SolidColorBrush(color);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ParseColor(string hex)
    {
        var value = hex.Trim().TrimStart('#');
        if (value.Length != 6) return Microsoft.UI.ColorHelper.FromArgb(255, 29, 78, 216);
        return Microsoft.UI.ColorHelper.FromArgb(
            255,
            Convert.ToByte(value[..2], 16),
            Convert.ToByte(value[2..4], 16),
            Convert.ToByte(value[4..6], 16));
    }
}
