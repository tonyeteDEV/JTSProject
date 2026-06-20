using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace JTS_App.Services;

public sealed class GlobalHotkeyService
{
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkT = 0x54;

    private readonly DispatcherTimer _timer;
    private bool _wasPressed;

    public event EventHandler? QuickCaptureRequested;

    public GlobalHotkeyService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var pressed = IsDown(VkControl) && IsDown(VkMenu) && IsDown(VkT);
        if (pressed && !_wasPressed)
        {
            QuickCaptureRequested?.Invoke(this, EventArgs.Empty);
        }

        _wasPressed = pressed;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
