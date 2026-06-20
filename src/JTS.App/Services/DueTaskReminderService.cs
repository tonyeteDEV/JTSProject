using System.Globalization;
using JTS.Core;
using JTS.Data.Entities;
using Microsoft.UI.Xaml;

namespace JTS_App.Services;

/// <summary>
/// Periodically checks the (cached) task snapshot for due/overdue tasks and raises
/// toast notifications. Each task notifies at most once per day. Non-critical: any
/// failure is swallowed so reminders never affect the app.
/// </summary>
public sealed class DueTaskReminderService
{
    private readonly DataverseAppDataService _data;
    private readonly NotificationService _notifications;
    private readonly AppSettingsService _settings;
    private readonly DispatcherTimer _timer;
    private bool _checking;

    public DueTaskReminderService(DataverseAppDataService data, NotificationService notifications, AppSettingsService settings)
    {
        _data = data;
        _notifications = notifications;
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = RunInitialCheckAsync();
    }

    public void Stop() => _timer.Stop();

    private async Task RunInitialCheckAsync()
    {
        // Let the Dataverse preload populate the snapshot before the first check.
        await Task.Delay(TimeSpan.FromSeconds(20));
        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            if (!string.Equals(await _settings.GetRemindersEnabledAsync() ?? "true", "true", StringComparison.OrdinalIgnoreCase))
                return;

            var leadDays = int.TryParse(await _settings.GetRemindersLeadDaysAsync(), out var d) ? Math.Clamp(d, 0, 30) : 0;
            var today = DateTime.Today;

            var snapshot = await _data.LoadTaskSnapshotAsync(false);
            var candidates = snapshot.Tasks
                .Where(t => t.Status is not (TaskItemStatus.Done or TaskItemStatus.Cancelled))
                .Where(t => t.DataverseId is not null)
                .Where(t => t.DueDate is DateTime due && due.Date <= today.AddDays(leadDays))
                .OrderBy(t => t.DueDate)
                .ToList();
            if (candidates.Count == 0) return;

            // De-dupe: notify each task at most once per calendar day.
            var (markerDate, notified) = ParseLastNotified(await _settings.GetReminderLastNotifiedAsync());
            if (markerDate != today) notified = new HashSet<string>();

            var fresh = candidates.Where(t => notified.Add(t.DataverseId!.Value.ToString())).ToList();
            if (fresh.Count == 0) return;

            ShowToast(fresh, today);
            await _settings.SetReminderLastNotifiedAsync($"{today:yyyy-MM-dd};{string.Join(",", notified)}");
        }
        catch
        {
            // Reminders are non-critical; never let a toast close the app.
        }
        finally
        {
            _checking = false;
        }
    }

    private void ShowToast(IReadOnlyList<TaskItem> fresh, DateTime today)
    {
        if (fresh.Count == 1)
        {
            var task = fresh[0];
            var due = task.DueDate!.Value.Date;
            var when = due < today ? "overdue" : due == today ? "due today" : $"due {due:dd/MM}";
            _notifications.Show($"Task {when}", task.Title);
            return;
        }

        var overdue = fresh.Count(t => t.DueDate!.Value.Date < today);
        var dueToday = fresh.Count(t => t.DueDate!.Value.Date == today);
        var dueSoon = fresh.Count - overdue - dueToday;

        var parts = new List<string>();
        if (overdue > 0) parts.Add($"{overdue} overdue");
        if (dueToday > 0) parts.Add($"{dueToday} due today");
        if (dueSoon > 0) parts.Add($"{dueSoon} due soon");
        _notifications.Show("Task reminders", string.Join(", ", parts) + ".");
    }

    private static (DateTime Date, HashSet<string> Ids) ParseLastNotified(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (DateTime.MinValue, new());
        var sep = raw.IndexOf(';');
        if (sep < 0 ||
            !DateTime.TryParseExact(raw[..sep], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (DateTime.MinValue, new());

        var ids = raw[(sep + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
        return (date, ids);
    }
}
