using System.Runtime.InteropServices;
using JTS_App.Services;
using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace JTS_App;

public sealed partial class FocusBarWindow : Window
{
    private const int LogicalWidth = 470;
    private const int LogicalHeight = 50;

    private readonly AppSettingsService _settings;
    private bool _dragging;
    private POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;

    public FocusViewModel ViewModel { get; }

    public FocusBarWindow()
    {
        ViewModel = App.Services.GetRequiredService<FocusViewModel>();
        _settings = App.Services.GetRequiredService<AppSettingsService>();
        InitializeComponent();

        App.Services.GetRequiredService<AppAppearanceService>().ApplyToFrameworkResources();

        AppWindow.Title = "JTS Focus";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        RootBar.Loaded += async (_, _) => await ApplySizeAndPositionAsync();
        Closed += (_, _) => _ = _settings.SetFocusBarVisibleAsync(false);

        if (ViewModel.TodayTasks.Count == 0)
            _ = ViewModel.LoadAsync();
        _ = _settings.SetFocusBarVisibleAsync(true);
    }

    private async Task ApplySizeAndPositionAsync()
    {
        var scale = RootBar.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)Math.Round(LogicalWidth * scale);
        var height = (int)Math.Round(LogicalHeight * scale);
        AppWindow.Resize(new SizeInt32(width, height));

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        PointInt32 position;
        if (TryParsePosition(await _settings.GetFocusBarPositionAsync(), out var x, out var y))
        {
            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
            position = new PointInt32(x, y);
        }
        else
        {
            position = new PointInt32(work.X + work.Width - width - 16, work.Y + work.Height - height - 8);
        }

        AppWindow.Move(position);
    }

    private async void TaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskCombo.SelectedItem is FocusTaskOption option)
            await ViewModel.SelectTaskAsync(option);
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await ViewModel.StartTimeTrackingAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e) => await ViewModel.StopTimeTrackingAsync();

    private async void SaveComment_Click(object sender, RoutedEventArgs e)
    {
        var text = CommentBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        await ViewModel.AddJournalEntryAsync(text);
        CommentBox.Text = string.Empty;
        CommentFlyout.Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Drag_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        GetCursorPos(out _dragStartCursor);
        _dragStartWindow = AppWindow.Position;
        DragGrip.CapturePointer(e.Pointer);
    }

    private void Drag_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        GetCursorPos(out var current);
        AppWindow.Move(new PointInt32(
            _dragStartWindow.X + (current.X - _dragStartCursor.X),
            _dragStartWindow.Y + (current.Y - _dragStartCursor.Y)));
    }

    private void Drag_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DragGrip.ReleasePointerCapture(e.Pointer);
        var position = AppWindow.Position;
        _ = _settings.SetFocusBarPositionAsync($"{position.X},{position.Y}");
    }

    private static bool TryParsePosition(string? raw, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var parts = raw.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
