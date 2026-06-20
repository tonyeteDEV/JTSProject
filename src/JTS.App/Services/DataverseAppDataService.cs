using JTS.Core;
using JTS.Data.Entities;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace JTS_App.Services;

public sealed class DataverseAppDataService
{
    private const string ProjectTable = "jts_proyecto";
    private const string ProjectId = "jts_proyectoid";
    private const string ProjectName = "jts_proyecto";
    private const string ProjectDescription = "jts_descripcion";
    private const string ProjectCustomer = "jts_cliente";
    private const string ProjectColorHex = "jts_colorhex";

    private const string TaskTable = "task";
    private const string TaskId = "activityid";
    private const string TaskTitle = "subject";
    private const string TaskDescription = "description";
    private const string TaskPriorityCode = "prioritycode";
    private const string TaskScheduledStart = "scheduledstart";
    private const string TaskScheduledEnd = "scheduledend";
    private const string TaskRegarding = "regardingobjectid";
    private const string TaskProject = "jts_proyectoid";
    private const string TaskWorkType = "jts_worktype";
    private const string TaskAppStatus = "jts_appstatus";
    private const string TaskEstimatedMinutes = "jts_estimatedminutes";
    private const string TaskDueDate = "jts_duedate";
    private const string TaskMobileVisible = "jts_mobilevisible";

    private const string CalendarTable = "jts_bloquecalendario";
    private const string CalendarTask = "jts_taskid";
    private const string CalendarName = "jts_name";
    private const string CalendarStart = "jts_start";
    private const string CalendarEnd = "jts_end";
    private const string CalendarSource = "jts_source";

    private const string CommentTable = "jts_comentariotarea";
    private const string CommentTask = "jts_taskid";
    private const string CommentName = "jts_name";
    private const string CommentContent = "jts_content";
    private const string CommentSource = "jts_source";
    private const string CommentAiReviewed = "jts_aireviewed";

    private const string TimeTable = "jts_tiempotarea";
    private const string TimeTask = "jts_taskid";
    private const string TimeName = "jts_name";
    private const string TimeStartedAt = "jts_startedat";
    private const string TimeEndedAt = "jts_endedat";
    private const string TimeActualSeconds = "jts_actualseconds";
    private const string TimeWorkDate = "jts_workdate";
    private const string TimeNote = "jts_note";
    private const string TimesheetLineTable = "jts_lineadehoras";

    private static readonly string[] ProjectPalette =
    [
        "#254D8F", "#2F6B56", "#7A4A1E", "#7D3446", "#5A438B", "#286A79",
        "#6B5A24", "#75364F", "#464E8A", "#3F6A38", "#70402C", "#2D6861"
    ];

    private readonly AppSettingsService _settings;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly object _detailCacheGate = new();
    private readonly Dictionary<Guid, IReadOnlyList<TaskJournalEntry>> _commentsByTask = new();
    private readonly Dictionary<Guid, IReadOnlyList<PomodoroSession>> _timeEntriesByTask = new();
    private DataverseTaskSnapshot? _cachedSnapshot;

    public DataverseAppDataService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public async Task<DataverseTaskSnapshot> LoadTaskSnapshotAsync(bool forceSync = false)
    {
        if (!forceSync && _cachedSnapshot is not null) return _cachedSnapshot;

        await _snapshotLock.WaitAsync();
        try
        {
            if (!forceSync && _cachedSnapshot is not null) return _cachedSnapshot;

        using var service = await CreateServiceClientAsync();
        var projects = await LoadProjectsAsync(service);
        var projectIdByDataverseId = projects
            .Where(p => p.DataverseId is not null)
            .ToDictionary(p => p.DataverseId!.Value, p => p.Id);
        var tasks = await LoadTasksAsync(service, projects, projectIdByDataverseId);
        await LoadCalendarBlocksAsync(service, tasks);
            _cachedSnapshot = new DataverseTaskSnapshot(projects, tasks);
            return _cachedSnapshot;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    public async Task<IReadOnlyList<TaskJournalEntry>> LoadCommentsAsync(Guid taskId, bool forceSync = false)
    {
        lock (_detailCacheGate)
        {
            if (!forceSync && _commentsByTask.TryGetValue(taskId, out var cached)) return cached;
        }

        using var service = await CreateServiceClientAsync();
        var commentLineLookup = FindLookupAttribute(RetrieveMetadata(service, CommentTable), TimesheetLineTable)?.LogicalName;
        var columns = new ColumnSet("jts_comentariotareaid", CommentContent, "createdon");
        if (!string.IsNullOrWhiteSpace(commentLineLookup)) columns.AddColumn(commentLineLookup);

        var rows = await RetrieveAllAsync(service, new QueryExpression(CommentTable)
        {
            ColumnSet = columns,
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(CommentTask, ConditionOperator.Equal, taskId) }
            },
            Orders = { new OrderExpression("createdon", OrderType.Descending) }
        });

        var result = rows.Select((row, index) => new TaskJournalEntry
        {
            Id = index + 1,
            DataverseId = row.Id,
            Content = row.GetAttributeValue<string>(CommentContent) ?? string.Empty,
            CreatedAt = row.GetAttributeValue<DateTime?>("createdon") ?? DateTime.UtcNow,
            TimesheetLineDataverseId = string.IsNullOrWhiteSpace(commentLineLookup)
                ? null
                : row.GetAttributeValue<EntityReference>(commentLineLookup)?.Id
        }).ToList();
        lock (_detailCacheGate)
        {
            _commentsByTask[taskId] = result;
        }

        return result;
    }

