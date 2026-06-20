using System.Globalization;
using JTS.Core;
using JTS.Data.Entities;
using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace JTS_App.Pages;

public sealed partial class TasksPage : Page, IRefreshablePage
{
    private bool _isCompactLayout;
    private bool _updatingStatus;
    private TaskRowView? _draggingTask;
    private readonly KanbanStatusOption[] _statusOptions =
    [
        new(TaskItemStatus.Assigned,   "Assigned"),
        new(TaskItemStatus.InProgress, "Ongoing"),
        new(TaskItemStatus.Testing,    "Testing"),
        new(TaskItemStatus.Tested,     "Tested"),
        new(TaskItemStatus.UAT,        "UAT"),
        new(TaskItemStatus.Done,       "Production"),
    ];

    private TaskCompletionSource<TaskEditorResult?>? _taskEditorCompletion;
    public TasksViewModel ViewModel { get; }

    public TasksPage()
    {
        ViewModel = App.Services.GetRequiredService<TasksViewModel>();
        InitializeComponent();
        StatusBox.ItemsSource = _statusOptions;
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public async Task RefreshAsync() => await ViewModel.LoadAsync(forceSync: true);

    private void TaskCard_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TaskRowView task) return;
        ViewModel.SelectedTask = task;
        _updatingStatus = true;
        StatusBox.SelectedItem = Array.Find(_statusOptions, o => o.Status == DisplayStatus(task.StatusEnum));
        _updatingStatus = false;
    }

    private async void StatusBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingStatus) return;
        if (StatusBox.SelectedItem is not KanbanStatusOption option) return;
        if (ViewModel.SelectedTask is not { } row) return;
        if (option.Status == row.StatusEnum) return;
        if (!ViewModel.CanChangeSelectedTaskStatus) return;
        try
        {
            await ViewModel.SetStatusAsync(row.Id, option.Status);
            await ViewModel.LoadAsync(forceSync: true);
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageAsync("Task locked", ex.Message);
        }
    }

    // Legacy Backlog/Todo tasks are shown under the Assigned column, so reflect that
    // in the "Move to" box selection.
    private static TaskItemStatus DisplayStatus(TaskItemStatus status) => status switch
    {
        TaskItemStatus.Backlog or TaskItemStatus.Todo => TaskItemStatus.Assigned,
        _ => status
    };

    private void KanbanList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggingTask = e.Items.Count > 0 ? e.Items[0] as TaskRowView : null;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void KanbanList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingTask is null) { e.AcceptedOperation = DataPackageOperation.None; return; }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = "Move here";
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void KanbanList_Drop(object sender, DragEventArgs e)
    {
        var task = _draggingTask;
        _draggingTask = null;
        if (task is null || sender is not ListView { Name: var name }) return;

        var targetStatus = name switch
        {
            "AssignedList"   => TaskItemStatus.Assigned,
            "OngoingList"    => TaskItemStatus.InProgress,
            "TestingList"    => TaskItemStatus.Testing,
            "TestedList"     => TaskItemStatus.Tested,
            "UatList"        => TaskItemStatus.UAT,
            "ProductionList" => TaskItemStatus.Done,
            _                => (TaskItemStatus?)null
        };

        if (targetStatus is null || targetStatus == task.StatusEnum) return;
        await ViewModel.SetStatusAsync(task.Id, targetStatus.Value);
        await ViewModel.LoadAsync(forceSync: true);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1120;
        if (compact == _isCompactLayout)
        {
            if (compact)
                TaskDetailPanel.MaxHeight = Math.Max(280, e.NewSize.Height * 0.42);
            return;
        }

        _isCompactLayout = compact;
        if (compact)
        {
            DetailsColumn.Width = new GridLength(0);
            Grid.SetColumnSpan(KanbanBoard, 2);
            Grid.SetRow(TaskDetailPanel, 3);
            Grid.SetColumn(TaskDetailPanel, 0);
            Grid.SetColumnSpan(TaskDetailPanel, 2);
            TaskDetailPanel.Width = double.NaN;
            TaskDetailPanel.MaxHeight = Math.Max(280, e.NewSize.Height * 0.42);
            RootGrid.Padding = new Thickness(20, 18, 20, 20);
        }
        else
        {
            DetailsColumn.Width = GridLength.Auto;
            Grid.SetColumnSpan(KanbanBoard, 1);
            Grid.SetRow(TaskDetailPanel, 2);
            Grid.SetColumn(TaskDetailPanel, 1);
            Grid.SetColumnSpan(TaskDetailPanel, 1);
            TaskDetailPanel.Width = 460;
            TaskDetailPanel.MaxHeight = double.PositiveInfinity;
            RootGrid.Padding = new Thickness(28, 22, 28, 28);
        }
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AllProjects.Count == 0)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "No projects",
                Content = "Create a project first.",
                CloseButtonText = "OK"
            }.ShowAsync();
            return;
        }
        var editor = await ShowTaskEditorAsync(null);
        if (editor is not null && !string.IsNullOrWhiteSpace(editor.TaskTitle))
        {
            await ViewModel.AddTaskAsync(editor.SelectedProject.Id, editor.TaskTitle, editor.Description, editor.WorkType, editor.Priority, 0, editor.DueDate);
            await ViewModel.LoadAsync();
        }
    }

    private async void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not { } row) return;
        if (!ViewModel.CanEditSelectedTask) return;
        var existing = await ViewModel.GetFullTaskAsync(row.Id);
        if (existing is null) return;

        var editor = await ShowTaskEditorAsync(existing);
        if (editor is not null && !string.IsNullOrWhiteSpace(editor.TaskTitle))
        {
            existing.ProjectId = editor.SelectedProject.Id;
            existing.Title = editor.TaskTitle;
            existing.Description = editor.Description;
            existing.WorkType = editor.WorkType;
            existing.Priority = editor.Priority;
            existing.DueDate = editor.DueDate;
            try
            {
                await ViewModel.UpdateTaskAsync(existing);
                await ViewModel.LoadAsync(forceSync: true);
            }
            catch (InvalidOperationException ex)
            {
                await ShowMessageAsync("Task locked", ex.Message);
            }
        }
    }

    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not { } row) return;
        if (!ViewModel.CanDeleteSelectedTask) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete task?",
            Content = $"Delete \"{row.Title}\"? Calendar assignments, comments and tracked time linked to this task will also be deleted from Dataverse.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await ViewModel.DeleteTaskAsync(row.Id);
                await ViewModel.LoadAsync(forceSync: true);
            }
            catch (InvalidOperationException ex)
            {
                await ShowMessageAsync("Task locked", ex.Message);
            }
        }
    }

    private async void AddJournal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not { } row) return;
        var content = JournalBox.Text;
        if (string.IsNullOrWhiteSpace(content)) return;
        await ViewModel.AddJournalEntryAsync(row.Id, content);
        JournalBox.Text = string.Empty;
        await ViewModel.LoadJournalAsync();
    }

    private async void EditTimeEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskTimeByDayView entry) return;
        if (!entry.IsEditable) return;

        var startDatePicker = new CalendarDatePicker
        {
            Header = "Start date",
            Date = new DateTimeOffset(entry.StartedAtSpain.Date),
            FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var startPicker = new TimePicker
        {
            Header = "Start time",
            Time = entry.StartedAtSpain.TimeOfDay
        };
        var endDatePicker = new CalendarDatePicker
        {
            Header = "End date",
            Date = new DateTimeOffset(entry.EndedAtSpain.Date),
            FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var endPicker = new TimePicker
        {
            Header = "End time",
            Time = entry.EndedAtSpain.TimeOfDay
        };

        var form = new StackPanel { Spacing = 12, MinWidth = 360 };
        form.Children.Add(startDatePicker);
        form.Children.Add(startPicker);
        form.Children.Add(endDatePicker);
        form.Children.Add(endPicker);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Edit tracked time",
            Content = form,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            startDatePicker.Date is not DateTimeOffset startDate ||
            endDatePicker.Date is not DateTimeOffset endDate)
            return;

        var startedAtSpain = startDate.Date.Add(startPicker.Time);
        var endedAtSpain = endDate.Date.Add(endPicker.Time);
        if (endedAtSpain <= startedAtSpain)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Invalid time range",
                Content = "End date and time must be after the start.",
                CloseButtonText = "OK"
            }.ShowAsync();
            return;
        }

        var duration = Math.Max(1, (int)Math.Round((endedAtSpain - startedAtSpain).TotalMinutes));
        try
        {
            await ViewModel.UpdateTimeEntryAsync(entry.DataverseId, startedAtSpain, duration);
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageAsync("Tracked time locked", ex.Message);
        }
    }

    private async void DeleteTimeEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskTimeByDayView entry) return;
        if (!entry.IsEditable) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete tracked time?",
            Content = $"Delete {entry.DateText} {entry.TimeRangeText} ({entry.DurationText}) from Dataverse?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await ViewModel.DeleteTimeEntryAsync(entry.DataverseId);
            }
            catch (InvalidOperationException ex)
            {
                await ShowMessageAsync("Tracked time locked", ex.Message);
            }
        }
    }

    private async void DeleteJournal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskJournalEntryView entry) return;
        if (!entry.IsEditable || entry.DataverseId is not Guid commentId) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete comment?",
            Content = $"Delete this comment from Dataverse?\n\n{entry.Content}",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await ViewModel.DeleteJournalEntryAsync(commentId);
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageAsync("Comment locked", ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        }.ShowAsync();
    }

    private Task<TaskEditorResult?> ShowTaskEditorAsync(TaskItem? existing)
    {
        var choices = ViewModel.AllProjects
            .Select(project => new ProjectChoice(project, FormatProjectChoice(project)))
            .ToList();

        TaskEditorHeading.Text = existing is null ? "New task" : "Edit task";
        TaskProjectBox.ItemsSource = choices;
        TaskProjectBox.SelectedItem = existing is null
            ? choices.FirstOrDefault()
            : choices.FirstOrDefault(choice => choice.Project.Id == existing.ProjectId) ?? choices.FirstOrDefault();

        TaskTitleBox.Text = existing?.Title ?? string.Empty;
        TaskDescriptionBox.Text = existing?.Description ?? string.Empty;

        TaskWorkTypeBox.ItemsSource = Enum.GetValues<WorkType>();
        TaskWorkTypeBox.SelectedItem = existing?.WorkType ?? WorkType.DeepWork;

        TaskPriorityBox.ItemsSource = Enum.GetValues<TaskPriority>();
        TaskPriorityBox.SelectedItem = existing?.Priority ?? TaskPriority.Medium;

        var scheduledMinutes = existing?.ScheduleBlocks.Sum(b => Math.Max(0, (int)Math.Round((b.End - b.Start).TotalMinutes))) ?? 0;
        TaskPomodoroSummaryText.Text = FormatPomodoroSummary(scheduledMinutes, existing?.EstimatedPomodoros ?? 0);
        TaskDueBox.Date = existing?.DueDate is DateTime due ? new DateTimeOffset(due) : null;
        UpdateTaskDueDayText();

        _taskEditorCompletion = new TaskCompletionSource<TaskEditorResult?>();
        TaskEditorOverlay.Visibility = Visibility.Visible;
        TaskTitleBox.Focus(FocusState.Programmatic);
        return _taskEditorCompletion.Task;
    }

    private void SaveTaskEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_taskEditorCompletion is null) return;
        if (TaskProjectBox.SelectedItem is not ProjectChoice choice || string.IsNullOrWhiteSpace(TaskTitleBox.Text)) return;

        var result = new TaskEditorResult(
            choice.Project,
            TaskTitleBox.Text.Trim(),
            string.IsNullOrWhiteSpace(TaskDescriptionBox.Text) ? null : TaskDescriptionBox.Text,
            (WorkType)(TaskWorkTypeBox.SelectedItem ?? WorkType.DeepWork),
            (TaskPriority)(TaskPriorityBox.SelectedItem ?? TaskPriority.Medium),
            TaskDueBox.Date?.DateTime.Date);

        CompleteTaskEditor(result);
    }

    private void CancelTaskEditor_Click(object sender, RoutedEventArgs e) => CompleteTaskEditor(null);

    private void CompleteTaskEditor(TaskEditorResult? result)
    {
        TaskEditorOverlay.Visibility = Visibility.Collapsed;
        _taskEditorCompletion?.TrySetResult(result);
        _taskEditorCompletion = null;
    }

    private void TaskDueBox_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) =>
        UpdateTaskDueDayText();

    private void UpdateTaskDueDayText()
    {
        TaskDueDayText.Text = TaskDueBox.Date is DateTimeOffset due
            ? due.DateTime.ToString("dddd, d MMMM yyyy", new CultureInfo("en-GB"))
            : "No due date";
    }

    private static string FormatProjectChoice(Project project)
    {
        var parts = new[] { project.Name, project.Customer?.Name, project.Description }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return string.Join(" - ", parts);
    }

    private static string FormatPomodoroSummary(int scheduledMinutes, int storedPomodoros)
    {
        var calculatedPomodoros = scheduledMinutes <= 0
            ? storedPomodoros
            : Math.Round((scheduledMinutes / 60d) * 2, MidpointRounding.AwayFromZero) / 2d;

        if (calculatedPomodoros <= 0) return "Calculated from calendar assignments";

        var formatted = calculatedPomodoros.ToString("0.#", CultureInfo.InvariantCulture);
        var suffix = calculatedPomodoros == 1 ? "pomodoro" : "pomodoros";
        return scheduledMinutes > 0
            ? $"{formatted} {suffix} from assigned time"
            : $"{formatted} {suffix} until calendar assignments change";
    }

    private sealed record ProjectChoice(Project Project, string DisplayText);

    private sealed record TaskEditorResult(
        Project SelectedProject,
        string TaskTitle,
        string? Description,
        WorkType WorkType,
        TaskPriority Priority,
        DateTime? DueDate);

    private sealed record KanbanStatusOption(TaskItemStatus Status, string Label)
    {
        public override string ToString() => Label;
    }
}
