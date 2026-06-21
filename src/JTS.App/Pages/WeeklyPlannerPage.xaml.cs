using System.Globalization;
using JTS_App.Services;
using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace JTS_App.Pages;

public sealed partial class WeeklyPlannerPage : Page, IRefreshablePage
{
    private const int StartHour = 0;
    private const int EndHour = 24;
    private const int WorkdayStartHour = 9;
    private const int WorkdayEndHour = 18;
    private const int DefaultScrollHour = 8;
    private const int DefaultScrollMinute = 30;
    private const int CalendarSnapMinutes = 15;
    private const double HeaderHeight = 42;
    private const double TimeColumnWidth = 58;
    private const double SlotHeight = 32;
    private const double DayMinWidth = 150;

    private PlannerBlockView? _activeBlock;
    private ResizeMode _resizeMode;
    private bool _isPointerGestureActive;
    private bool _didPointerMove;
    private DateTime _previewStart;
    private DateTime _previewEnd;
    private PlannerTaskView? _draggedTask;
    private PlannerBlockView? _draggedBlock;
    private Border? _dropPreview;
    private readonly List<Border> _dropPreviews = new();
    private DateTime _dropPreviewStart;
    private DateTime _dropPreviewEnd;
    private string? _lastDropPreviewLogKey;
    private bool _isRenderQueued;
    private bool _isBindingsUpdateQueued;
    private readonly PomodoroService _pomodoro;

    public WeeklyPlannerViewModel ViewModel { get; }
    public string WeekLabel => $"{ViewModel.WeekStart.ToString("dd MMM", UiCulture)} - {ViewModel.WeekStart.AddDays(6).ToString("dd MMM yyyy", UiCulture)}";