    public async Task<IReadOnlyList<PomodoroSession>> LoadTimeEntriesAsync(Guid taskId, bool forceSync = false)
    {
        lock (_detailCacheGate)
        {
            if (!forceSync && _timeEntriesByTask.TryGetValue(taskId, out var cached)) return cached;
        }

        using var service = await CreateServiceClientAsync();
        var timeLineLookup = FindLookupAttribute(RetrieveMetadata(service, TimeTable), TimesheetLineTable)?.LogicalName;
        var columns = new ColumnSet("jts_tiempotareaid", TimeStartedAt, TimeEndedAt, TimeActualSeconds, TimeWorkDate);
        if (!string.IsNullOrWhiteSpace(timeLineLookup)) columns.AddColumn(timeLineLookup);

        var rows = await RetrieveAllAsync(service, new QueryExpression(TimeTable)
        {
            ColumnSet = columns,
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(TimeTask, ConditionOperator.Equal, taskId) }
            }
        });

        var result = rows.Select((row, index) =>
        {
            var startedAt = row.GetAttributeValue<DateTime?>(TimeStartedAt)
                ?? row.GetAttributeValue<DateTime?>(TimeWorkDate)
                ?? DateTime.UtcNow;
            var actualSeconds = row.GetAttributeValue<int?>(TimeActualSeconds) ?? 0;
            return new PomodoroSession
            {
                Id = index + 1,
                DataverseId = row.Id,
                StartedAt = startedAt,
                EndedAt = row.GetAttributeValue<DateTime?>(TimeEndedAt),
                ActualMinutes = Math.Max(1, (int)Math.Ceiling(actualSeconds / 60d)),
                PlannedMinutes = Math.Max(1, (int)Math.Ceiling(actualSeconds / 60d)),
                SessionType = PomodoroSessionType.Work,
                Completed = true,
                TimesheetLineDataverseId = string.IsNullOrWhiteSpace(timeLineLookup)
                    ? null
                : row.GetAttributeValue<EntityReference>(timeLineLookup)?.Id
            };
        }).ToList();
        lock (_detailCacheGate)
        {
            _timeEntriesByTask[taskId] = result;
        }

        return result;
    }

    public async Task PreloadCommentsAsync(IEnumerable<Guid> taskIds, bool forceSync = false)
    {
        var ids = FilterIdsToLoad(taskIds, _commentsByTask, forceSync);
        if (ids.Count == 0) return;

        using var service = await CreateServiceClientAsync();
        var commentLineLookup = FindLookupAttribute(RetrieveMetadata(service, CommentTable), TimesheetLineTable)?.LogicalName;
        var columns = new ColumnSet("jts_comentariotareaid", CommentTask, CommentContent, "createdon");
        if (!string.IsNullOrWhiteSpace(commentLineLookup)) columns.AddColumn(commentLineLookup);

        var rows = new List<Entity>();
        foreach (var chunk in ids.Chunk(200))
        {
            rows.AddRange(await RetrieveAllAsync(service, new QueryExpression(CommentTable)
            {
                ColumnSet = columns,
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(CommentTask, ConditionOperator.In, chunk.Cast<object>().ToArray())
                    }
                },
                Orders = { new OrderExpression("createdon", OrderType.Descending) }
            }));
        }

        var grouped = rows
            .Where(row => row.GetAttributeValue<EntityReference>(CommentTask)?.Id is not null)
            .GroupBy(row => row.GetAttributeValue<EntityReference>(CommentTask)!.Id)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TaskJournalEntry>)group.Select((row, index) => new TaskJournalEntry
                {
                    Id = index + 1,
                    DataverseId = row.Id,
                    Content = row.GetAttributeValue<string>(CommentContent) ?? string.Empty,
                    CreatedAt = row.GetAttributeValue<DateTime?>("createdon") ?? DateTime.UtcNow,
                    TimesheetLineDataverseId = string.IsNullOrWhiteSpace(commentLineLookup)
                        ? null
                        : row.GetAttributeValue<EntityReference>(commentLineLookup)?.Id
                }).ToList());

        lock (_detailCacheGate)
        {
            foreach (var id in ids)
                _commentsByTask[id] = grouped.TryGetValue(id, out var comments) ? comments : [];
        }
    }

    public async Task PreloadTimeEntriesAsync(IEnumerable<Guid> taskIds, bool forceSync = false)
    {
        var ids = FilterIdsToLoad(taskIds, _timeEntriesByTask, forceSync);
        if (ids.Count == 0) return;

        using var service = await CreateServiceClientAsync();
        var timeLineLookup = FindLookupAttribute(RetrieveMetadata(service, TimeTable), TimesheetLineTable)?.LogicalName;
        var columns = new ColumnSet("jts_tiempotareaid", TimeTask, TimeStartedAt, TimeEndedAt, TimeActualSeconds, TimeWorkDate);
        if (!string.IsNullOrWhiteSpace(timeLineLookup)) columns.AddColumn(timeLineLookup);

        var rows = new List<Entity>();
        foreach (var chunk in ids.Chunk(200))
        {
            rows.AddRange(await RetrieveAllAsync(service, new QueryExpression(TimeTable)
            {
                ColumnSet = columns,
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(TimeTask, ConditionOperator.In, chunk.Cast<object>().ToArray())
                    }
                }
            }));
        }

        var grouped = rows
            .Where(row => row.GetAttributeValue<EntityReference>(TimeTask)?.Id is not null)
            .GroupBy(row => row.GetAttributeValue<EntityReference>(TimeTask)!.Id)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PomodoroSession>)group.Select((row, index) =>
                {
                    var startedAt = row.GetAttributeValue<DateTime?>(TimeStartedAt)
                        ?? row.GetAttributeValue<DateTime?>(TimeWorkDate)
                        ?? DateTime.UtcNow;
                    var actualSeconds = row.GetAttributeValue<int?>(TimeActualSeconds) ?? 0;
                    var actualMinutes = Math.Max(1, (int)Math.Ceiling(actualSeconds / 60d));
                    return new PomodoroSession
                    {
                        Id = index + 1,
                        DataverseId = row.Id,
                        StartedAt = startedAt,
                        EndedAt = row.GetAttributeValue<DateTime?>(TimeEndedAt),
                        ActualMinutes = actualMinutes,
                        PlannedMinutes = actualMinutes,
                        SessionType = PomodoroSessionType.Work,
                        Completed = true,
                        TimesheetLineDataverseId = string.IsNullOrWhiteSpace(timeLineLookup)
                            ? null
                            : row.GetAttributeValue<EntityReference>(timeLineLookup)?.Id
                    };
                }).ToList());

        lock (_detailCacheGate)
        {
            foreach (var id in ids)
                _timeEntriesByTask[id] = grouped.TryGetValue(id, out var entries) ? entries : [];
        }
    }

    public async Task<int> LoadTimeEntrySecondsTotalAsync(Guid taskId, bool forceSync = false)
    {
        var entries = await LoadTimeEntriesAsync(taskId, forceSync);
        return entries.Sum(entry => Math.Max(0, entry.ActualMinutes * 60));
    }

    public async Task<bool> HasTimesheetLockedTaskDataAsync(Guid taskId, bool forceSync = false)
    {
        var comments = await LoadCommentsAsync(taskId, forceSync);
        if (comments.Any(comment => comment.TimesheetLineDataverseId is not null)) return true;

        var entries = await LoadTimeEntriesAsync(taskId, forceSync);
        return entries.Any(entry => entry.TimesheetLineDataverseId is not null);
    }

    public async Task<IReadOnlyList<DataverseTimeEntryContext>> LoadRecentTimeEntryContextAsync(DateTime sinceUtc, int take)
    {
        using var service = await CreateServiceClientAsync();
        var rows = await RetrieveAllAsync(service, new QueryExpression(TimeTable)
        {
            ColumnSet = new ColumnSet("jts_tiempotareaid", TimeTask, TimeStartedAt, TimeEndedAt, TimeActualSeconds, TimeWorkDate),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(TimeStartedAt, ConditionOperator.OnOrAfter, sinceUtc) }
            },
            Orders = { new OrderExpression(TimeStartedAt, OrderType.Descending) }
        });

        return rows
            .Select(row =>
            {
                var startedAt = row.GetAttributeValue<DateTime?>(TimeStartedAt)
                    ?? row.GetAttributeValue<DateTime?>(TimeWorkDate)
                    ?? DateTime.UtcNow;
                var actualSeconds = row.GetAttributeValue<int?>(TimeActualSeconds) ?? 0;
                return new DataverseTimeEntryContext(
                    row.Id,
                    row.GetAttributeValue<EntityReference>(TimeTask)?.Id,
                    startedAt,
                    row.GetAttributeValue<DateTime?>(TimeEndedAt),
                    Math.Max(1, (int)Math.Ceiling(actualSeconds / 60d)));
            })
            .Where(entry => entry.StartedAt >= sinceUtc)
            .OrderByDescending(entry => entry.StartedAt)
            .Take(Math.Max(1, take))
            .ToList();
    }

    public async Task<IReadOnlyList<DataverseCommentContext>> LoadRecentCommentContextAsync(DateTime sinceUtc, int take)
    {
        using var service = await CreateServiceClientAsync();
        var rows = await RetrieveAllAsync(service, new QueryExpression(CommentTable)
        {
            ColumnSet = new ColumnSet("jts_comentariotareaid", CommentTask, CommentContent, "createdon"),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression("createdon", ConditionOperator.OnOrAfter, sinceUtc) }
            },
            Orders = { new OrderExpression("createdon", OrderType.Descending) }
        });

        return rows
            .Select(row => new DataverseCommentContext(
                row.Id,
                row.GetAttributeValue<EntityReference>(CommentTask)?.Id,
                row.GetAttributeValue<DateTime?>("createdon") ?? DateTime.UtcNow,
                row.GetAttributeValue<string>(CommentContent) ?? string.Empty))
            .Where(comment => comment.CreatedAt >= sinceUtc)
            .OrderByDescending(comment => comment.CreatedAt)
            .Take(Math.Max(1, take))
            .ToList();
    }

    public async Task<Guid> CreateTaskAsync(TaskItem task, Guid projectDataverseId)
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(TaskTable);
        WriteTaskFields(entity, task, projectDataverseId);
        var id = await Task.Run(() => service.Create(entity));
        InvalidateSnapshot();
        return id;
    }

    public async Task UpdateTaskAsync(TaskItem task, Guid projectDataverseId)
    {
        if (task.DataverseId is not Guid taskId) return;
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(TaskTable, taskId);
        WriteTaskFields(entity, task, projectDataverseId);
        await Task.Run(() => service.Update(entity));
        InvalidateSnapshot();
    }

    public async Task UpdateTaskPlanningFieldsAsync(TaskItem task)
    {
        if (task.DataverseId is not Guid taskId) return;

        using var service = await CreateServiceClientAsync();
        var entity = new Entity(TaskTable, taskId)
        {
            [TaskEstimatedMinutes] = Math.Max(0, task.EstimatedPomodoros) * 30,
            [TaskMobileVisible] = true,
            [TaskScheduledStart] = task.ScheduledStart is DateTime scheduledStart ? DisplayFormat.SpainTimeToUtc(scheduledStart) : null,
            [TaskScheduledEnd] = task.ScheduledEnd is DateTime scheduledEnd ? DisplayFormat.SpainTimeToUtc(scheduledEnd) : null
        };

        await Task.Run(() => service.Update(entity));
        InvalidateSnapshot();
    }

    public async Task SetStatusAsync(Guid taskId, TaskItemStatus status)
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(TaskTable, taskId)
        {
            [TaskAppStatus] = status.ToString(),
            [TaskMobileVisible] = true
        };
        await Task.Run(() => service.Update(entity));
        InvalidateSnapshot();
    }

    public async Task DeleteTaskAsync(Guid taskId)
    {
        await EnsureTaskHasNoTimesheetLinksAsync(taskId);
        using var service = await CreateServiceClientAsync();
        await DeleteRelatedRecordsAsync(service, CalendarTable, CalendarTask, taskId);
        await DeleteRelatedRecordsAsync(service, CommentTable, CommentTask, taskId);
        await DeleteRelatedRecordsAsync(service, TimeTable, TimeTask, taskId);
        await Task.Run(() => service.Delete(TaskTable, taskId));
        InvalidateSnapshot();
        lock (_detailCacheGate)
        {
            _commentsByTask.Remove(taskId);
            _timeEntriesByTask.Remove(taskId);
        }
    }

    public async Task UpdateProjectColorAsync(Guid projectId, string colorHex)
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(ProjectTable, projectId)
        {
            [ProjectColorHex] = NormalizeColorHex(colorHex)
        };
        await Task.Run(() => service.Update(entity));
        InvalidateSnapshot();
    }

    public async Task<Guid> AddCalendarBlockAsync(Guid taskId, string taskTitle, DateTime startedAt, DateTime endedAt)
    {
        using var service = await CreateServiceClientAsync();
        var startUtc = DisplayFormat.SpainTimeToUtc(startedAt);
        var endUtc = DisplayFormat.SpainTimeToUtc(endedAt);
        var entity = new Entity(CalendarTable)
        {
            [CalendarName] = $"{taskTitle} {startedAt:yyyy-MM-dd HH:mm}",
            [CalendarTask] = new EntityReference(TaskTable, taskId),
            [CalendarStart] = startUtc,
            [CalendarEnd] = endUtc,
            [CalendarSource] = "Desktop"
        };
        var id = await Task.Run(() => service.Create(entity));
        InvalidateSnapshot();
        return id;
    }

    public async Task UpdateCalendarBlockAsync(Guid blockId, DateTime startedAt, DateTime endedAt)
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(CalendarTable, blockId)
        {
            [CalendarStart] = DisplayFormat.SpainTimeToUtc(startedAt),
            [CalendarEnd] = DisplayFormat.SpainTimeToUtc(endedAt),
            [CalendarSource] = "Desktop"
        };
        await Task.Run(() => service.Update(entity));
        InvalidateSnapshot();
    }

    public async Task DeleteCalendarBlockAsync(Guid blockId)
    {
        using var service = await CreateServiceClientAsync();
        await Task.Run(() => service.Delete(CalendarTable, blockId));
        InvalidateSnapshot();
    }

    public async Task AddCommentAsync(Guid taskId, string taskTitle, string content, string source = "Desktop")
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(CommentTable)
        {
            [CommentName] = $"{taskTitle} {DateTime.Now:yyyy-MM-dd HH:mm}",
            [CommentTask] = new EntityReference(TaskTable, taskId),
            [CommentContent] = content.Trim(),
            [CommentSource] = source,
            [CommentAiReviewed] = true
        };
        await Task.Run(() => service.Create(entity));
        lock (_detailCacheGate)
        {
            _commentsByTask.Remove(taskId);
        }
    }

    public async Task UpdateCommentAsync(Guid commentId, string content)
    {
        using var service = await CreateServiceClientAsync();
        await EnsureRecordHasNoTimesheetLineAsync(service, CommentTable, commentId, "This comment is already included in a timesheet and cannot be edited.");
        var entity = new Entity(CommentTable, commentId)
        {
            [CommentContent] = content.Trim(),
            [CommentSource] = "Desktop"
        };
        await Task.Run(() => service.Update(entity));
        var affectedTaskIds = FindCommentCacheKeys(commentId);
        lock (_detailCacheGate)
        {
            foreach (var taskId in affectedTaskIds)
                _commentsByTask.Remove(taskId);
        }
    }

    public async Task DeleteCommentAsync(Guid commentId)
    {
        using var service = await CreateServiceClientAsync();
        await EnsureRecordHasNoTimesheetLineAsync(service, CommentTable, commentId, "This comment is already included in a timesheet and cannot be deleted.");
        await Task.Run(() => service.Delete(CommentTable, commentId));
        var affectedTaskIds = FindCommentCacheKeys(commentId);
        lock (_detailCacheGate)
        {
            foreach (var taskId in affectedTaskIds)
                _commentsByTask.Remove(taskId);
        }
    }

    public async Task<Guid> AddTimeEntryAsync(Guid taskId, string taskTitle, DateTime startedAt, DateTime endedAt, string note)
    {
        using var service = await CreateServiceClientAsync();
        var seconds = Math.Max(1, (int)Math.Round((endedAt - startedAt).TotalSeconds));
        var entity = new Entity(TimeTable)
        {
            [TimeName] = $"{taskTitle} {startedAt:yyyy-MM-dd HH:mm}",
            [TimeTask] = new EntityReference(TaskTable, taskId),
            [TimeStartedAt] = startedAt,
            [TimeEndedAt] = endedAt,
            [TimeActualSeconds] = seconds,
            [TimeWorkDate] = startedAt.Date,
            [TimeNote] = note
        };
        var id = await Task.Run(() => service.Create(entity));
        lock (_detailCacheGate)
        {
            _timeEntriesByTask.Remove(taskId);
        }
        return id;
    }

    public async Task UpdateTimeEntryAsync(Guid timeEntryId, DateTime startedAtUtc, int actualMinutes)
    {
        using var service = await CreateServiceClientAsync();
        await EnsureRecordHasNoTimesheetLineAsync(service, TimeTable, timeEntryId, "This tracked time is already included in a timesheet and cannot be edited.");
        var safeMinutes = Math.Max(1, actualMinutes);
        var endedAtUtc = startedAtUtc.AddMinutes(safeMinutes);
        var entity = new Entity(TimeTable, timeEntryId)
        {
            [TimeStartedAt] = startedAtUtc,
            [TimeEndedAt] = endedAtUtc,
            [TimeActualSeconds] = safeMinutes * 60,
            [TimeWorkDate] = startedAtUtc.Date
        };
        await Task.Run(() => service.Update(entity));
        var affectedTaskIds = FindTimeCacheKeys(timeEntryId);
        lock (_detailCacheGate)
        {
            foreach (var taskId in affectedTaskIds)
                _timeEntriesByTask.Remove(taskId);
        }
    }

    public async Task DeleteTimeEntryAsync(Guid timeEntryId)
    {
        using var service = await CreateServiceClientAsync();
        await EnsureRecordHasNoTimesheetLineAsync(service, TimeTable, timeEntryId, "This tracked time is already included in a timesheet and cannot be deleted.");
        await Task.Run(() => service.Delete(TimeTable, timeEntryId));
        var affectedTaskIds = FindTimeCacheKeys(timeEntryId);
        lock (_detailCacheGate)
        {
            foreach (var taskId in affectedTaskIds)
                _timeEntriesByTask.Remove(taskId);
        }
    }

    public void ClearCache()
    {
        InvalidateSnapshot();
        lock (_detailCacheGate)
        {
            _commentsByTask.Clear();
            _timeEntriesByTask.Clear();
        }
    }

    private void InvalidateSnapshot() => _cachedSnapshot = null;

    private List<Guid> FindCommentCacheKeys(Guid commentId)
    {
        lock (_detailCacheGate)
        {
            return _commentsByTask
                .Where(pair => pair.Value.Any(entry => entry.DataverseId == commentId))
                .Select(pair => pair.Key)
                .ToList();
        }
    }

    private List<Guid> FindTimeCacheKeys(Guid timeEntryId)
    {
        lock (_detailCacheGate)
        {
            return _timeEntriesByTask
                .Where(pair => pair.Value.Any(entry => entry.DataverseId == timeEntryId))
                .Select(pair => pair.Key)
                .ToList();
        }
    }

    private List<Guid> FilterIdsToLoad<T>(IEnumerable<Guid> taskIds, Dictionary<Guid, IReadOnlyList<T>> cache, bool forceSync)
    {
        lock (_detailCacheGate)
        {
            return taskIds
                .Distinct()
                .Where(id => forceSync || !cache.ContainsKey(id))
                .ToList();
        }
    }

    private async Task EnsureTaskHasNoTimesheetLinksAsync(Guid taskId)
    {
        if (await HasTimesheetLockedTaskDataAsync(taskId, forceSync: true))
            throw new InvalidOperationException("This task has comments or tracked time already included in a timesheet and cannot be edited or deleted.");
    }

    private static async Task EnsureRecordHasNoTimesheetLineAsync(ServiceClient service, string table, Guid recordId, string message)
    {
        var lineLookup = FindLookupAttribute(RetrieveMetadata(service, table), TimesheetLineTable)?.LogicalName;
        if (string.IsNullOrWhiteSpace(lineLookup)) return;

        var row = await Task.Run(() => service.Retrieve(table, recordId, new ColumnSet(lineLookup)));
        if (row.GetAttributeValue<EntityReference>(lineLookup) is not null)
            throw new InvalidOperationException(message);
    }

    private static async Task<List<Project>> LoadProjectsAsync(ServiceClient service)
    {
        var rows = await RetrieveAllAsync(service, new QueryExpression(ProjectTable)
        {
            ColumnSet = new ColumnSet(ProjectId, ProjectName, ProjectDescription, ProjectCustomer, ProjectColorHex),
            Orders = { new OrderExpression(ProjectName, OrderType.Ascending) }
        });

        return rows.Select((row, index) =>
        {
            var customerReference = row.GetAttributeValue<EntityReference>(ProjectCustomer);
            return new Project
            {
                Id = index + 1,
                DataverseId = row.Id,
                Name = row.GetAttributeValue<string>(ProjectName) ?? "(no project)",
                Description = row.GetAttributeValue<string>(ProjectDescription),
                Customer = customerReference is null
                    ? null
                    : new Customer
                    {
                        Id = index + 1,
                        DataverseId = customerReference.Id,
                        Name = customerReference.Name ?? "(no customer)"
                    },
                ColorHex = NormalizeColorHex(row.GetAttributeValue<string>(ProjectColorHex)) ?? ColorFromGuid(row.Id)
            };
        }).ToList();
    }

    private static async Task<List<TaskItem>> LoadTasksAsync(
        ServiceClient service,
        IReadOnlyList<Project> projects,
        IReadOnlyDictionary<Guid, int> projectIdByDataverseId)
    {
        var rows = await RetrieveAllAsync(service, new QueryExpression(TaskTable)
        {
            ColumnSet = new ColumnSet(TaskId, TaskTitle, TaskDescription, TaskPriorityCode, TaskScheduledStart, TaskScheduledEnd,
                TaskRegarding, TaskProject, TaskWorkType, TaskAppStatus, TaskEstimatedMinutes, TaskDueDate, TaskMobileVisible),
            Criteria = new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(TaskMobileVisible, ConditionOperator.Equal, true),
                    new ConditionExpression(TaskAppStatus, ConditionOperator.NotNull),
                    new ConditionExpression(TaskProject, ConditionOperator.NotNull),
                    new ConditionExpression(TaskRegarding, ConditionOperator.NotNull)
                }
            }
        });

        var projectsById = projects.ToDictionary(p => p.Id);
        var tasks = new List<TaskItem>();
        foreach (var row in rows)
        {
            if (ResolveProjectReference(row) is not Guid projectDataverseId ||
                !projectIdByDataverseId.TryGetValue(projectDataverseId, out var projectId) ||
                !projectsById.TryGetValue(projectId, out var project))
            {
                continue;
            }

            var task = new TaskItem
            {
                Id = tasks.Count + 1,
                DataverseId = row.Id,
                ProjectId = projectId,
                Project = project,
                Title = row.GetAttributeValue<string>(TaskTitle) ?? "(no title)",
                Description = row.GetAttributeValue<string>(TaskDescription),
                Priority = FromDataversePriority(row.GetAttributeValue<OptionSetValue>(TaskPriorityCode)?.Value),
                WorkType = ParseEnum(row.GetAttributeValue<string>(TaskWorkType), WorkType.DeepWork),
                Status = ParseEnum(row.GetAttributeValue<string>(TaskAppStatus), TaskItemStatus.Todo),
                DueDate = row.GetAttributeValue<DateTime?>(TaskDueDate)?.Date,
                ScheduledStart = row.GetAttributeValue<DateTime?>(TaskScheduledStart) is DateTime scheduledStart ? DisplayFormat.ToSpainTime(scheduledStart) : null,
                ScheduledEnd = row.GetAttributeValue<DateTime?>(TaskScheduledEnd) is DateTime scheduledEnd ? DisplayFormat.ToSpainTime(scheduledEnd) : null,
                EstimatedPomodoros = Math.Max(0, (int)Math.Ceiling((row.GetAttributeValue<int?>(TaskEstimatedMinutes) ?? 0) / 30d))
            };

            tasks.Add(task);
        }

        return tasks;
    }

    private static async Task LoadCalendarBlocksAsync(ServiceClient service, IReadOnlyList<TaskItem> tasks)
    {
        var rows = await RetrieveAllAsync(service, new QueryExpression(CalendarTable)
        {
            ColumnSet = new ColumnSet("jts_bloquecalendarioid", CalendarTask, CalendarStart, CalendarEnd)
        });
        var tasksByDataverseId = tasks.Where(t => t.DataverseId is not null).ToDictionary(t => t.DataverseId!.Value);
        var blockIndex = 1;
        foreach (var row in rows)
        {
            if (row.GetAttributeValue<EntityReference>(CalendarTask)?.Id is not Guid taskId ||
                !tasksByDataverseId.TryGetValue(taskId, out var task))
            {
                continue;
            }

            var start = row.GetAttributeValue<DateTime?>(CalendarStart) is DateTime startValue
                ? DisplayFormat.ToSpainTime(startValue)
                : (DateTime?)null;
            var end = row.GetAttributeValue<DateTime?>(CalendarEnd) is DateTime endValue
                ? DisplayFormat.ToSpainTime(endValue)
                : (DateTime?)null;
            if (start is null || end is null || end <= start) continue;

            if (task.ScheduleBlocks.Any(b => b.Start == start && b.End == end)) continue;
            task.ScheduleBlocks.Add(new TaskScheduleBlock
            {
                Id = blockIndex++,
                DataverseId = row.Id,
                TaskItemId = task.Id,
                TaskItem = task,
                Start = start.Value,
                End = end.Value
            });
        }
    }

    private static void WriteTaskFields(Entity entity, TaskItem task, Guid projectDataverseId)
    {
        entity[TaskTitle] = task.Title.Trim();
        entity[TaskDescription] = task.Description ?? string.Empty;
        entity[TaskPriorityCode] = new OptionSetValue(ToDataversePriority(task.Priority));
        entity[TaskWorkType] = task.WorkType.ToString();
        entity[TaskAppStatus] = task.Status.ToString();
        entity[TaskEstimatedMinutes] = Math.Max(0, task.EstimatedPomodoros) * 30;
        entity[TaskDueDate] = task.DueDate?.Date;
        entity[TaskMobileVisible] = true;
        entity[TaskProject] = new EntityReference(ProjectTable, projectDataverseId);
        entity[TaskRegarding] = new EntityReference(ProjectTable, projectDataverseId);
        if (task.ScheduledStart is DateTime scheduledStart) entity[TaskScheduledStart] = DisplayFormat.SpainTimeToUtc(scheduledStart);
        if (task.ScheduledEnd is DateTime scheduledEnd) entity[TaskScheduledEnd] = DisplayFormat.SpainTimeToUtc(scheduledEnd);
    }

    private async Task<ServiceClient> CreateServiceClientAsync()
    {
        var options = new D365Options(
            await _settings.GetD365TenantIdAsync() ?? string.Empty,
            await _settings.GetD365ClientIdAsync() ?? string.Empty,
            await _settings.GetD365ClientSecretAsync() ?? string.Empty,
            await _settings.GetD365EnvironmentUrlAsync() ?? string.Empty);
        if (!options.IsComplete)
            throw new InvalidOperationException("Completa tenant, client, secret y URL de Dataverse en Settings.");

        var client = new ServiceClient(
            $"AuthType=ClientSecret;Url={options.NormalizedEnvironmentUrl};ClientId={options.ClientId};ClientSecret={options.ClientSecret};TenantId={options.TenantId};RequireNewInstance=true");
        if (!client.IsReady)
            throw new InvalidOperationException(client.LastError ?? "Dataverse client is not ready.");
        return client;
    }

    private static async Task DeleteRelatedRecordsAsync(ServiceClient service, string table, string taskLookup, Guid taskId)
    {
        var rows = await RetrieveAllAsync(service, new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(taskLookup, ConditionOperator.Equal, taskId) }
            }
        });

        foreach (var row in rows)
            await Task.Run(() => service.Delete(table, row.Id));
    }

    private static async Task<List<Entity>> RetrieveAllAsync(ServiceClient service, QueryExpression query)
    {
        var rows = new List<Entity>();
        query.PageInfo = new PagingInfo { Count = 500, PageNumber = 1 };
        while (true)
        {
            var page = await Task.Run(() => service.RetrieveMultiple(query));
            rows.AddRange(page.Entities);
            if (!page.MoreRecords) break;
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }

        return rows;
    }

    private static EntityMetadata RetrieveMetadata(ServiceClient service, string logicalName)
    {
        var response = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
        {
            LogicalName = logicalName,
            EntityFilters = EntityFilters.Attributes,
            RetrieveAsIfPublished = true
        });
        return response.EntityMetadata;
    }

    private static LookupAttributeMetadata? FindLookupAttribute(EntityMetadata metadata, string targetLogicalName) =>
        metadata.Attributes
            .OfType<LookupAttributeMetadata>()
            .FirstOrDefault(a => a.Targets?.Contains(targetLogicalName, StringComparer.OrdinalIgnoreCase) == true);

    private static Guid? ResolveProjectReference(Entity row)
    {
        var project = row.GetAttributeValue<EntityReference>(TaskProject);
        if (project is not null) return project.Id;

        var regarding = row.GetAttributeValue<EntityReference>(TaskRegarding);
        return regarding is not null && string.Equals(regarding.LogicalName, ProjectTable, StringComparison.OrdinalIgnoreCase)
            ? regarding.Id
            : null;
    }

    private static int ToDataversePriority(TaskPriority priority) => priority switch
    {
        TaskPriority.Low => 0,
        TaskPriority.High or TaskPriority.Critical => 2,
        _ => 1
    };

    private static TaskPriority FromDataversePriority(int? priority) => priority switch
    {
        0 => TaskPriority.Low,
        2 => TaskPriority.High,
        _ => TaskPriority.Medium
    };

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse(value, true, out T parsed) ? parsed : fallback;

    private static string ColorFromGuid(Guid id)
    {
        var bytes = id.ToByteArray();
        var index = Math.Abs(BitConverter.ToInt32(bytes, 0)) % ProjectPalette.Length;
        return ProjectPalette[index];
    }

    private static string? NormalizeColorHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('#')) trimmed = "#" + trimmed;
        return trimmed.Length == 7 ? trimmed.ToUpperInvariant() : null;
    }
}

public sealed record DataverseTaskSnapshot(IReadOnlyList<Project> Projects, IReadOnlyList<TaskItem> Tasks);

public sealed record DataverseTimeEntryContext(Guid Id, Guid? TaskDataverseId, DateTime StartedAt, DateTime? EndedAt, int ActualMinutes);

public sealed record DataverseCommentContext(Guid Id, Guid? TaskDataverseId, DateTime CreatedAt, string Content);
