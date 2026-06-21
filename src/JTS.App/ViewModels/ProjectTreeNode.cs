using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Data.Entities;

namespace JTS_App.ViewModels;

public partial class ProjectTreeNode : ObservableObject
{
    [ObservableProperty]
    private Project _project = null!;

    public ObservableCollection<ProjectTreeNode> Children { get; } = new();

    [ObservableProperty]
    private string _totalTrackedText = "0m";

    public string DisplayName => Project.Name;
    public string Description => Project.Description ?? string.Empty;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Project.Description);
}
