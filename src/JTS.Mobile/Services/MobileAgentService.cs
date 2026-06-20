using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JTS.Mobile.Services;

public sealed class MobileAgentService
{
    private readonly HttpClient _http = new();
    private readonly List<AgentLlmMessage> _history = new();

    public MobileAgentService() { }

    public async Task<AgentTurnResult> SendAsync(
        string text,
        MobileSettings settings,
        IReadOnlyList<MobileProject> projects,
        IReadOnlyList<MobileTask> tasks,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new AgentTurnResult("Tell me what you need and I'll prepare the preview before touching anything.", null);

        if (!string.IsNullOrWhiteSpace(settings.DeepSeekApiKey))
        {
            try
            {
                var result = await SendToDeepSeekAsync(text, settings, projects, tasks, cancellationToken);
                Remember("user", text);
                Remember("assistant", result.Message);
                return result;
            }
            catch
            {
                // Keep the app useful if the model is temporarily unavailable.
            }
        }

        var local = BuildLocalPreview(text, projects, tasks);
        var response = local is null
            ? "I understood you, but I need a bit more detail to prepare a preview. For example: create a task, schedule this task tomorrow from 10 to 11, or add a comment."
            : BuildPreviewMessage(local);
        return new AgentTurnResult(response, local);
    }

    public string BuildEditableText(AgentActionPreview preview) =>
        JsonSerializer.Serialize(preview.ToDto(), JsonOptions);

    public AgentActionPreview? FromEditableText(string text, IReadOnlyList<MobileProject> projects, IReadOnlyList<MobileTask> tasks)
    {
        var dto = JsonSerializer.Deserialize<AgentActionDto>(text, JsonOptions);
        return dto is null ? null : BuildPreview(dto, projects, tasks);
    }

