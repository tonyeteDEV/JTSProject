using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Core;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public partial class ProjectsViewModel : ObservableObject
{
    private readonly DataverseAppDataService _data;
    private IReadOnlyList<TaskItem>? _latestTasks;

    public ObservableCollection<ProjectTreeNode> RootNodes { get; } = new();
    public ObservableCollection<Customer> AllCustomers { get; } = new();
    public ObservableCollection<Project> AllProjectsFlat { get; } = new();
    public ObservableCollection<ProjectRelationView> SelectedProjectRelations { get; } = new();
    public ObservableCollection<TaskItem> SelectedProjectTasks { get; } = new();
    public ObservableCollection<ProjectTimeByDayView> SelectedProjectTimeByDay { get; } = new();

    [ObservableProperty]
    private ProjectTreeNode? _selectedNode;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _selectedProjectTimeTotalText = "Total tracked: 0m";

    [ObservableProperty]
    private bool _isSyncing;

    partial void OnSelectedNodeChanged(ProjectTreeNode? value)
    {
        _ = LoadSelectedDetailsAsync();
    }

    public ProjectsViewModel(DataverseAppDataService data)
    {
        _data = data;
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        var selectedProjectId = SelectedNode?.Project.Id;

        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        var projects = snapshot.Projects.OrderBy(p => p.Name).ToList();
        var tasks = snapshot.Tasks;
        _latestTasks = tasks;
        var customers = Array.Empty<Customer>();

        AllCustomers.Clear();
        foreach (var c in customers) AllCustomers.Add(c);

        AllProjectsFlat.Clear();
        foreach (var p in projects) AllProjectsFlat.Add(p);

        RootNodes.Clear();
        var nodeById = projects.ToDictionary(p => p.Id, p => new ProjectTreeNode { Project = p });
        foreach (var p in projects)
        {
            var node = nodeById[p.Id];
            if (p.ParentProjectId is int pid && nodeById.TryGetValue(pid, out var parent))
                parent.Children.Add(node);
            else
                RootNodes.Add(node);
        }

        var tasksWithDataverseId = tasks.Where(t => t.DataverseId is not null).ToList();
        var entriesByTask = await _data.LoadTimeEntriesByTaskAsync(tasksWithDataverseId.Select(t => t.DataverseId!.Value), forceSync);
        var minutesByProject = new Dictionary<int, int>();
        foreach (var task in tasksWithDataverseId)
        {
            var entries = entriesByTask.TryGetValue(task.DataverseId!.Value, out var cached) ? cached : [];
            minutesByProject[task.ProjectId] = minutesByProject.GetValueOrDefault(task.ProjectId)
                + entries.Sum(e => Math.Max(0, e.ActualMinutes));
        }
        foreach (var (projectId, node) in nodeById)
            node.TotalTrackedText = FormatMinutes(minutesByProject.GetValueOrDefault(projectId));

        SelectedNode = selectedProjectId is int id && nodeById.TryGetValue(id, out var selected)
            ? selected
            : RootNodes.Count == 1 ? RootNodes[0] : null;
        await LoadSelectedDetailsAsync(tasks);
    }

    public async Task LoadSelectedDetailsAsync() => await LoadSelectedDetailsAsync(null);

    private async Task LoadSelectedDetailsAsync(IReadOnlyList<TaskItem>? tasks)
    {
        SelectedProjectRelations.Clear();
        SelectedProjectTasks.Clear();
        SelectedProjectTimeByDay.Clear();
        SelectedProjectTimeTotalText = "Total tracked: 0m";
        if (SelectedNode?.Project is not { } project) return;

        tasks ??= _latestTasks ?? (await _data.LoadTaskSnapshotAsync()).Tasks;
        var projectTasks = tasks.Where(t => t.ProjectId == project.Id).ToList();
        foreach (var task in projectTasks.Where(t => t.ParentTaskId == null).OrderBy(t => t.CreatedAt))
            SelectedProjectTasks.Add(task);

        var entriesByDate = new Dictionary<DateTime, int>();
        var tasksWithDataverseId = projectTasks.Where(t => t.DataverseId is not null).ToList();
        var entriesByTask = await _data.LoadTimeEntriesByTaskAsync(tasksWithDataverseId.Select(t => t.DataverseId!.Value));
        foreach (var task in tasksWithDataverseId)
        {
            var entries = entriesByTask.TryGetValue(task.DataverseId!.Value, out var cached) ? cached : [];
            foreach (var entry in entries)
            {
                var workDate = DisplayFormat.ToSpainTime(entry.StartedAt).Date;
                entriesByDate[workDate] = entriesByDate.GetValueOrDefault(workDate) + Math.Max(0, entry.ActualMinutes);
            }
        }

        foreach (var item in entriesByDate
            .OrderByDescending(pair => pair.Key)
            .Select(pair => new ProjectTimeByDayView(DisplayFormat.Date(pair.Key), FormatMinutes(pair.Value))))
        {
            SelectedProjectTimeByDay.Add(item);
        }

        SelectedProjectTimeTotalText = $"Total tracked: {FormatMinutes(entriesByDate.Values.Sum())}";
    }

    public async Task<Project> AddProjectAsync(string name, string? description, string? colorHex, int? parentProjectId, int? customerId)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Projects are managed in Dataverse.");
    }

    public async Task UpdateProjectAsync(Project project)
    {
        await Task.CompletedTask;
        Status = "Projects are managed in Dataverse.";
    }

    public async Task SaveSelectedProjectColorAsync(string colorHex)
    {
        if (SelectedNode?.Project is not { } selected) return;
        if (selected.DataverseId is not Guid projectId)
        {
            Status = "The selected project does not have a Dataverse id.";
            return;
        }

        await _data.UpdateProjectColorAsync(projectId, colorHex);
        selected.ColorHex = colorHex;
        Status = "Project color saved in Dataverse.";
    }

    public async Task DeleteProjectAsync(Project project)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Projects are managed in Dataverse.");
    }

    public async Task AddRelationAsync(int fromProjectId, int toProjectId, ProjectRelationType type, string? note)
    {
        await Task.CompletedTask;
        Status = "Project relations need a Dataverse table before they can be saved.";
    }

    public async Task RemoveRelationAsync(int relationId)
    {
        await Task.CompletedTask;
        Status = "Project relations need a Dataverse table before they can be removed.";
    }

    public async Task SyncFromD365Async()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        Status = "Syncing D365CE...";
        try
        {
            await LoadAsync();
            Status = "Projects loaded from Dataverse.";
        }
        catch (Exception ex)
        {
            Status = $"D365CE sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours > 0 ? $"{hours}h {remainder:00}m" : $"{remainder}m";
    }
}

public record ProjectRelationView(int Id, string ToProjectName, ProjectRelationType RelationType, string? Note);

public sealed record ProjectTimeByDayView(string DateText, string TimeText);