    public WeeklyPlannerPage()
    {
        ViewModel = App.Services.GetRequiredService<WeeklyPlannerViewModel>();
        _pomodoro = App.Services.GetRequiredService<PomodoroService>();
        InitializeComponent();
        _pomodoro.Tick += Pomodoro_Tick;
        Unloaded += (_, _) => _pomodoro.Tick -= Pomodoro_Tick;
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            ScheduleBindingsUpdate();
            ScheduleRenderCalendar();
            ScrollToDefaultStartTime();
        };
        SizeChanged += (_, _) => ScheduleRenderCalendar();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.WeekStart))
            {
                ScheduleBindingsUpdate();
                ScheduleRenderCalendar();
            }
            else if (e.PropertyName is nameof(ViewModel.SelectedTask) or nameof(ViewModel.SelectedBlock) or nameof(ViewModel.Status))
            {
                ScheduleBindingsUpdate();
                ScheduleRenderCalendar();
            }
        };
        ViewModel.PlannedBlocks.CollectionChanged += (_, _) => ScheduleRenderCalendar();
    }

    private void Pomodoro_Tick(object? sender, EventArgs e)
    {
        ViewModel.RefreshActivePomodoroTime();
        ScheduleBindingsUpdate();
    }

    public async Task RefreshAsync()
    {
        await ViewModel.LoadAsync(forceSync: true);
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private void ScheduleRenderCalendar()
    {
        if (_isRenderQueued) return;
        _isRenderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _isRenderQueued = false;
            RenderCalendar();
        });
    }

    private void ScheduleBindingsUpdate()
    {
        if (_isBindingsUpdateQueued) return;
        _isBindingsUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _isBindingsUpdateQueued = false;
            Bindings.Update();
        });
    }

    private void RenderCalendar()
    {
        if (CalendarGrid is null || CalendarHeaderGrid is null) return;

        CalendarHeaderGrid.Children.Clear();
        CalendarHeaderGrid.RowDefinitions.Clear();
        CalendarHeaderGrid.ColumnDefinitions.Clear();
        CalendarGrid.Children.Clear();
        _dropPreview = null;
        CalendarGrid.RowDefinitions.Clear();
        CalendarGrid.ColumnDefinitions.Clear();

        CalendarHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        for (var i = 0; i < TotalHalfHourSlots; i++)
        {
            CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SlotHeight) });
        }

        CalendarHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeColumnWidth) });
        CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeColumnWidth) });
        for (var day = 0; day < 7; day++)
        {
            CalendarHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = DayMinWidth });
            CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = DayMinWidth });
        }

        DrawHeaders();
        DrawTimeRows();
        DrawScheduleSurfaces();
        DrawTasks();
    }

    private void DrawHeaders()
    {
        var timeHeader = new Border
        {
            BorderBrush = Brush("#343A44"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = Brush("#20252C")
        };
        Grid.SetRow(timeHeader, 0);
        Grid.SetColumn(timeHeader, 0);
        CalendarHeaderGrid.Children.Add(timeHeader);

        for (var day = 0; day < 7; day++)
        {
            var date = ViewModel.WeekStart.AddDays(day);
            var header = new Border
            {
                BorderBrush = Brush("#343A44"),
                BorderThickness = new Thickness(day == 0 ? 1 : 0, 0, 1, 1),
                Background = date.Date == DateTime.Today ? Brush("#21364F") : Brush("#20252C"),
                Child = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = date.ToString("ddd", UiCulture), Foreground = Brush("#DDE4EE"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock { Text = date.ToString("dd"), Foreground = Brush("#A9B4C2"), HorizontalAlignment = HorizontalAlignment.Center }
                    }
                }
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, day + 1);
            CalendarHeaderGrid.Children.Add(header);
        }
    }

    private void DrawTimeRows()
    {
        for (var slot = 0; slot < TotalHalfHourSlots; slot++)
        {
            var row = slot;
            var minutes = StartHour * 60 + slot * 30;
            var isFullHour = minutes % 60 == 0;
            var hour = minutes / 60;
            var time = new Border
            {
                BorderBrush = Brush("#343A44"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = isFullHour ? $"{hour:00}:00" : string.Empty,
                    Foreground = Brush("#A9B4C2"),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 8, 0)
                }
            };
            Grid.SetRow(time, row);
            Grid.SetColumn(time, 0);
            CalendarGrid.Children.Add(time);

            for (var day = 0; day < 7; day++)
            {
                var isWorkdaySlot = minutes >= WorkdayStartHour * 60 && minutes < WorkdayEndHour * 60;
                var cell = new Border
                {
                    BorderBrush = Brush("#343A44"),
                    BorderThickness = new Thickness(day == 0 ? 1 : 0, 0, 1, 1),
                    Background = Brush(isWorkdaySlot ? "#202833" : "#1D2229")
                };

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, day + 1);
                CalendarGrid.Children.Add(cell);
            }
        }
    }

    private void DrawTasks()
    {
        var dayWidth = Math.Max(DayMinWidth, (ActualWidth - 28 - 28 - 310 - 14 - TimeColumnWidth) / 7);
        var layouts = CalculateBlockLayouts(dayWidth);

        foreach (var block in ViewModel.PlannedBlocks)
        {
            var start = block.Start;
            var end = block.End;
            var day = (start.Date - ViewModel.WeekStart).Days;
            if (day is < 0 or > 6) continue;

            var topMinutes = Math.Max(0, (start.Hour * 60 + start.Minute) - StartHour * 60);
            var endMinutes = Math.Min((EndHour - StartHour) * 60, (end.Hour * 60 + end.Minute) - StartHour * 60);
            var top = topMinutes / 30d * SlotHeight;
            var height = Math.Max(8, (endMinutes - topMinutes) / 30d * SlotHeight);
            var layout = layouts.GetValueOrDefault(block.BlockId, new BlockLayout(6, dayWidth - 12));

            var card = CreateTaskCard(block, layout.Width, height);
            card.Margin = new Thickness(layout.Left, top, 0, 0);
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Height = height;
            card.Tag = block;
            card.CanDrag = block.IsEditable;
            if (block.IsEditable)
            {
                card.DragStarting += TaskCard_DragStarting;
            }

            card.Tapped += TaskCard_Tapped;
            card.DoubleTapped += TaskCard_DoubleTapped;

            Grid.SetRow(card, 0);
            Grid.SetRowSpan(card, TotalHalfHourSlots);
            Grid.SetColumn(card, day + 1);
            Canvas.SetZIndex(card, 20);
            CalendarGrid.Children.Add(card);
        }
    }

    private Dictionary<int, BlockLayout> CalculateBlockLayouts(double dayWidth)
    {
        var layouts = new Dictionary<int, BlockLayout>();

        for (var day = 0; day < 7; day++)
        {
            var dayBlocks = ViewModel.PlannedBlocks
                .Where(b => (b.Start.Date - ViewModel.WeekStart).Days == day)
                .OrderBy(b => b.Start)
                .ThenBy(b => b.End)
                .ToList();

            foreach (var group in BuildOverlapGroups(dayBlocks))
            {
                var lanes = new List<DateTime>();
                var laneByBlockId = new Dictionary<int, int>();

                foreach (var block in group.OrderBy(b => b.Start).ThenBy(b => b.End))
                {
                    var lane = 0;
                    while (lane < lanes.Count && lanes[lane] > block.Start)
                    {
                        lane++;
                    }

                    if (lane == lanes.Count) lanes.Add(block.End);
                    else lanes[lane] = block.End;

                    laneByBlockId[block.BlockId] = lane;
                }

                var laneCount = Math.Max(1, lanes.Count);
                var gutter = laneCount > 1 ? 4 : 0;
                var outerMargin = 6d;
                var usableWidth = Math.Max(24, dayWidth - outerMargin * 2 - gutter * (laneCount - 1));
                var laneWidth = usableWidth / laneCount;

                foreach (var block in group)
                {
                    var lane = laneByBlockId[block.BlockId];
                    var left = outerMargin + lane * (laneWidth + gutter);
                    layouts[block.BlockId] = new BlockLayout(left, Math.Max(24, laneWidth));
                }
            }
        }

        return layouts;
    }

    private static List<List<PlannerBlockView>> BuildOverlapGroups(List<PlannerBlockView> blocks)
    {
        var groups = new List<List<PlannerBlockView>>();
        var current = new List<PlannerBlockView>();
        var currentEnd = DateTime.MinValue;

        foreach (var block in blocks)
        {
            if (current.Count == 0 || block.Start < currentEnd)
            {
                current.Add(block);
                if (block.End > currentEnd) currentEnd = block.End;
            }
            else
            {
                groups.Add(current);
                current = new List<PlannerBlockView> { block };
                currentEnd = block.End;
            }
        }

        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    private Border CreateTaskCard(PlannerBlockView block, double width, double height)
    {
        var grid = new Grid();
        FrameworkElement content = height < 58
            ? new TextBlock
            {
                Text = block.IsCompleted ? $"{block.TimeRange}  {block.Title} (done)" : $"{block.TimeRange}  {block.Title}{(block.HasLocalChanges ? " *" : "")}",
                Foreground = Brush("#FFFFFF"),
                FontSize = 12,
                Margin = new Thickness(8, 5, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
            : new StackPanel
            {
                Margin = new Thickness(10, 8, 10, 8),
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = block.Title, Foreground = Brush("#FFFFFF"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = block.IsCompleted ? $"{block.TimeRange} - Done" : block.TimeRange, Foreground = Brush("#FFFFFF"), Opacity = block.IsCompleted ? 0.78 : 0.92, FontSize = 12 },
                    new TextBlock { Text = block.HasLocalChanges ? $"{block.ProjectName} - Pending" : block.ProjectName, Foreground = Brush("#FFFFFF"), Opacity = 0.88, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            };
        grid.Children.Add(content);
        if (block.IsEditable)
        {
            var topGrip = CreateResizeGrip(ResizeMode.Top);
            var bottomGrip = CreateResizeGrip(ResizeMode.Bottom);
            topGrip.PointerPressed += ResizeGrip_PointerPressed;
            topGrip.PointerMoved += ResizeGrip_PointerMoved;
            topGrip.PointerReleased += ResizeGrip_PointerReleased;
            topGrip.PointerCanceled += ResizeGrip_PointerReleased;
            bottomGrip.PointerPressed += ResizeGrip_PointerPressed;
            bottomGrip.PointerMoved += ResizeGrip_PointerMoved;
            bottomGrip.PointerReleased += ResizeGrip_PointerReleased;
            bottomGrip.PointerCanceled += ResizeGrip_PointerReleased;
            grid.Children.Add(topGrip);
            grid.Children.Add(bottomGrip);
        }

        var card = new Border
        {
            Width = width,
            MinHeight = SlotHeight,
            Background = block.IsCompleted ? Brush("#3E4852") : Brush(block.ProjectColorHex),
            BorderBrush = block.IsCompleted ? Brush("#8C98A6") : block.HasLocalChanges ? Brush("#F4D35E") : ViewModel.IsBlockSelected(block.BlockId) ? Brush("#F4D35E") : Brush("#FFFFFF"),
            BorderThickness = ViewModel.IsBlockSelected(block.BlockId) ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = grid
        };

        return card;
    }

    private Border CreateResizeGrip(ResizeMode mode)
    {
        return new Border
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = mode == ResizeMode.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Margin = mode == ResizeMode.Top ? new Thickness(0, -9, 0, 0) : new Thickness(0, 0, 0, -9),
            Background = Brush("#2B3038"),
            BorderBrush = Brush("#E6EDF5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Tag = mode,
            Child = new FontIcon
            {
                Glyph = mode == ResizeMode.Top ? "\uE74A" : "\uE74B",
                FontSize = 10,
                Foreground = Brush("#F3F7FB"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private void DrawScheduleSurfaces()
    {
        for (var day = 0; day < 7; day++)
        {
            var surface = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
                Tag = day
            };
            surface.Tapped += CalendarDay_Tapped;
            Grid.SetRow(surface, 0);
            Grid.SetRowSpan(surface, TotalHalfHourSlots);
            Grid.SetColumn(surface, day + 1);
            Canvas.SetZIndex(surface, 5);
            CalendarGrid.Children.Add(surface);
        }
    }

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlannerTaskView task) SelectTask(task.Id);
    }

    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0 || e.Items[0] is not PlannerTaskView task)
        {
            e.Cancel = true;
            return;
        }

        _draggedTask = task;
        _draggedBlock = null;
        _lastDropPreviewLogKey = null;
        SelectTask(task.Id);
        PlannerLog($"Task drag starting taskId={task.Id} title='{task.Title}'");
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(task.Id.ToString());
        e.Data.Properties.Title = task.Title;
        ViewModel.Status = "Drop the task on the calendar";
    }

    private void CalendarGrid_DragOver(object sender, DragEventArgs e)
    {
        if ((_draggedTask is null && _draggedBlock is null) || !TryGetDropPreview(e.GetPosition(CalendarScroll), out var day, out var start, out var end))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            RemoveDropPreview();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = $"{start:ddd HH:mm} - {end:HH:mm}";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
        LogDropPreviewIfChanged(day, start, end);
        if (_draggedBlock is not null)
        {
            ShowMovePreview(_draggedBlock, day, start, end);
        }
        else if (_draggedTask is not null)
        {
            ShowDropPreview(_draggedTask, day, start, end);
        }
    }

    private void CalendarGrid_DragLeave(object sender, DragEventArgs e)
    {
        PlannerLog("Calendar drag leave");
        _lastDropPreviewLogKey = null;
        RemoveDropPreview();
    }

    private async void CalendarGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var task = _draggedTask;
            if (task is null && e.DataView.Contains(StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                PlannerLog($"Drop data text='{text}'");
                if (int.TryParse(text, out var taskId))
                {
                    task = ViewModel.AssignedTasks.FirstOrDefault(t => t.Id == taskId)
                        ?? ViewModel.PendingTasks.FirstOrDefault(t => t.Id == taskId);
                }
            }

            if (TryGetDropPreview(e.GetPosition(CalendarScroll), out var day, out var start, out var end))
            {
                PlannerLog($"Drop accepted kind={CurrentDragKind()} day={day} start={start:O} end={end:O}");
                if (_draggedBlock is not null)
                {
                    await ViewModel.UpdateDraftBlockAsync(_draggedBlock.BlockId, start, end);
                }
                else if (task is not null)
                {
                    await ViewModel.AddDraftBlockAsync(task.Id, start, end);
                }

                ScheduleBindingsUpdate();
                ScheduleRenderCalendar();
                e.AcceptedOperation = DataPackageOperation.Move;
            }
            else
            {
                PlannerLog("Drop rejected: no preview target");
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }
        catch (Exception ex)
        {
            PlannerLog("Drop failed: " + ex);
            e.AcceptedOperation = DataPackageOperation.None;
            throw;
        }
        finally
        {
            _draggedTask = null;
            _draggedBlock = null;
            _lastDropPreviewLogKey = null;
            RemoveDropPreview();
        }
    }

    private async void CalendarDay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int day }) return;
        if (ViewModel.SelectedTask is null)
        {
            ViewModel.Status = "Select a task first";
            e.Handled = true;
            return;
        }
        if (!ViewModel.SelectedTask.IsSchedulable)
        {
            ViewModel.Status = "Completed tasks are visible as history and cannot be scheduled again";
            e.Handled = true;
            return;
        }

        var position = e.GetPosition((UIElement)sender);
        var start = DateTimeFromCalendarBodyY(day, position.Y);
        if (ViewModel.SelectedBlock is { IsEditable: true } selectedBlock)
        {
            var duration = Math.Max(CalendarSnapMinutes, (int)Math.Round((selectedBlock.End - selectedBlock.Start).TotalMinutes));
            await ViewModel.UpdateDraftBlockAsync(selectedBlock.BlockId, start, start.AddMinutes(duration));
        }
        else
        {
            var end = start.AddMinutes(60);
            await ViewModel.AddDraftBlockAsync(ViewModel.SelectedTask.Id, start, end);
        }
        ScheduleRenderCalendar();
        e.Handled = true;
    }

    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ResizeMode mode } grip) return;
        if (grip.Parent is not FrameworkElement { Parent: Border { Tag: PlannerBlockView block } card }) return;
        if (!block.IsEditable) return;

        _activeBlock = block;
        _resizeMode = mode;
        _isPointerGestureActive = true;
        _didPointerMove = false;
        _previewStart = block.Start;
        _previewEnd = block.End;
        PlannerLog($"Resize starting blockId={block.BlockId} mode={mode} start={block.Start:O} end={block.End:O}");
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TaskCard_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not Border { Tag: PlannerBlockView block }) return;
        if (!block.IsEditable)
        {
            args.Cancel = true;
            return;
        }

        _draggedBlock = block;
        _draggedTask = null;
        _lastDropPreviewLogKey = null;
        SelectBlock(block.BlockId);
        PlannerLog($"Block drag starting blockId={block.BlockId} taskId={block.TaskId} start={block.Start:O} end={block.End:O}");
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText($"block:{block.BlockId}");
        args.Data.Properties.Title = block.Title;
        ViewModel.Status = "Drop the block on the calendar";
    }

    private void TaskCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: PlannerBlockView block })
        {
            SelectBlock(block.BlockId);
            e.Handled = true;
        }
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activeBlock is null || sender is not FrameworkElement grip) return;
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        if (!TryGetTaskCardFromGrip(grip, out var card)) return;

        var column = Grid.GetColumn(card) - 1;

        if (_resizeMode != ResizeMode.None)
        {
            var position = e.GetCurrentPoint(CalendarScroll).Position;
            var contentY = position.Y + CalendarScroll.VerticalOffset;
            var target = DateTimeFromCalendarY(column, contentY);
            if (_resizeMode == ResizeMode.Top)
            {
                if (target < _previewEnd) _previewStart = target;
            }
            else
            {
                if (target > _previewStart) _previewEnd = target;
            }
            PositionCard(card, _previewStart, _previewEnd);
            _didPointerMove = true;
        }
    }

    private async void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement element) element.ReleasePointerCapture(e.Pointer);
        if (_activeBlock is not null && _isPointerGestureActive && _didPointerMove)
        {
            PlannerLog($"Resize released blockId={_activeBlock.BlockId} start={_previewStart:O} end={_previewEnd:O}");
            await ViewModel.UpdateDraftBlockAsync(_activeBlock.BlockId, _previewStart, _previewEnd);
        }

        _activeBlock = null;
        _resizeMode = ResizeMode.None;
        _isPointerGestureActive = false;
        _didPointerMove = false;
        ScheduleRenderCalendar();
    }

    private void TaskCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: PlannerBlockView block })
        {
            SelectBlock(block.BlockId);
        }

        _activeBlock = null;
        _resizeMode = ResizeMode.None;
        _isPointerGestureActive = false;
        _didPointerMove = false;
        e.Handled = true;
    }

    private void PositionCard(Border card, DateTime start, DateTime end)
    {
        var topMinutes = Math.Max(0, (start.Hour * 60 + start.Minute) - StartHour * 60);
        var endMinutes = Math.Min((EndHour - StartHour) * 60, (end.Hour * 60 + end.Minute) - StartHour * 60);
        var top = topMinutes / 30d * SlotHeight;
        var height = Math.Max(8, (endMinutes - topMinutes) / 30d * SlotHeight);
        card.Margin = new Thickness(6, top, 6, 0);
        card.Height = height;
    }

    private bool TryGetDropPreview(Windows.Foundation.Point viewportPosition, out int day, out DateTime start, out DateTime end)
    {
        day = -1;
        start = default;
        end = default;

        if (viewportPosition.X < TimeColumnWidth) return false;

        day = DayFromCalendarX(viewportPosition.X);
        start = DateTimeFromCalendarBodyY(day, viewportPosition.Y + CalendarScroll.VerticalOffset);
        end = start.AddMinutes(CurrentDragDurationMinutes());
        return true;
    }

    private int DayFromCalendarX(double x)
    {
        var dayWidth = Math.Max(DayMinWidth, (CalendarScroll.ActualWidth - TimeColumnWidth) / 7);
        return Math.Clamp((int)((x - TimeColumnWidth) / dayWidth), 0, 6);
    }

    private void ShowMovePreview(PlannerBlockView block, int day, DateTime start, DateTime end)
    {
        if (_dropPreview is null)
        {
            _dropPreview = CreateDropPreviewCard(block.Title, block.ProjectName, "Release to move here");
            Grid.SetRow(_dropPreview, 0);
            Grid.SetRowSpan(_dropPreview, TotalHalfHourSlots);
            Canvas.SetZIndex(_dropPreview, 45);
            CalendarGrid.Children.Add(_dropPreview);
        }

        Grid.SetColumn(_dropPreview, day + 1);
        PositionCard(_dropPreview, start, end);
    }

    private void ShowDropPreview(PlannerTaskView task, int day, DateTime start, DateTime end)
    {
        if (_dropPreview is null)
        {
            _dropPreview = CreateDropPreviewCard(task.Title, task.ProjectName, "Release to schedule here");
            Grid.SetRow(_dropPreview, 0);
            Grid.SetRowSpan(_dropPreview, TotalHalfHourSlots);
            Canvas.SetZIndex(_dropPreview, 40);
            CalendarGrid.Children.Add(_dropPreview);
        }

        _dropPreviewStart = start;
        _dropPreviewEnd = end;
        Grid.SetColumn(_dropPreview, day + 1);
        PositionCard(_dropPreview, _dropPreviewStart, _dropPreviewEnd);
    }

    private Border CreateDropPreviewCard(string title, string projectName, string caption)
    {
        return new Border
        {
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 47, 120, 183)),
            BorderBrush = Brush("#F4D35E"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel
            {
                Margin = new Thickness(10, 8, 10, 8),
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = title, Foreground = Brush("#F3F7FB"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = caption, Foreground = Brush("#F4D35E"), FontSize = 12 },
                    new TextBlock { Text = projectName, Foreground = Brush("#D8E0EA"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };
    }

    private void RemoveDropPreview()
    {
        if (_dropPreview is not null)
        {
            CalendarGrid.Children.Remove(_dropPreview);
            _dropPreview = null;
        }

        foreach (var preview in _dropPreviews)
        {
            CalendarGrid.Children.Remove(preview);
        }

        _dropPreviews.Clear();
    }

    private bool TryGetTaskCardFromGrip(FrameworkElement grip, out Border card)
    {
        card = null!;
        if (grip.Parent is FrameworkElement { Parent: Border { Tag: PlannerBlockView } parentCard })
        {
            card = parentCard;
            return true;
        }

        PlannerLog($"Could not resolve task card from resize grip. sender={grip.GetType().Name}");
        return false;
    }

    private static int DefaultDropDurationMinutes(PlannerTaskView? task)
    {
        if (task is null || task.EstimatedPomodoros <= 0) return 60;
        return Math.Clamp((int)Math.Round(task.EstimatedPomodoros * 60), 30, 180);
    }

    private int CurrentDragDurationMinutes()
    {
        if (_draggedBlock is not null)
        {
            return Math.Max(5, (int)Math.Round((_draggedBlock.End - _draggedBlock.Start).TotalMinutes));
        }

        return DefaultDropDurationMinutes(_draggedTask ?? ViewModel.SelectedTask);
    }

    private string CurrentDragKind() =>
        _draggedBlock is not null ? $"block:{_draggedBlock.BlockId}" :
        _draggedTask is not null ? $"task:{_draggedTask.Id}" :
        "none";

    private void LogDropPreviewIfChanged(int day, DateTime start, DateTime end)
    {
        var key = $"{CurrentDragKind()}|{day}|{start:O}|{end:O}";
        if (key == _lastDropPreviewLogKey) return;
        _lastDropPreviewLogKey = key;
        PlannerLog($"Preview {key} scrollY={CalendarScroll.VerticalOffset:0.##} viewportWidth={CalendarScroll.ActualWidth:0.##}");
    }

    private static void PlannerLog(string message) => App.Log("[WeeklyPlannerPage] " + message);

    private DateTime DateTimeFromCalendarY(int day, double y)
    {
        return DateTimeFromCalendarBodyY(day, y);
    }

    private DateTime DateTimeFromCalendarBodyY(int day, double y)
    {
        var minutes = Math.Round((Math.Max(0, y) / SlotHeight * 30) / (double)CalendarSnapMinutes) * CalendarSnapMinutes;
        minutes = Math.Clamp(minutes, 0, (EndHour - StartHour) * 60 - CalendarSnapMinutes);
        return ViewModel.WeekStart.AddDays(day).AddHours(StartHour).AddMinutes(minutes);
    }

    private void ScrollToDefaultStartTime()
    {
        var offset = ((DefaultScrollHour * 60 + DefaultScrollMinute) - StartHour * 60) / 30d * SlotHeight;
        CalendarScroll.ChangeView(null, Math.Max(0, offset - SlotHeight), null, disableAnimation: true);
    }

    private async void PreviousWeek_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardPendingChangesAsync()) return;
        ViewModel.PreviousWeek();
        await ViewModel.LoadAsync();
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private async void CurrentWeek_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardPendingChangesAsync()) return;
        ViewModel.CurrentWeek();
        await ViewModel.LoadAsync();
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private async void NextWeek_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardPendingChangesAsync()) return;
        ViewModel.NextWeek();
        await ViewModel.LoadAsync();
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private async Task<bool> ConfirmDiscardPendingChangesAsync()
    {
        if (!ViewModel.HasPendingChanges) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Pending changes",
            Content = "You have unconfirmed local changes. Switching weeks will discard them.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SelectTask(int taskId)
    {
        if (!ViewModel.TrySelectTask(taskId)) return;
        ViewModel.Status = ViewModel.SelectedTask?.IsSchedulable == true
            ? "Click a calendar slot to schedule the selected task"
            : "Completed task selected as historical information";
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private void SelectBlock(int blockId)
    {
        if (!ViewModel.TrySelectBlock(blockId)) return;
        ViewModel.Status = ViewModel.SelectedBlock?.IsEditable == true
            ? "Selected one calendar block. Drag it, resize it, or edit its dates below."
            : "Completed calendar block selected as historical information";
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConfirmChangesAsync();
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private async void DeleteBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int blockId })
        {
            await ViewModel.DeleteDraftBlockAsync(blockId);
            ScheduleRenderCalendar();
        }
    }

    private async void ConvertBlockToTime_Click(object sender, RoutedEventArgs e)
    {
        var block = ViewModel.SelectedBlock;
        if (block is null) return;

        var commentBox = new TextBox
        {
            Header = "Task comment",
            PlaceholderText = "What was done during this block? Leave empty to only create tracked time.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 440,
            Children =
            {
                new TextBlock
                {
                    Text = $"{block.Title}\n{block.Start:dddd, dd/MM/yyyy HH:mm} - {block.End:HH:mm}",
                    TextWrapping = TextWrapping.Wrap
                },
                commentBox
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Create completed time?",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await ViewModel.ConvertSelectedBlockToCompletedTimeAsync(commentBox.Text);
            ScheduleBindingsUpdate();
            ScheduleRenderCalendar();
        }
        catch (InvalidOperationException ex)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Could not create completed time",
                Content = ex.Message,
                CloseButtonText = "OK"
            }.ShowAsync();
        }
    }

    private void AssignmentTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || sender is not TextBox textBox) return;
        TryApplyAssignmentTextBox(textBox);
        e.Handled = true;
    }

    private void BlockStartDate_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox) TryApplyAssignmentTextBox(textBox);
    }

    private void BlockStartTime_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox) TryApplyAssignmentTextBox(textBox);
    }

    private void BlockEndDate_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox) TryApplyAssignmentTextBox(textBox);
    }

    private void BlockEndTime_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox) TryApplyAssignmentTextBox(textBox);
    }

    private async void TryApplyAssignmentTextBox(TextBox textBox)
    {
        if (textBox.Tag is not int blockId) return;
        var block = ViewModel.PlannedBlocks.FirstOrDefault(b => b.BlockId == blockId);
        if (block is null) return;
        if (!block.IsEditable)
        {
            ScheduleBindingsUpdate();
            return;
        }

        var row = textBox.Parent as Grid;
        var startDateText = GetAssignmentText(row, 0);
        var startTimeText = GetAssignmentText(row, 1);
        var endDateText = GetAssignmentText(row, 2);
        var endTimeText = GetAssignmentText(row, 3);

        if (!TryParseAssignmentDate(startDateText, block.Start, out var startDate) ||
            !TryParseAssignmentTime(startTimeText, block.Start.TimeOfDay, out var startTime) ||
            !TryParseAssignmentDate(endDateText, block.End, out var endDate) ||
            !TryParseAssignmentTime(endTimeText, block.End.TimeOfDay, out var endTime))
        {
            ScheduleBindingsUpdate();
            return;
        }

        var start = startDate.Add(startTime);
        var end = endDate.Add(endTime);
        if (start == block.Start && end == block.End) return;

        PlannerLog($"Editor text changed blockId={blockId} start={start:O} end={end:O}");
        await ViewModel.UpdateDraftBlockAsync(blockId, start, end);
        ScheduleBindingsUpdate();
        ScheduleRenderCalendar();
    }

    private static string GetAssignmentText(Grid? row, int column)
    {
        if (row is null) return string.Empty;
        foreach (var child in row.Children)
        {
            if (child is TextBox textBox && Grid.GetColumn(textBox) == column) return textBox.Text;
        }

        return string.Empty;
    }

    private static bool TryParseAssignmentDate(string value, DateTime fallback, out DateTime date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = fallback.Date;
            return true;
        }

        return DateTime.TryParseExact(
            value.Trim(),
            ["dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static bool TryParseAssignmentTime(string value, TimeSpan fallback, out TimeSpan time)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            time = fallback;
            return true;
        }

        return TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time);
    }

    private async void BlockStartDate_Changed(object sender, DatePickerValueChangedEventArgs args)
    {
        if (sender is not DatePicker picker || picker.Tag is not int blockId) return;
        var block = ViewModel.PlannedBlocks.FirstOrDefault(b => b.BlockId == blockId);
        if (block is null) return;
        var date = picker.Date.DateTime.Date;
        var start = date.Add(block.Start.TimeOfDay);
        var end = date.Add(block.End.TimeOfDay);
        if (start == block.Start && end == block.End) return;
        PlannerLog($"Editor start date changed blockId={blockId} start={start:O} end={end:O}");
        await ViewModel.UpdateDraftBlockAsync(blockId, start, end);
        ScheduleRenderCalendar();
    }

    private async void BlockEndDate_Changed(object sender, DatePickerValueChangedEventArgs args)
    {
        if (sender is not DatePicker picker || picker.Tag is not int blockId) return;
        var block = ViewModel.PlannedBlocks.FirstOrDefault(b => b.BlockId == blockId);
        if (block is null) return;
        var date = picker.Date.DateTime.Date;
        var end = date.Add(block.End.TimeOfDay);
        if (end == block.End) return;
        PlannerLog($"Editor end date changed blockId={blockId} start={block.Start:O} end={end:O}");
        await ViewModel.UpdateDraftBlockAsync(blockId, block.Start, end);
        ScheduleRenderCalendar();
    }

    private async void BlockStart_Changed(object sender, TimePickerValueChangedEventArgs args)
    {
        if (sender is not TimePicker picker || picker.Tag is not int blockId) return;
        var block = ViewModel.PlannedBlocks.FirstOrDefault(b => b.BlockId == blockId);
        if (block is null) return;
        var start = block.Start.Date.Add(picker.Time);
        if (start == block.Start) return;
        PlannerLog($"Editor start time changed blockId={blockId} start={start:O} end={block.End:O}");
        await ViewModel.UpdateDraftBlockAsync(blockId, start, block.End);
        ScheduleRenderCalendar();
    }

    private async void BlockEnd_Changed(object sender, TimePickerValueChangedEventArgs args)
    {
        if (sender is not TimePicker picker || picker.Tag is not int blockId) return;
        var block = ViewModel.PlannedBlocks.FirstOrDefault(b => b.BlockId == blockId);
        if (block is null) return;
        var end = block.End.Date.Add(picker.Time);
        if (end == block.End) return;
        PlannerLog($"Editor end time changed blockId={blockId} start={block.Start:O} end={end:O}");
        await ViewModel.UpdateDraftBlockAsync(blockId, block.Start, end);
        ScheduleRenderCalendar();
    }

    private static SolidColorBrush Brush(string hex)
    {
        var color = Microsoft.UI.ColorHelper.FromArgb(
            255,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
        return new SolidColorBrush(color);
    }

    private enum ResizeMode
    {
        None,
        Top,
        Bottom
    }

    private sealed record BlockLayout(double Left, double Width);

    private static readonly CultureInfo UiCulture = new("en-GB");

    private static int TotalHalfHourSlots => (EndHour - StartHour) * 2;
}
