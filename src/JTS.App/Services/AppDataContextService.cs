using System.Text;
using JTS.Core;
using JTS.Data.Entities;

namespace JTS_App.Services;

public sealed class AppDataContextService
{
    private readonly DataverseAppDataService _dataverseData;
    private readonly DataverseVideoAnalysisService _videoAnalysis;

    public AppDataContextService(DataverseAppDataService dataverseData, DataverseVideoAnalysisService videoAnalysis)
    {
        _dataverseData = dataverseData;
        _videoAnalysis = videoAnalysis;
    }

    public async Task<string> BuildAssistantContextAsync()
    {
        var snapshot = await _dataverseData.LoadTaskSnapshotAsync();
        var projects = snapshot.Projects.OrderBy(p => p.Name).Take(80).ToList();
        var tasks = snapshot.Tasks
            .OrderBy(t => t.DueDate)
            .ThenBy(t => t.Status)
            .Take(120)
            .ToList();
        var since = DateTime.UtcNow.AddDays(-14);
        var recentTimeEntries = (await _dataverseData.LoadRecentTimeEntryContextAsync(since, 60)).ToList();
        var recentComments = (await _dataverseData.LoadRecentCommentContextAsync(since, 60)).ToList();
        var recentVideoDocs = (await _videoAnalysis.LoadRecentDocumentationContextAsync(DateTime.UtcNow.AddDays(-60), 80)).ToList();

        var taskByDataverseId = tasks
            .Where(t => t.DataverseId is not null)
            .ToDictionary(t => t.DataverseId!.Value);

        var sb = new StringBuilder();
        sb.AppendLine("App data snapshot:");
        sb.AppendLine("Source of truth: Dataverse only. Ignore any local/cache data.");
        sb.AppendLine("Projects:");
        foreach (var p in projects)
            sb.AppendLine($"- projectId={p.Id} dataverseId={p.DataverseId} {p.Name}");

        sb.AppendLine("Tasks:");
        foreach (var t in tasks)
            sb.AppendLine($"- taskId={t.Id} taskDataverseId={t.DataverseId} [{t.Status}] {t.Title} project={t.Project?.Name ?? "?"} priority={t.Priority} workType={t.WorkType} due={t.DueDate:yyyy-MM-dd} estimate={t.EstimatedPomodoros}");

        sb.AppendLine("Recent tracked time entries:");
        foreach (var item in recentTimeEntries.Select((entry, index) => new { Entry = entry, Index = index + 1 }))
        {
            var entry = item.Entry;
            var task = entry.TaskDataverseId is Guid taskDataverseId && taskByDataverseId.TryGetValue(taskDataverseId, out var mappedTask)
                ? mappedTask
                : null;
            sb.AppendLine($"- timeEntryId={item.Index} timeEntryDataverseId={entry.Id} {entry.StartedAt:u}-{entry.EndedAt:u} taskId={task?.Id.ToString() ?? "unknown"} task={task?.Title ?? "?"} completed=true minutes={entry.ActualMinutes}");
        }

        sb.AppendLine("Recent task comments:");
        foreach (var item in recentComments.Select((comment, index) => new { Comment = comment, Index = index + 1 }))
        {
            var comment = item.Comment;
            var task = comment.TaskDataverseId is Guid taskDataverseId && taskByDataverseId.TryGetValue(taskDataverseId, out var mappedTask)
                ? mappedTask
                : null;
            sb.AppendLine($"- commentId={item.Index} commentDataverseId={comment.Id} {comment.CreatedAt:u} taskId={task?.Id.ToString() ?? "unknown"} task={task?.Title ?? "?"} content={comment.Content}");
        }

        sb.AppendLine("Recent video-generated documentation:");
        foreach (var item in recentVideoDocs.Select((doc, index) => new { Doc = doc, Index = index + 1 }))
        {
            var doc = item.Doc;
            var task = doc.TaskDataverseId is Guid taskDataverseId && taskByDataverseId.TryGetValue(taskDataverseId, out var mappedTask)
                ? mappedTask
                : null;
            sb.AppendLine($"- videoDocId={item.Index} videoDocDataverseId={doc.Id} date={doc.CreatedAt:u} status={doc.StatusText} project={doc.ProjectName} taskId={task?.Id.ToString() ?? "unknown"} task={task?.Title ?? doc.TaskName} sourceVideo={doc.VideoAnalysisName}");
            sb.AppendLine($"  documentation={Compact(doc.Markdown, 1200)}");
        }

        return sb.ToString();
    }

    public async Task<string> BuildVoiceAssistantContextAsync()
    {
        var snapshot = await _dataverseData.LoadTaskSnapshotAsync();
        var today = DateTime.Today;
        var limit = today.AddDays(7);
        var tasks = snapshot.Tasks
            .Where(t => t.Status != TaskItemStatus.Done &&
                        t.Status != TaskItemStatus.Cancelled)
            .Where(t => t.ScheduledStart == null || t.ScheduledStart.Value.Date <= limit)
            .OrderBy(t => t.ScheduledStart ?? t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.Priority)
            .Take(35)
            .ToList();
        var taskDataverseIds = tasks
            .Where(task => task.DataverseId is not null)
            .Select(task => task.DataverseId!.Value)
            .ToHashSet();
        var videoDocs = (await _videoAnalysis.LoadRecentDocumentationContextAsync(DateTime.UtcNow.AddDays(-60), 25))
            .Where(doc => doc.TaskDataverseId is Guid id && taskDataverseIds.Contains(id))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Compact app context for a spoken assistant turn.");
        sb.AppendLine($"Today: {today:yyyy-MM-dd}");
        sb.AppendLine("Open/relevant tasks:");
        foreach (var t in tasks)
            sb.AppendLine($"- #{t.Id} {t.Title} project={t.Project?.Name ?? "?"} status={t.Status} due={t.DueDate:yyyy-MM-dd} scheduled={t.ScheduledStart:yyyy-MM-dd HH:mm}");

        sb.AppendLine("Recent video documentation for these tasks:");
        foreach (var doc in videoDocs)
            sb.AppendLine($"- task={doc.TaskName} project={doc.ProjectName} date={doc.CreatedAt:u} summary={Compact(doc.Markdown, 500)}");

        return sb.ToString();
    }

    private static string Compact(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(empty)";
        var compact = string.Join(
            " ",
            value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        return compact.Length <= maxChars ? compact : compact[..maxChars] + "...";
    }
}
