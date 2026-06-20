using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JTS.Mobile.Services;

public sealed class DataverseMobileService
{
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, EntityMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lookupNavigationProperties = new(StringComparer.OrdinalIgnoreCase);
    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc;
    private List<MobileTask>? _cachedTasks;
    private List<MobileProject>? _cachedProjects;

    public MobileTask? SelectedTask { get; set; }
    public Guid? PendingFocusTaskId { get; set; }

    public async Task<bool> HasSettingsAsync()
    {
        var settings = await GetSettingsAsync();
        return settings.IsComplete;
    }

    public async Task<MobileSettings> GetSettingsAsync()
    {
        var secret = await SecureStorage.GetAsync("D365ClientSecret") ?? string.Empty;
        return new MobileSettings(
            Preferences.Get("D365TenantId", string.Empty),
            Preferences.Get("D365ClientId", string.Empty),
            secret,
            Preferences.Get("D365EnvironmentUrl", string.Empty),
            Preferences.Get("DeepSeekApiKey", string.Empty),
            Preferences.Get("DeepSeekModel", "deepseek-v4-flash"),
            Preferences.Get("DeepSeekThinking", false));
    }

    public async Task SaveSettingsAsync(MobileSettings settings)
    {
        Preferences.Set("D365TenantId", settings.TenantId.Trim());
        Preferences.Set("D365ClientId", settings.ClientId.Trim());
        Preferences.Set("D365EnvironmentUrl", settings.NormalizedEnvironmentUrl);
        Preferences.Set("DeepSeekApiKey", settings.DeepSeekApiKey.Trim());
        Preferences.Set("DeepSeekModel", string.IsNullOrWhiteSpace(settings.DeepSeekModel) ? "deepseek-v4-flash" : settings.DeepSeekModel.Trim());
        Preferences.Set("DeepSeekThinking", settings.DeepSeekThinking);
        await SecureStorage.SetAsync("D365ClientSecret", settings.ClientSecret.Trim());
        _accessToken = null;
        _metadata.Clear();
        _lookupNavigationProperties.Clear();
    }

    public async Task<IReadOnlyList<MobileProject>> GetProjectsAsync()
    {
        var metadata = await GetMetadataAsync("jts_proyecto");
        using var doc = await GetJsonAsync($"{metadata.EntitySetName}?$select={metadata.PrimaryIdAttribute},{metadata.PrimaryNameAttribute}&$orderby={metadata.PrimaryNameAttribute}");
        var result = doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(row => new MobileProject(
                row.GetGuid(metadata.PrimaryIdAttribute),
                row.GetStringOrDefault(metadata.PrimaryNameAttribute, "(no name)")))
            .Where(p => p.Id != Guid.Empty)
            .ToList();
        _cachedProjects = result;
        return result;
    }

    public async Task<IReadOnlyList<MobileProject>> GetCachedProjectsAsync() =>
        _cachedProjects is not null ? _cachedProjects : await GetProjectsAsync();

    public async Task<IReadOnlyList<MobileTask>> GetCachedTasksAsync() =>
        _cachedTasks is not null ? _cachedTasks : await GetTasksAsync();

    public void InvalidateCache()
    {
        _cachedTasks = null;
        _cachedProjects = null;
    }

    public async Task<IReadOnlyList<MobileTask>> GetTasksAsync()
    {
        var taskMetadata = await GetMetadataAsync("task");
        var projects = await GetProjectsAsync();
        var projectNames = projects.ToDictionary(p => p.Id, p => p.Name);
        var select = string.Join(",", "activityid", "subject", "description", "scheduledstart", "scheduledend",
            "jts_appstatus", "jts_worktype", "jts_estimatedminutes", "jts_duedate", "_jts_proyectoid_value", "prioritycode");
        var url = $"{taskMetadata.EntitySetName}?$select={Uri.EscapeDataString(select)}&$filter=jts_mobilevisible eq true or jts_appstatus ne null or _jts_proyectoid_value ne null&$orderby=scheduledstart asc,jts_duedate asc";
        using var doc = await GetJsonAsync(url);
        var result = doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(row =>
            {
                var projectId = row.GetGuid("_jts_proyectoid_value");
                return new MobileTask(
                    row.GetGuid("activityid"),
                    row.GetStringOrDefault("subject", "(no title)"),
                    row.GetStringOrDefault("description", string.Empty),
                    projectId,
                    projectNames.GetValueOrDefault(projectId, "No project"),
                    row.GetStringOrDefault("jts_appstatus", "Todo"),
                    row.GetStringOrDefault("jts_worktype", "DeepWork"),
                    row.GetDate("jts_duedate"),
                    row.GetDate("scheduledstart"),
                    row.GetDate("scheduledend"),
                    row.GetInt("jts_estimatedminutes"),
                    row.GetInt("prioritycode"));
            })
            .Where(t => t.Id != Guid.Empty)
            .ToList();
        _cachedTasks = result;
        return result;
    }

