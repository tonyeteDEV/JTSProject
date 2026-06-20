using System.Collections.ObjectModel;
using JTS.Mobile.Services;

namespace JTS.Mobile.Pages;

public partial class TasksPage : ContentPage
{
    private static readonly (string Label, string[] Statuses, string ColorHex)[] Columns =
    [
        ("Pending",    ["Todo", "Backlog"],              "#6B7280"),
        ("Assigned",   ["Assigned"],                    "#3B82F6"),
        ("Ongoing",    ["InProgress", "Blocked"],       "#F59E0B"),
        ("Testing",    ["Testing"],                     "#8B5CF6"),
        ("UAT",        ["UAT"],                         "#EC4899"),
        ("Production", ["Done"],                        "#10B981"),
    ];

    private readonly DataverseMobileService _dataverse;
    private readonly ObservableCollection<MobileTask> _visibleTasks = new();
    private List<MobileTask> _allTasks = [];
    private List<MobileProject> _projects = [];
    private int _selectedColumn = 0;
    private bool _loaded;

    public TasksPage(DataverseMobileService dataverse)
    {
        _dataverse = dataverse;
        InitializeComponent();
        TaskList.ItemsSource = _visibleTasks;
        BuildColumnStrip();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
        {
            _loaded = true;
            await LoadAsync();
        }
    }

    private void BuildColumnStrip()
    {
        ColumnStrip.Children.Clear();
        for (int i = 0; i < Columns.Length; i++)
        {
            var col = Columns[i];
            var idx = i;
            var btn = new Button
            {
                Text = col.Label,
                CornerRadius = 20,
                HeightRequest = 36,
                Padding = new Thickness(14, 0),
                FontSize = 13,
                FontAttributes = FontAttributes.None,
                TextColor = Colors.White,
                BackgroundColor = Color.FromArgb(idx == _selectedColumn ? col.ColorHex : "#252B33"),
            };
            btn.Clicked += (_, _) => SelectColumn(idx);
            ColumnStrip.Children.Add(btn);
        }
    }

    private void SelectColumn(int idx)
    {
        _selectedColumn = idx;
        for (int i = 0; i < ColumnStrip.Children.Count; i++)
        {
            if (ColumnStrip.Children[i] is Button btn)
                btn.BackgroundColor = Color.FromArgb(i == _selectedColumn ? Columns[i].ColorHex : "#252B33");
        }
        ApplyColumnFilter();
        UpdateStatus();
    }

    private async Task LoadAsync()
    {
        try
        {
            StatusLabel.Text = "Syncing...";
            _projects = (await _dataverse.GetProjectsAsync()).ToList();
            _allTasks = (await _dataverse.GetTasksAsync()).ToList();
            ApplyColumnFilter();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Connection error.";
            await DisplayAlertAsync("Dataverse", ex.Message, "OK");
        }
    }

    private void ApplyColumnFilter()
    {
        var statuses = Columns[_selectedColumn].Statuses;
        _visibleTasks.Clear();
        foreach (var task in _allTasks
            .Where(t => statuses.Any(s => string.Equals(t.Status, s, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.PriorityCode)
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.Title))
        {
            _visibleTasks.Add(task);
        }
    }

    private void UpdateStatus()
    {
        var col = Columns[_selectedColumn];
        var count = _visibleTasks.Count;
        StatusLabel.Text = $"{col.Label} · {count} task{(count == 1 ? "" : "s")}";
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await LoadAsync();
        TasksRefresh.IsRefreshing = false;
    }

    private async void OnTaskTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not MobileTask task) return;
        _dataverse.SelectedTask = task;
        await Navigation.PushAsync(new TaskDetailPage(_dataverse));
    }

    private async void OnNewTask(object? sender, EventArgs e)
    {
        if (_projects.Count == 0)
        {
            await DisplayAlertAsync("Project", "Sync projects before creating tasks.", "OK");
            return;
        }

        var title = await DisplayPromptAsync("New task", "Title");
        if (string.IsNullOrWhiteSpace(title)) return;

        var projectName = await DisplayActionSheetAsync("Project", "Cancel", null, _projects.Select(p => p.Name).ToArray());
        if (string.IsNullOrWhiteSpace(projectName) || projectName == "Cancel") return;
        var project = _projects.First(p => p.Name == projectName);

        var description = await DisplayPromptAsync("New task", "Description (optional)", initialValue: string.Empty) ?? string.Empty;

        try
        {
            StatusLabel.Text = "Creating...";
            await _dataverse.CreateTaskAsync(new MobileTaskDraft(project.Id, title, description, "DeepWork", 60, DateTime.Today));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error.";
            await DisplayAlertAsync("JTS", ex.Message, "OK");
        }
    }
}
