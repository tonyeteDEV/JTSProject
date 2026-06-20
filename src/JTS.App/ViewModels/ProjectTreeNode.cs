using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Data.Entities;

namespace JTS_App.ViewModels;

public partial class ProjectTreeNode : ObservableObject
{
    [ObservableProperty]
    private Project _project = null!;

    public ObservableCollection<ProjectTreeNode> Children { get; } = new();

    public string DisplayName => Project.Name;
}