    public async Task<Guid> CreateTaskAsync(MobileTaskDraft draft)
    {
        var taskMetadata = await GetMetadataAsync("task");
        var projectMetadata = await GetMetadataAsync("jts_proyecto");
        var projectLookup = await GetLookupNavigationPropertyAsync("task", "jts_proyectoid");
        var body = new Dictionary<string, object?>
        {
            ["subject"] = draft.Title.Trim(),
            ["description"] = draft.Description.Trim(),
            ["jts_worktype"] = draft.WorkType,
            ["jts_appstatus"] = "Todo",
            ["jts_estimatedminutes"] = Math.Max(0, draft.EstimatedMinutes),
            ["jts_mobilevisible"] = true,
            ["prioritycode"] = 1,
            [$"{projectLookup}@odata.bind"] = $"/{projectMetadata.EntitySetName}({draft.ProjectId})"
        };
        if (draft.DueDate is DateTime due) body["jts_duedate"] = due.Date;
        using var response = await SendJsonAsync(HttpMethod.Post, taskMetadata.EntitySetName, body);
        var entityId = response.Headers.TryGetValues("OData-EntityId", out var values)
            ? values.FirstOrDefault()
            : null;
        return TryParseIdFromEntityUrl(entityId) ?? Guid.Empty;
    }

    public async Task UpdateTaskStatusAsync(Guid taskId, string status)
    {
        var taskMetadata = await GetMetadataAsync("task");
        await SendJsonAsync(HttpMethod.Patch, $"{taskMetadata.EntitySetName}({taskId})", new Dictionary<string, object?>
        {
            ["jts_appstatus"] = status,
            ["jts_mobilevisible"] = true
        });
    }

    public async Task AddCommentAsync(Guid taskId, string taskTitle, string content)
    {
        var metadata = await GetMetadataAsync("jts_comentariotarea");
        var taskLookup = await GetLookupNavigationPropertyAsync("jts_comentariotarea", "jts_taskid");
        await SendJsonAsync(HttpMethod.Post, metadata.EntitySetName, new Dictionary<string, object?>
        {
            ["jts_name"] = $"{taskTitle} {DateTime.Now:yyyy-MM-dd HH:mm}",
            ["jts_content"] = content.Trim(),
            ["jts_source"] = "Mobile",
            ["jts_aireviewed"] = false,
            [$"{taskLookup}@odata.bind"] = $"/tasks({taskId})"
        });
    }

