using JTS.Core;
using Microsoft.UI.Xaml;

namespace JTS_App.Services;

public sealed class PomodoroService
{
    private readonly DispatcherTimer _timer;

    public event EventHandler? Tick;
    public event EventHandler? Completed;

    public PomodoroSessionType SessionType { get; private set; } = PomodoroSessionType.Work;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(25);
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(25);
    public int? ActiveTaskId { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public TimeSpan ActiveElapsed => HasActiveSession
        ? Duration - Remaining
        : TimeSpan.Zero;
    public bool IsRunning { get; private set; }
    public bool HasActiveSession { get; private set; }

    public PomodoroService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
    }

    public void Start(PomodoroSessionType sessionType, TimeSpan duration, int? taskId = null)
    {
        SessionType = sessionType;
        Duration = duration;
        Remaining = duration;
        ActiveTaskId = taskId;
        StartedAt = DateTime.UtcNow;
        HasActiveSession = true;
        IsRunning = true;
        _timer.Start();
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Restore(PomodoroSessionType sessionType, TimeSpan duration, TimeSpan remaining, int? taskId = null)
    {
        _timer.Stop();
        SessionType = sessionType;
        Duration = duration;
        Remaining = remaining <= TimeSpan.Zero ? duration : remaining;
        ActiveTaskId = taskId;
        StartedAt = null;
        HasActiveSession = true;
        IsRunning = false;
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void SetReady(PomodoroSessionType sessionType, TimeSpan duration)
    {
        if (HasActiveSession) return;
        SessionType = sessionType;
        Duration = duration;
        Remaining = duration;
        ActiveTaskId = null;
        StartedAt = null;
        IsRunning = false;
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!HasActiveSession) return;
        _timer.Stop();
        IsRunning = false;
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (!HasActiveSession || IsRunning) return;
        _timer.Start();
        IsRunning = true;
        Tick?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        _timer.Stop();
        IsRunning = false;
        HasActiveSession = false;
        ActiveTaskId = null;
        StartedAt = null;
        Remaining = Duration;
        Tick?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimerTick(object? sender, object e)
    {
        if (!HasActiveSession) return;

        Remaining = Remaining.Subtract(TimeSpan.FromSeconds(1));
        if (Remaining <= TimeSpan.Zero)
        {
            Remaining = TimeSpan.Zero;
            _timer.Stop();
            IsRunning = false;
            HasActiveSession = false;
            ActiveTaskId = null;
            StartedAt = null;
            Tick?.Invoke(this, EventArgs.Empty);
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }
}
