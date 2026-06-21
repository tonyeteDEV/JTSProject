using JTS_App.ViewModels;
using JTS.Core;
using JTS.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace JTS_App.Pages;

public sealed partial class AssistantPage : Page, IRefreshablePage
{
    private bool _loaded;
    private bool _enterSubmitHandled;
    public AssistantViewModel ViewModel { get; }

    public AssistantPage()
    {
        ViewModel = App.Services.GetRequiredService<AssistantViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            _loaded = true;
        };
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await ViewModel.SendAsync();

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");
        if (App.MainWindow is { } window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var text = await FileIO.ReadTextAsync(file);
        await ViewModel.ImportCsvAsync(text, file.Name);
    }

    private void RemoveCsvTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CsvTaskPreviewItem item)
            ViewModel.RemoveCsvTask(item);
    }

    private void CancelCsv_Click(object sender, RoutedEventArgs e) => ViewModel.CancelCsvPreview();

    private async void CreateCsvTasks_Click(object sender, RoutedEventArgs e) => await ViewModel.CreateCsvTasksAsync();

    public async Task RefreshAsync() => await ViewModel.LoadAsync(forceSync: true);

    private async void AgentAudio_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleAgentAudioAsync();

    private async void RealtimeVoice_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleRealtimeVoiceAsync();

    private async void ApplyAgentAction_Click(object sender, RoutedEventArgs e) => await ViewModel.ApplyAgentActionAsync();

    private async void PreviewApply_Click(object sender, PendingAgentAction action) => await ViewModel.ApplyAgentActionAsync(action);

    private void PreviewCancel_Click(object sender, PendingAgentAction action) => ViewModel.CancelAgentActionPreview(action);

    private async void PreviewEdit_Click(object sender, PendingAgentAction action)
    {
        var dialogWidth = Math.Clamp(XamlRoot.Size.Width - 180, 680, 1180);
        var dialogHeight = Math.Clamp(XamlRoot.Size.Height - 220, 460, 720);
        var form = BuildPreviewEditForm(action, dialogWidth, dialogHeight);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Modify preview",
            Content = form.Root,
            PrimaryButtonText = "Save preview",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            FullSizeDesired = false,
            MinWidth = Math.Min(920, dialogWidth),
            MaxWidth = dialogWidth
        };
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(760, dialogWidth);
        dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxHeight"] = dialogHeight + 160;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var replaced = await ViewModel.ReplaceAgentActionPreviewAsync(form.ToDto());
        if (!replaced)
            ViewModel.Messages.Add(new ChatMessageView("Assistant", "I couldn't turn those changes into a valid preview."));
    }

    private PreviewEditForm BuildPreviewEditForm(PendingAgentAction action, double dialogWidth, double dialogHeight)
    {
        var compact = dialogWidth < 820;
        var contentWidth = Math.Max(560, dialogWidth - 48);
        var content = new StackPanel
        {
            Spacing = 14,
            Width = contentWidth,
            MaxWidth = 1120,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var root = new ScrollViewer
        {
            Width = contentWidth,
            MaxHeight = dialogHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = content
        };

        var titleBox = TextField("Title", action.Title ?? action.Task?.Title ?? string.Empty);
        var descriptionBox = MultilineField("Description / details", action.Description ?? action.Task?.Description ?? string.Empty);
        var commentBox = MultilineField("Comment", action.Comment ?? string.Empty);

        var projectBox = new ComboBox
        {
            Header = "Project",
            ItemsSource = ViewModel.Projects,
            DisplayMemberPath = nameof(Project.Name),
            SelectedItem = action.Project ?? ViewModel.Projects.FirstOrDefault(p => p.Id == action.Task?.ProjectId),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var priorityBox = EnumBox("Priority", action.Priority, action.Kind is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask or AgentActionKind.UpdateTask);
        var workTypeBox = EnumBox("Work type", action.WorkType, action.Kind is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask or AgentActionKind.UpdateTask);
        var statusBox = EnumBox("Status", action.Status ?? action.Task?.Status ?? TaskItemStatus.Todo, action.Kind == AgentActionKind.UpdateTask);

        var dueEnabled = new CheckBox { Content = "Due date", IsChecked = action.DueDate is not null };
        var dueDate = new CalendarDatePicker
        {
            Date = action.DueDate is DateTime due ? new DateTimeOffset(due) : null,
            IsEnabled = dueEnabled.IsChecked == true,
            FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        dueEnabled.Checked += (_, _) => dueDate.IsEnabled = true;
        dueEnabled.Unchecked += (_, _) => dueDate.IsEnabled = false;

        var showSchedule = action.Kind is AgentActionKind.ScheduleTask or AgentActionKind.CreateAndScheduleTask or AgentActionKind.UpdateCalendar;
        var showTimeEntry = action.Kind is AgentActionKind.AddTimeEntry or AgentActionKind.UpdateTimeEntry;
        var startDate = new CalendarDatePicker { Header = "Start day", Date = action.Start is DateTime start ? new DateTimeOffset(start) : null, FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday, HorizontalAlignment = HorizontalAlignment.Stretch };
        var startTime = new TimePicker { Header = "Start time", Time = action.Start?.TimeOfDay ?? TimeSpan.FromHours(9) };
        var endDate = new CalendarDatePicker { Header = "End day", Date = action.End is DateTime end ? new DateTimeOffset(end) : null, FirstDayOfWeek = Windows.Globalization.DayOfWeek.Monday, HorizontalAlignment = HorizontalAlignment.Stretch };
        var endTime = new TimePicker { Header = "End time", Time = action.End?.TimeOfDay ?? TimeSpan.FromHours(10) };

        content.Children.Add(Section("Action", ReadOnlyField("Change type", FriendlyActionName(action.Kind))));

        if (action.Kind is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask or AgentActionKind.UpdateTask)
        {
            content.Children.Add(Section(
                "Task",
                TwoColumn(
                    compact,
                    titleBox,
                    projectBox,
                    priorityBox,
                    workTypeBox,
                    statusBox,
                    DueDateBlock(dueEnabled, dueDate)),
                descriptionBox));
        }

        if (showSchedule)
        {
            content.Children.Add(Section(
                "Calendar",
                TwoColumn(compact, startDate, startTime, endDate, endTime)));
        }

        if (showTimeEntry)
        {
            content.Children.Add(Section(
                "Time",
                TwoColumn(compact, startDate, startTime, endDate, endTime)));
        }

        if (action.Kind is AgentActionKind.AddTaskComment or AgentActionKind.DeleteTaskComment)
            content.Children.Add(Section("Comment", commentBox));

        if (action.Kind is AgentActionKind.DeleteTask or AgentActionKind.DeleteCalendar or AgentActionKind.DeleteTimeEntry)
            content.Children.Add(Section("Affected item", ReadOnlyField("Item", action.Task?.Title ?? action.Title ?? "Untitled")));

        return new PreviewEditForm(
            root,
            action,
            titleBox,
            descriptionBox,
            commentBox,
            projectBox,
            priorityBox,
            workTypeBox,
            statusBox,
            dueEnabled,
            dueDate,
            startDate,
            startTime,
            endDate,
            endTime);
    }

    private async void Prompt_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSubmitKey(e.Key)) return;

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            return;

        e.Handled = true;
        _enterSubmitHandled = true;
        await ViewModel.SendAsync();
    }

    private async void Prompt_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSubmitKey(e.Key)) return;

        if (_enterSubmitHandled)
        {
            _enterSubmitHandled = false;
            e.Handled = true;
            return;
        }

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            return;

        e.Handled = true;
        ViewModel.Prompt = ViewModel.Prompt.TrimEnd('\r', '\n');
        PromptBox.Text = ViewModel.Prompt;
        await ViewModel.SendAsync();
    }

    private static bool IsSubmitKey(VirtualKey key) =>
        key is VirtualKey.Enter or VirtualKey.Accept;

    private static Border Section(string title, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        foreach (var child in children)
            panel.Children.Add(child);

        return new Border
        {
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 61, 68, 79)),
            Child = panel
        };
    }

    private static Grid TwoColumn(bool compact, params UIElement[] children)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 10
        };
        var columns = compact ? 1 : 2;
        for (var i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < children.Length; i++)
        {
            if (i % columns == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (children[i] is not FrameworkElement child) continue;
            Grid.SetRow(child, i / columns);
            Grid.SetColumn(child, i % columns);
            grid.Children.Add(child);
        }

        return grid;
    }

    private static StackPanel DueDateBlock(CheckBox dueEnabled, CalendarDatePicker dueDate) => new()
    {
        Spacing = 4,
        Children =
        {
            dueEnabled,
            dueDate
        }
    };

    private static TextBox TextField(string label, string value) => new()
    {
        Header = label,
        Text = value,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static TextBox MultilineField(string label, string value) => new()
    {
        Header = label,
        Text = value,
        AcceptsReturn = true,
        MinHeight = 120,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static TextBox ReadOnlyField(string label, string value) => new()
    {
        Header = label,
        Text = value,
        IsReadOnly = true,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static ComboBox EnumBox<T>(string label, T selected, bool enabled) where T : struct, Enum
    {
        var box = new ComboBox
        {
            Header = label,
            ItemsSource = Enum.GetValues<T>(),
            SelectedItem = selected,
            IsEnabled = enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        return box;
    }

    private static string FriendlyActionName(AgentActionKind kind) => kind switch
    {
        AgentActionKind.CreateTask => "Create task",
        AgentActionKind.CreateAndScheduleTask => "Create and schedule task",
        AgentActionKind.UpdateTask => "Update task",
        AgentActionKind.DeleteTask => "Delete task",
        AgentActionKind.ScheduleTask => "Add to calendar",
        AgentActionKind.UpdateCalendar => "Modify calendar",
        AgentActionKind.DeleteCalendar => "Remove from calendar",
        AgentActionKind.AddTaskComment => "Add comment",
        AgentActionKind.DeleteTaskComment => "Delete comment",
        AgentActionKind.AddTimeEntry => "Log time",
        AgentActionKind.UpdateTimeEntry => "Modify time",
        AgentActionKind.DeleteTimeEntry => "Delete time",
        _ => "Action"
    };

    private static string? DateString(CalendarDatePicker picker) =>
        picker.Date is DateTimeOffset value ? value.Date.ToString("yyyy-MM-dd") : null;

    private static string? DateTimeString(CalendarDatePicker datePicker, TimePicker timePicker)
    {
        if (datePicker.Date is not DateTimeOffset date) return null;
        var value = date.Date + timePicker.Time;
        return value.ToString("yyyy-MM-dd HH:mm");
    }

    private sealed record PreviewEditForm(
        ScrollViewer Root,
        PendingAgentAction Action,
        TextBox TitleBox,
        TextBox DescriptionBox,
        TextBox CommentBox,
        ComboBox ProjectBox,
        ComboBox PriorityBox,
        ComboBox WorkTypeBox,
        ComboBox StatusBox,
        CheckBox DueEnabled,
        CalendarDatePicker DueDate,
        CalendarDatePicker StartDate,
        TimePicker StartTime,
        CalendarDatePicker EndDate,
        TimePicker EndTime)
    {
        public AgentActionDto ToDto()
        {
            var selectedProject = ProjectBox.SelectedItem as Project;
            return new AgentActionDto
            {
                Action = Action.Kind.ToString(),
                Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? Action.Title : TitleBox.Text.Trim(),
                Description = DescriptionBox.Text,
                ProjectHint = selectedProject?.Name ?? Action.Project?.Name,
                TaskId = Action.Task?.Id,
                TaskHint = Action.Task?.Title,
                ScheduleBlockId = Action.ScheduleBlockId,
                JournalEntryId = Action.JournalEntryId,
                TimeEntryId = Action.TimeEntryId,
                Comment = CommentBox.Text,
                Start = DateTimeString(StartDate, StartTime),
                End = DateTimeString(EndDate, EndTime),
                DueDate = DueEnabled.IsChecked == true ? DateString(DueDate) : null,
                WorkType = WorkTypeBox.IsEnabled ? WorkTypeBox.SelectedItem?.ToString() : null,
                Priority = PriorityBox.IsEnabled ? PriorityBox.SelectedItem?.ToString() : null,
                Status = StatusBox.IsEnabled ? StatusBox.SelectedItem?.ToString() : null
            };
        }
    }

    private async void VoiceSpeed_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        await ViewModel.SaveVoiceSpeakingRateAsync(e.NewValue);
    }

    private async void DeepSeekModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        await ViewModel.SaveDeepSeekModelAsync((sender as ComboBox)?.SelectedItem as string);
    }

    private async void DeepSeekThinking_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        await ViewModel.SaveDeepSeekThinkingAsync((sender as CheckBox)?.IsChecked == true);
    }
}