    private async Task<string> ChatAsync(
        string apiKey,
        string model,
        bool thinking,
        IReadOnlyList<AgentLlmMessage> messages,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            thinking = new { type = thinking ? "enabled" : "disabled" },
            stream = false
        });

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private async Task<AgentTurnResult> SendToDeepSeekAsync(
        string text,
        MobileSettings settings,
        IReadOnlyList<MobileProject> projects,
        IReadOnlyList<MobileTask> tasks,
        CancellationToken cancellationToken)
    {
        var messages = new List<AgentLlmMessage>
        {
            new("system", AgentPrompt),
            new("system", BuildContext(projects, tasks)),
        };
        messages.AddRange(_history.TakeLast(8));
        messages.Add(new AgentLlmMessage("user", text));

        var model = string.IsNullOrWhiteSpace(settings.DeepSeekModel) ? "deepseek-v4-flash" : settings.DeepSeekModel;
        var response = await ChatAsync(settings.DeepSeekApiKey, model, settings.DeepSeekThinking, messages, cancellationToken);

        var cleaned = ExtractJson(response);
        var dto = JsonSerializer.Deserialize<AgentActionDto>(cleaned, JsonOptions);
        var preview = dto is null ? null : BuildPreview(dto, projects, tasks);
        if (preview is null)
        {
            var answer = string.IsNullOrWhiteSpace(dto?.Message)
                ? response.Trim()
                : dto.Message.Trim();
            return new AgentTurnResult(answer, null);
        }

        return new AgentTurnResult(BuildPreviewMessage(preview), preview);
    }

    private static AgentActionPreview? BuildPreview(
        AgentActionDto dto,
        IReadOnlyList<MobileProject> projects,
        IReadOnlyList<MobileTask> tasks)
    {
        var action = NormalizeAction(dto.Action);
        if (action == AgentActionKind.None) return null;

        var project = ResolveProject(dto.ProjectId, dto.ProjectName, projects);
        var task = ResolveTask(dto.TaskId, dto.TaskTitle, tasks);

        if (action is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask && project is null)
            project = projects.FirstOrDefault();
        if (action is not (AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask) && task is null)
            return null;

        var start = ParseDateTime(dto.Start);
        var end = ParseDateTime(dto.End);
        var due = ParseDateTime(dto.DueDate)?.Date;
        if (action is AgentActionKind.CreateAndScheduleTask or AgentActionKind.ScheduleTask or AgentActionKind.UpdateCalendar)
        {
            if (start is null) return null;
            end ??= start.Value.AddHours(1);
        }

        var title = string.IsNullOrWhiteSpace(dto.Title) ? task?.Title ?? "New task" : dto.Title.Trim();
        var description = dto.Description?.Trim();
        if (action == AgentActionKind.UpdateTask && description is null && task is not null)
            description = task.Description;

        var workType = string.IsNullOrWhiteSpace(dto.WorkType) ? task?.WorkType ?? "DeepWork" : dto.WorkType.Trim();
        var status = string.IsNullOrWhiteSpace(dto.Status) ? task?.Status : dto.Status.Trim();
        var estimate = dto.EstimatedMinutes is > 0 ? dto.EstimatedMinutes.Value : 0;
        var comment = dto.Comment?.Trim();

        if (action == AgentActionKind.AddTaskComment && string.IsNullOrWhiteSpace(comment)) return null;

        return new AgentActionPreview(
            action,
            project,
            task,
            title,
            description ?? string.Empty,
            workType,
            status,
            due,
            estimate,
            start,
            end,
            comment ?? string.Empty);
    }

    private static AgentActionPreview? BuildLocalPreview(string text, IReadOnlyList<MobileProject> projects, IReadOnlyList<MobileTask> tasks)
    {
        var lower = text.ToLowerInvariant();
        var task = ResolveTask(null, text, tasks);
        var project = ResolveProject(null, text, projects) ?? projects.FirstOrDefault();

        if ((lower.Contains("comment") || lower.Contains("note")) && task is not null)
        {
            var comment = text;
            var marker = lower.Contains("that ") ? lower.IndexOf("that ", StringComparison.Ordinal) + 5 : -1;
            if (marker > 0 && marker < text.Length) comment = text[marker..];
            return new AgentActionPreview(AgentActionKind.AddTaskComment, null, task, task.Title, string.Empty, task.WorkType, null, null, 0, null, null, comment.Trim());
        }

        if ((lower.Contains("schedule") || lower.Contains("calendar") || lower.Contains("plan")) && task is not null)
        {
            var start = InferDate(text);
            if (start is not null)
                return new AgentActionPreview(AgentActionKind.ScheduleTask, null, task, task.Title, string.Empty, task.WorkType, null, task.DueDate, 0, start, start.Value.AddHours(1), string.Empty);
        }

        if (lower.Contains("create") || lower.Contains("new task") || lower.Contains("add task"))
        {
            var title = text.Replace("create me", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("create", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("a task", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("new task", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim(' ', '.', ':');
            if (string.IsNullOrWhiteSpace(title)) title = "New task";
            return new AgentActionPreview(AgentActionKind.CreateTask, project, null, title, string.Empty, "DeepWork", "Todo", DateTime.Today, 60, null, null, string.Empty);
        }

        return null;
    }

    public static string BuildPreviewMessage(AgentActionPreview preview)
    {
        var intro = preview.Kind switch
        {
            AgentActionKind.CreateTask => "Sure, here's the task before I create it:",
            AgentActionKind.CreateAndScheduleTask => "Sure, here's the task and its calendar slot:",
            AgentActionKind.UpdateTask => "OK, here's the change to the task:",
            AgentActionKind.DeleteTask => "Here's the deletion of this task:",
            AgentActionKind.ScheduleTask => "OK, here's the calendar slot:",
            AgentActionKind.UpdateCalendar => "OK, here's how the calendar change would look:",
            AgentActionKind.DeleteCalendar => "Here's removing this task from the calendar:",
            AgentActionKind.AddTaskComment => "Sure, here's the comment before saving it:",
            _ => "Here's this change before applying it:"
        };

        return $"{intro}\n\n{preview.Summary}";
    }

    private static string BuildContext(IReadOnlyList<MobileProject> projects, IReadOnlyList<MobileTask> tasks)
    {
        var projectLines = projects.Take(30).Select(p => $"- {p.Id}: {p.Name}");
        var taskLines = tasks.Take(80).Select(t =>
            $"- {t.Id}: {t.Title} | Project: {t.ProjectName} | Status: {t.Status} | Type: {t.WorkType} | Date: {(t.ScheduledStart ?? t.DueDate)?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "no date"}");
        return "Available projects:\n" + string.Join("\n", projectLines) +
               "\n\nAvailable tasks:\n" + string.Join("\n", taskLines) +
               $"\n\nCurrent local date: {DateTime.Now:yyyy-MM-dd HH:mm}.";
    }

    private void Remember(string role, string content)
    {
        _history.Add(new AgentLlmMessage(role, content));
        if (_history.Count > 16)
            _history.RemoveRange(0, _history.Count - 16);
    }

    private static MobileProject? ResolveProject(string? id, string? hint, IReadOnlyList<MobileProject> projects)
    {
        if (Guid.TryParse(id, out var guid))
            return projects.FirstOrDefault(p => p.Id == guid);
        if (string.IsNullOrWhiteSpace(hint)) return null;
        return projects
            .OrderByDescending(p => Score(p.Name, hint))
            .FirstOrDefault(p => Score(p.Name, hint) > 0);
    }

    private static MobileTask? ResolveTask(string? id, string? hint, IReadOnlyList<MobileTask> tasks)
    {
        if (Guid.TryParse(id, out var guid))
            return tasks.FirstOrDefault(t => t.Id == guid);
        if (string.IsNullOrWhiteSpace(hint)) return null;
        return tasks
            .OrderByDescending(t => Score(t.Title + " " + t.ProjectName, hint))
            .FirstOrDefault(t => Score(t.Title + " " + t.ProjectName, hint) > 0);
    }

    private static int Score(string value, string hint)
    {
        var normalized = value.ToLowerInvariant();
        return hint.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(word => word.Length > 2 && normalized.Contains(word, StringComparison.Ordinal));
    }

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)) return date;
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out date)) return date;
        return InferDate(value);
    }

    private static DateTime? InferDate(string text)
    {
        var lower = text.ToLowerInvariant();
        var date = DateTime.Today;
        if (lower.Contains("tomorrow")) date = date.AddDays(1);
        if (lower.Contains("next week")) date = date.AddDays(7);

        var hour = 9;
        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var cleaned = words[i].Trim(':', '.', ',', ';');
            if (int.TryParse(cleaned, out var parsed) && parsed is >= 0 and <= 23)
            {
                hour = parsed;
                break;
            }
        }

        return date.AddHours(hour);
    }

    private static AgentActionKind NormalizeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return AgentActionKind.None;
        return action.Trim().ToLowerInvariant() switch
        {
            "createtask" or "create_task" or "crear tarea" => AgentActionKind.CreateTask,
            "createandscheduletask" or "create_and_schedule_task" or "crear y agendar tarea" => AgentActionKind.CreateAndScheduleTask,
            "updatetask" or "update_task" or "actualizar tarea" => AgentActionKind.UpdateTask,
            "deletetask" or "delete_task" or "eliminar tarea" => AgentActionKind.DeleteTask,
            "scheduletask" or "schedule_task" or "addcalendar" or "agendar tarea" => AgentActionKind.ScheduleTask,
            "updatecalendar" or "update_calendar" or "modificar calendario" => AgentActionKind.UpdateCalendar,
            "deletecalendar" or "delete_calendar" or "eliminar calendario" => AgentActionKind.DeleteCalendar,
            "addtaskcomment" or "add_comment" or "addcomment" or "anadir comentario" => AgentActionKind.AddTaskComment,
            _ => Enum.TryParse(action, true, out AgentActionKind parsed) ? parsed : AgentActionKind.None
        };
    }

    private static string ExtractJson(string response)
    {
        var trimmed = response.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private const string AgentPrompt = """
You are the conversational agent of a task-management app. You speak natural English and prepare previews before modifying data.
Your response must ALWAYS be a single JSON object, with no markdown.
If the user only asks for information, return {"action":"None","message":"natural answer"}.
If they want to change data, return an action in this format:
{
  "action":"CreateTask|CreateAndScheduleTask|UpdateTask|DeleteTask|ScheduleTask|UpdateCalendar|DeleteCalendar|AddTaskComment|None",
  "message":"short human sentence",
  "taskId":"guid if applicable",
  "taskTitle":"task title or hint if applicable",
  "projectId":"guid if applicable",
  "projectName":"project name or hint if applicable",
  "title":"task title",
  "description":"description",
  "status":"Todo|Assigned|Done|Cancelled",
  "workType":"DeepWork|Admin|Meeting|Learning|Communication|Planning|Other",
  "estimatedMinutes":60,
  "dueDate":"yyyy-MM-dd or null",
  "start":"yyyy-MM-dd HH:mm or null",
  "end":"yyyy-MM-dd HH:mm or null",
  "comment":"comment if applicable"
}
Don't invent IDs. Use the IDs from the context when you have a clear match. If something essential is missing, action None and ask for what's needed.
""";
}