    public async Task UpdateTaskAsync(MobileTaskChange change)
    {
        var taskMetadata = await GetMetadataAsync("task");
        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(change.Title)) body["subject"] = change.Title.Trim();
        if (change.Description is not null) body["description"] = change.Description.Trim();
        if (!string.IsNullOrWhiteSpace(change.Status)) body["jts_appstatus"] = change.Status.Trim();
        if (!string.IsNullOrWhiteSpace(change.WorkType)) body["jts_worktype"] = change.WorkType.Trim();
        if (change.DueDate is DateTime due) body["jts_duedate"] = due.Date;
        if (change.EstimatedMinutes is int minutes) body["jts_estimatedminutes"] = Math.Max(0, minutes);
        body["jts_mobilevisible"] = true;
        if (body.Count == 1) return;
        await SendJsonAsync(HttpMethod.Patch, $"{taskMetadata.EntitySetName}({change.TaskId})", body);
    }

    public async Task DeleteTaskAsync(Guid taskId)
    {
        var taskMetadata = await GetMetadataAsync("task");
        using var request = await CreateRequestAsync(HttpMethod.Delete, $"{taskMetadata.EntitySetName}({taskId})");
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(TrimError(error));
        }
    }

    public async Task SetTaskScheduleAsync(Guid taskId, DateTime start, DateTime end)
    {
        var taskMetadata = await GetMetadataAsync("task");
        await SendJsonAsync(HttpMethod.Patch, $"{taskMetadata.EntitySetName}({taskId})", new Dictionary<string, object?>
        {
            ["scheduledstart"] = start,
            ["scheduledend"] = end,
            ["jts_mobilevisible"] = true
        });
    }

    public async Task ClearTaskScheduleAsync(Guid taskId)
    {
        var taskMetadata = await GetMetadataAsync("task");
        await SendJsonAsync(HttpMethod.Patch, $"{taskMetadata.EntitySetName}({taskId})", new Dictionary<string, object?>
        {
            ["scheduledstart"] = null,
            ["scheduledend"] = null,
            ["jts_mobilevisible"] = true
        });
    }

    public async Task<IReadOnlyList<MobileComment>> GetCommentsAsync(Guid taskId)
    {
        var metadata = await GetMetadataAsync("jts_comentariotarea");
        var url = $"{metadata.EntitySetName}?$select=jts_comentariotareaid,jts_content,createdon,_jts_timesheetlineid_value&$filter=_jts_taskid_value eq {taskId}&$orderby=createdon asc";
        using var doc = await GetJsonAsync(url);
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(row => new MobileComment(
                row.GetGuid("jts_comentariotareaid"),
                row.GetDate("createdon") ?? DateTime.UtcNow,
                row.GetStringOrDefault("jts_content", ""),
                row.TryGetProperty("_jts_timesheetlineid_value", out var ts) && ts.ValueKind != JsonValueKind.Null))
            .ToList();
    }

    public async Task<IReadOnlyList<MobileTimeEntry>> GetTimeEntriesAsync(Guid taskId)
    {
        var metadata = await GetMetadataAsync("jts_tiempotarea");
        var url = $"{metadata.EntitySetName}?$select=jts_tiempotareaid,jts_startedat,jts_endedat,jts_actualseconds,_jts_timesheetlineid_value&$filter=_jts_taskid_value eq {taskId}&$orderby=jts_startedat desc";
        using var doc = await GetJsonAsync(url);
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(row =>
            {
                var seconds = row.GetInt("jts_actualseconds");
                var started = row.GetDate("jts_startedat") ?? DateTime.UtcNow;
                var ended = row.GetDate("jts_endedat") ?? started.AddSeconds(seconds);
                return new MobileTimeEntry(
                    row.GetGuid("jts_tiempotareaid"),
                    started,
                    ended,
                    Math.Max(0, seconds / 60),
                    row.TryGetProperty("_jts_timesheetlineid_value", out var ts) && ts.ValueKind != JsonValueKind.Null);
            })
            .ToList();
    }

    public async Task AddTimeEntryAsync(Guid taskId, string taskTitle, DateTime startedAt, DateTime endedAt)
    {
        var metadata = await GetMetadataAsync("jts_tiempotarea");
        var taskLookup = await GetLookupNavigationPropertyAsync("jts_tiempotarea", "jts_taskid");
        var seconds = Math.Max(0, (int)Math.Round((endedAt - startedAt).TotalSeconds));
        await SendJsonAsync(HttpMethod.Post, metadata.EntitySetName, new Dictionary<string, object?>
        {
            ["jts_name"] = $"{taskTitle} {startedAt:yyyy-MM-dd HH:mm}",
            ["jts_startedat"] = startedAt,
            ["jts_endedat"] = endedAt,
            ["jts_actualseconds"] = seconds,
            ["jts_workdate"] = startedAt.Date,
            ["jts_note"] = "Logged from mobile",
            [$"{taskLookup}@odata.bind"] = $"/tasks({taskId})"
        });
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, relativeUrl);
        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(TrimError(json));
        return JsonDocument.Parse(json);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string relativeUrl, object body)
    {
        using var request = await CreateRequestAsync(method, relativeUrl);
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new InvalidOperationException(TrimError(error));
        }

        return response;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string relativeUrl)
    {
        var settings = await GetSettingsAsync();
        if (!settings.IsComplete) throw new InvalidOperationException("Set up the Dataverse connection first.");
        var token = await GetAccessTokenAsync(settings);
        var request = new HttpRequestMessage(method, $"{settings.NormalizedEnvironmentUrl}/api/data/v9.2/{relativeUrl}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        return request;
    }

    private async Task<string> GetAccessTokenAsync(MobileSettings settings)
    {
        if (_accessToken is not null && _accessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(2))
            return _accessToken;

        var tokenUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(settings.TenantId)}/oauth2/v2.0/token";
        using var response = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["grant_type"] = "client_credentials",
            ["scope"] = $"{settings.NormalizedEnvironmentUrl}/.default"
        }));
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(TrimError(json));
        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expires = doc.RootElement.TryGetProperty("expires_in", out var expiresElement) ? expiresElement.GetInt32() : 3600;
        _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expires);
        return _accessToken ?? throw new InvalidOperationException("Dataverse did not return an access_token.");
    }

    private async Task<EntityMetadata> GetMetadataAsync(string logicalName)
    {
        if (_metadata.TryGetValue(logicalName, out var cached)) return cached;
        using var doc = await GetJsonAsync($"EntityDefinitions(LogicalName='{logicalName}')?$select=EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute");
        var root = doc.RootElement;
        var metadata = new EntityMetadata(
            root.GetStringOrDefault("EntitySetName", logicalName),
            root.GetStringOrDefault("PrimaryIdAttribute", $"{logicalName}id"),
            root.GetStringOrDefault("PrimaryNameAttribute", "jts_name"));
        _metadata[logicalName] = metadata;
        return metadata;
    }

    private async Task<string> GetLookupNavigationPropertyAsync(string entityLogicalName, string lookupAttributeLogicalName)
    {
        var cacheKey = $"{entityLogicalName}:{lookupAttributeLogicalName}";
        if (_lookupNavigationProperties.TryGetValue(cacheKey, out var cached)) return cached;

        var url =
            $"EntityDefinitions(LogicalName='{entityLogicalName}')/ManyToOneRelationships?" +
            "$select=ReferencingAttribute,ReferencingEntityNavigationPropertyName&" +
            $"$filter=ReferencingAttribute eq '{lookupAttributeLogicalName}'";
        using var doc = await GetJsonAsync(url);
        var row = doc.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        var navigationProperty = row.ValueKind == JsonValueKind.Object
            ? row.GetStringOrDefault("ReferencingEntityNavigationPropertyName", lookupAttributeLogicalName)
            : lookupAttributeLogicalName;
        _lookupNavigationProperties[cacheKey] = navigationProperty;
        return navigationProperty;
    }

    private static Guid? TryParseIdFromEntityUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var start = url.LastIndexOf('(');
        var end = url.LastIndexOf(')');
        return start >= 0 && end > start && Guid.TryParse(url[(start + 1)..end], out var id) ? id : null;
    }

    private static string TrimError(string error)
    {
        var text = error;
        try
        {
            using var doc = JsonDocument.Parse(error);
            var root = doc.RootElement;
            text = root.GetStringOrDefault("error_description",
                root.GetStringOrDefault("message", error));
        }
        catch
        {
            // Keep the raw response when it is not JSON.
        }

        if (text.Contains("AADSTS7000215", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Invalid client secret", StringComparison.OrdinalIgnoreCase))
        {
            return "Azure AD rejected the client secret. Check that the Tenant ID and Client ID belong to the same app registration, and paste the client secret VALUE, not the Secret ID. If the secret was created a while ago, it may have expired.";
        }

        return text.Length <= 500 ? text : text[..500] + "...";
    }
}

public sealed record MobileSettings(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string EnvironmentUrl,
    string DeepSeekApiKey = "",
    string DeepSeekModel = "deepseek-v4-flash",
    bool DeepSeekThinking = false)
{
    public string NormalizedEnvironmentUrl
    {
        get
        {
            var value = EnvironmentUrl.Trim().TrimEnd('/');
            return value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value;
        }
    }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(EnvironmentUrl);
}

public sealed record MobileProject(Guid Id, string Name);

public sealed record MobileTask(
    Guid Id,
    string Title,
    string Description,
    Guid ProjectId,
    string ProjectName,
    string Status,
    string WorkType,
    DateTime? DueDate,
    DateTime? ScheduledStart,
    DateTime? ScheduledEnd,
    int EstimatedMinutes,
    int PriorityCode = 1)
{
    public string Meta => string.Join(" · ", new[]
    {
        ProjectName,
        Status,
        WorkType,
        ScheduledStart is DateTime start ? $"{start:dd/MM HH:mm}" : DueDate is DateTime due ? $"Due {due:dd/MM}" : "No date"
    });
    public string PriorityIcon => PriorityCode switch { 2 => "↑", 0 => "↓", _ => "—" };
    public Color PriorityColor => PriorityCode switch
    {
        2 => Color.FromArgb("#F87171"),
        0 => Color.FromArgb("#60A5FA"),
        _ => Color.FromArgb("#9CA3AF")
    };
    public string DueDateShort => DueDate?.ToString("dd/MM") ?? "";
    public bool HasDueDate => DueDate is not null;
}

public sealed record MobileTaskDraft(Guid ProjectId, string Title, string Description, string WorkType, int EstimatedMinutes, DateTime? DueDate);

public sealed record MobileComment(Guid Id, DateTime CreatedAt, string Content, bool IsTransferred);

public sealed record MobileTimeEntry(Guid Id, DateTime StartedAt, DateTime EndedAt, int ActualMinutes, bool IsTransferred);

public sealed record MobileTaskChange(
    Guid TaskId,
    string? Title,
    string? Description,
    string? Status,
    string? WorkType,
    DateTime? DueDate,
    int? EstimatedMinutes);

internal sealed record EntityMetadata(string EntitySetName, string PrimaryIdAttribute, string PrimaryNameAttribute);

internal static class JsonElementExtensions
{
    public static string GetStringOrDefault(this JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() ?? fallback
            : fallback;

    public static Guid GetGuid(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)
            ? id
            : Guid.Empty;

    public static DateTime? GetDate(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var date)
            ? date
            : null;

    public static int GetInt(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : 0;
}