public sealed record AgentTurnResult(string Message, AgentActionPreview? Preview);

public sealed record AgentLlmMessage(string Role, string Content);

public sealed record AgentActionPreview(
    AgentActionKind Kind,
    MobileProject? Project,
    MobileTask? Task,
    string Title,
    string Description,
    string WorkType,
    string? Status,
    DateTime? DueDate,
    int EstimatedMinutes,
    DateTime? Start,
    DateTime? End,
    string Comment)
{
    public string Summary
    {
        get
        {
            var lines = new List<string>();
            if (Kind is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask or AgentActionKind.UpdateTask)
            {
                lines.Add($"Task: {Title}");
                if (Project is not null) lines.Add($"Project: {Project.Name}");
                if (!string.IsNullOrWhiteSpace(Description)) lines.Add($"Description: {Description}");
                if (!string.IsNullOrWhiteSpace(WorkType)) lines.Add($"Type: {WorkType}");
                if (!string.IsNullOrWhiteSpace(Status)) lines.Add($"Status: {Status}");
                if (DueDate is DateTime due) lines.Add($"Due: {due:dd/MM/yyyy}");
                if (EstimatedMinutes > 0) lines.Add($"Estimate: {EstimatedMinutes} min");
            }
            else if (Task is not null)
            {
                lines.Add($"Task: {Task.Title}");
                lines.Add($"Project: {Task.ProjectName}");
            }

            if (Start is DateTime start) lines.Add($"Start: {start:dd/MM/yyyy HH:mm}");
            if (End is DateTime end) lines.Add($"End: {end:dd/MM/yyyy HH:mm}");
            if (!string.IsNullOrWhiteSpace(Comment)) lines.Add($"Comment: {Comment}");
            return string.Join("\n", lines);
        }
    }

    public AgentActionDto ToDto() => new()
    {
        Action = Kind.ToString(),
        TaskId = Task?.Id.ToString(),
        TaskTitle = Task?.Title,
        ProjectId = Project?.Id.ToString(),
        ProjectName = Project?.Name,
        Title = Title,
        Description = Description,
        Status = Status,
        WorkType = WorkType,
        EstimatedMinutes = EstimatedMinutes,
        DueDate = DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Start = Start?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        End = End?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        Comment = Comment
    };
}

public sealed class AgentActionDto
{
    public string? Action { get; set; }
    public string? Message { get; set; }
    public string? TaskId { get; set; }
    public string? TaskTitle { get; set; }
    public string? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public string? WorkType { get; set; }
    public int? EstimatedMinutes { get; set; }
    public string? DueDate { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? Comment { get; set; }
}

public enum AgentActionKind
{
    None,
    CreateTask,
    CreateAndScheduleTask,
    UpdateTask,
    DeleteTask,
    ScheduleTask,
    UpdateCalendar,
    DeleteCalendar,
    AddTaskComment
}
