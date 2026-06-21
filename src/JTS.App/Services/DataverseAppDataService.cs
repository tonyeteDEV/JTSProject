using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JTS.Data;
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
    private const int CacheVersion = 1;
    private static readonly TimeSpan PersistentCacheTtl = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions PersistentCacheJsonOptions = new(JsonSerializerDefaults.Web);

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
    private const string TaskChecklist = "jts_checklist";
    private const string TaskRecurrence = "jts_recurrence";

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
    private string? _cacheEnvironmentHash;
    private string? _commentTimesheetLineLookup;
    private string? _timeTimesheetLineLookup;

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

            if (!forceSync && await ReadPersistentCacheAsync<DataverseTaskSnapshotCache>("snapshot") is { } snapshotCache)
            {
                _cachedSnapshot = FromCache(snapshotCache);
                return _cachedSnapshot;
            }

            using var service = await CreateServiceClientAsync();
            var projects = await LoadProjectsAsync(service);
            var projectIdByDataverseId = projects
                .Where(p => p.DataverseId is not null)
                .ToDictionary(p => p.DataverseId!.Value, p => p.Id);
            var tasks = await LoadTasksAsync(service, projects, projectIdByDataverseId);
            await LoadCalendarBlocksAsync(service, tasks);
            _cachedSnapshot = new DataverseTaskSnapshot(projects, tasks);
            await WritePersistentCacheAsync("snapshot", ToCache(_cachedSnapshot));
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

        var byTask = await LoadCommentsByTaskAsync([taskId], forceSync);
        return byTask.TryGetValue(taskId, out var comments) ? comments : [];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TaskJournalEntry>>> LoadCommentsByTaskAsync(IEnumerable<Guid> taskIds, bool forceSync = false)
    {
        var ids = taskIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, IReadOnlyList<TaskJournalEntry>>();

        await PreloadCommentsAsync(ids, forceSync);
        lock (_detailCacheGate)
        {
            return ids.ToDictionary(
                id => id,
                id => _commentsByTask.TryGetValue(id, out var comments)
                    ? comments
                    : (IReadOnlyList<TaskJournalEntry>)[]);
        }
    }

    public async Task<IReadOnlyList<PomodoroSession>> LoadTimeEntriesAsync(Guid taskId, bool forceSync = false)
    {
        lock (_detailCacheGate)
        {
            if (!forceSync && _timeEntriesByTask.TryGetValue(taskId, out var cached)) return cached;
        }

        var byTask = await LoadTimeEntriesByTaskAsync([taskId], forceSync);
        return byTask.TryGetValue(taskId, out var entries) ? entries : [];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PomodoroSession>>> LoadTimeEntriesByTaskAsync(IEnumerable<Guid> taskIds, bool forceSync = false)
    {
        var ids = taskIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, IReadOnlyList<PomodoroSession>>();

        await PreloadTimeEntriesAsync(ids, forceSync);
        lock (_detailCacheGate)
        {
            return ids.ToDictionary(
                id => id,
                id => _timeEntriesByTask.TryGetValue(id, out var entries)
                    ? entries
                    : (IReadOnlyList<PomodoroSession>)[]);
        }
    }

    public async Task<DataverseTaskDetailsSnapshot> LoadTaskDetailsSnapshotAsync(IEnumerable<Guid> taskIds, bool forceSync = false)
    {
        var ids = taskIds.Distinct().ToList();
        if (ids.Count == 0)
            return new DataverseTaskDetailsSnapshot(
                new Dictionary<Guid, IReadOnlyList<TaskJournalEntry>>(),
                new Dictionary<Guid, IReadOnlyList<PomodoroSession>>());

        var commentsTask = LoadCommentsByTaskAsync(ids, forceSync);
        var timeTask = LoadTimeEntriesByTaskAsync(ids, forceSync);
        await Task.WhenAll(commentsTask, timeTask);
        return new DataverseTaskDetailsSnapshot(await commentsTask, await timeTask);
    }

    public async Task<DataverseTaskDetailsSnapshot> LoadTaskDetailsForSpainDateAsync(IEnumerable<Guid> taskIds, DateTime spainDate)
    {
        var ids = taskIds.Distinct().ToList();
        var emptyComments = ids.ToDictionary(id => id, _ => (IReadOnlyList<TaskJournalEntry>)[]);
        var emptyTimeEntries = ids.ToDictionary(id => id, _ => (IReadOnlyList<PomodoroSession>)[]);
        if (ids.Count == 0) return new DataverseTaskDetailsSnapshot(emptyComments, emptyTimeEntries);

        using var service = await CreateServiceClientAsync();
        var startUtc = DisplayFormat.SpainDayStartUtc(spainDate);
        var endUtc = DisplayFormat.SpainDayStartUtc(spainDate.AddDays(1));

        var commentLineLookup = GetCommentTimesheetLineLookup(service);
        var commentColumns = new ColumnSet("jts_comentariotareaid", CommentTask, CommentContent, "createdon");
        if (!string.IsNullOrWhiteSpace(commentLineLookup)) commentColumns.AddColumn(commentLineLookup);

        var timeLineLookup = GetTimeTimesheetLineLookup(service);
        var timeColumns = new ColumnSet("jts_tiempotareaid", TimeTask, TimeStartedAt, TimeEndedAt, TimeActualSeconds, TimeWorkDate);
        if (!string.IsNullOrWhiteSpace(timeLineLookup)) timeColumns.AddColumn(timeLineLookup);

        var commentRows = new List<Entity>();
        var timeRows = new List<Entity>();
        foreach (var chunk in ids.Chunk(200))
        {
            var chunkIds = chunk.Cast<object>().ToArray();
            commentRows.AddRange(await RetrieveAllAsync(service, new QueryExpression(CommentTable)
            {
                ColumnSet = commentColumns,
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(CommentTask, ConditionOperator.In, chunkIds),
                        new ConditionExpression("createdon", ConditionOperator.OnOrAfter, startUtc),
                        new ConditionExpression("createdon", ConditionOperator.LessThan, endUtc)
                    }
                },
                Orders = { new OrderExpression("createdon", OrderType.Descending) }
            }));

            timeRows.AddRange(await RetrieveAllAsync(service, new QueryExpression(TimeTable)
            {
                ColumnSet = timeColumns,
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(TimeTask, ConditionOperator.In, chunkIds),
                        new ConditionExpression(TimeStartedAt, ConditionOperator.OnOrAfter, startUtc),
                        new ConditionExpression(TimeStartedAt, ConditionOperator.LessThan, endUtc)
                    }
                }
            }));
        }

        var commentsByTask = commentRows
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

        var timeEntriesByTask = timeRows
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

        return new DataverseTaskDetailsSnapshot(
            ids.ToDictionary(id => id, id => commentsByTask.TryGetValue(id, out var comments) ? comments : emptyComments[id]),
            ids.ToDictionary(id => id, id => timeEntriesByTask.TryGetValue(id, out var entries) ? entries : emptyTimeEntries[id]));
    }

    public async Task PreloadCommentsAsync(IEnumerable<Guid> taskIds, bool forceSync = false)
    {
        var ids = FilterIdsToLoad(taskIds, _commentsByTask, forceSync);
        if (ids.Count == 0) return;

        var idsToLoad = new List<Guid>();
        foreach (var id in ids)
        {
            if (!forceSync && await ReadPersistentCacheAsync<List<TaskJournalEntryCache>>($"comments-{id:N}") is { } cachedComments)
            {
                lock (_detailCacheGate)
                {
                    _commentsByTask[id] = FromCache(cachedComments);
                }
            }
            else
            {
                idsToLoad.Add(id);
            }
        }
        if (idsToLoad.Count == 0) return;

        using var service = await CreateServiceClientAsync();
        var commentLineLookup = GetCommentTimesheetLineLookup(service);
        var columns = new ColumnSet("jts_comentariotareaid", CommentTask, CommentContent, "createdon");
        if (!string.IsNullOrWhiteSpace(commentLineLookup)) columns.AddColumn(commentLineLookup);

        var rows = new List<Entity>();
        foreach (var chunk in idsToLoad.Chunk(200))
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
            foreach (var id in idsToLoad)
                _commentsByTask[id] = grouped.TryGetValue(id, out var comments) ? comments : [];
        }

        foreach (var id in idsToLoad)
        {
            IReadOnlyList<TaskJournalEntry> comments;
            lock (_detailCacheGate)
            {
                comments = _commentsByTask.TryGetValue(id, out var cached) ? cached : [];
            }
            await WritePersistentCacheAsync($"comments-{id:N}", ToCache(comments));
        }
    }

    public async Task PreloadTimeEntriesAsync(IEnumerable<Guid> taskIds, bool forceSync = false)
    {
        var ids = FilterIdsToLoad(taskIds, _timeEntriesByTask, forceSync);
        if (ids.Count == 0) return;

        var idsToLoad = new List<Guid>();
        foreach (var id in ids)
        {
            if (!forceSync && await ReadPersistentCacheAsync<List<PomodoroSessionCache>>($"time-{id:N}") is { } cachedEntries)
            {
                lock (_detailCacheGate)
                {
                    _timeEntriesByTask[id] = FromCache(cachedEntries);
                }
            }
            else
            {
                idsToLoad.Add(id);
            }
        }
        if (idsToLoad.Count == 0) return;

        using var service = await CreateServiceClientAsync();
        var timeLineLookup = GetTimeTimesheetLineLookup(service);
        var columns = new ColumnSet("jts_tiempotareaid", TimeTask, TimeStartedAt, TimeEndedAt, TimeActualSeconds, TimeWorkDate);
        if (!string.IsNullOrWhiteSpace(timeLineLookup)) columns.AddColumn(timeLineLookup);

        var rows = new List<Entity>();
        foreach (var chunk in idsToLoad.Chunk(200))
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
            foreach (var id in idsToLoad)
                _timeEntriesByTask[id] = grouped.TryGetValue(id, out var entries) ? entries : [];
        }

        foreach (var id in idsToLoad)
        {
            IReadOnlyList<PomodoroSession> entries;
            lock (_detailCacheGate)
            {
                entries = _timeEntriesByTask.TryGetValue(id, out var cached) ? cached : [];
            }
            await WritePersistentCacheAsync($"time-{id:N}", ToCache(entries));
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
        DeletePersistentCache($"comments-{taskId:N}");
        DeletePersistentCache($"time-{taskId:N}");
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

    public async Task<Guid> AddCalendarBlockAsync(Guid taskId, string taskTitle, DateTime startedAt, DateTime endedAt, string source = "Desktop")
    {
        using var service = await CreateServiceClientAsync();
        var entity = BuildCalendarBlockEntity(taskId, taskTitle, startedAt, endedAt, source);
        var id = await Task.Run(() => service.Create(entity));
        InvalidateSnapshot();
        return id;
    }

    public async Task AddCalendarBlocksAsync(Guid taskId, string taskTitle, IReadOnlyList<(DateTime Start, DateTime End)> blocks, string source)
    {
        if (blocks.Count == 0) return;
        using var service = await CreateServiceClientAsync();
        await Task.Run(() =>
        {
            foreach (var block in blocks)
                service.Create(BuildCalendarBlockEntity(taskId, taskTitle, block.Start, block.End, source));
        });
        InvalidateSnapshot();
    }

    private static Entity BuildCalendarBlockEntity(Guid taskId, string taskTitle, DateTime startedAt, DateTime endedAt, string source) =>
        new(CalendarTable)
        {
            [CalendarName] = $"{taskTitle} {startedAt:yyyy-MM-dd HH:mm}",
            [CalendarTask] = new EntityReference(TaskTable, taskId),
            [CalendarStart] = DisplayFormat.SpainTimeToUtc(startedAt),
            [CalendarEnd] = DisplayFormat.SpainTimeToUtc(endedAt),
            [CalendarSource] = source
        };

    public async Task UpdateCalendarBlockAsync(Guid blockId, DateTime startedAt, DateTime endedAt)
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(CalendarTable, blockId)
        {
            [CalendarStart] = DisplayFormat.SpainTimeToUtc(startedAt),
            [CalendarEnd] = DisplayFormat.SpainTimeToUtc(endedAt)
        };
        await Task.Run(() => service.Update(entity));
        InvalidateSnapshot();
    }

    public async Task DeleteRecurrenceBlocksFromAsync(Guid taskId, DateTime fromDateSpain)
    {
        using var service = await CreateServiceClientAsync();
        var fromUtc = DisplayFormat.SpainTimeToUtc(fromDateSpain.Date);
        var query = new QueryExpression(CalendarTable)
        {
            ColumnSet = new ColumnSet("jts_bloquecalendarioid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(CalendarTask, ConditionOperator.Equal, taskId),
                    new ConditionExpression(CalendarSource, ConditionOperator.Equal, "Recurrence"),
                    new ConditionExpression(CalendarStart, ConditionOperator.GreaterEqual, fromUtc)
                }
            }
        };
        var rows = await RetrieveAllAsync(service, query);
        await Task.Run(() =>
        {
            foreach (var row in rows)
                service.Delete(CalendarTable, row.Id);
        });
        InvalidateSnapshot();
    }

    public async Task UpdateTaskRecurrenceAsync(Guid taskId, string? json)
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(TaskTable, taskId)
        {
            [TaskRecurrence] = json,
            [TaskMobileVisible] = true
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
        var id = await Task.Run(() => service.Create(entity));
        lock (_detailCacheGate)
        {
            if (_commentsByTask.TryGetValue(taskId, out var cached))
            {
                var updated = new List<TaskJournalEntry>
                {
                    new()
                    {
                        Id = 1,
                        DataverseId = id,
                        Content = content.Trim(),
                        CreatedAt = DateTime.UtcNow
                    }
                };
                updated.AddRange(cached.Select((entry, index) =>
                {
                    entry.Id = index + 2;
                    return entry;
                }));
                _commentsByTask[taskId] = updated;
            }
        }
        DeletePersistentCache($"comments-{taskId:N}");
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
        if (affectedTaskIds.Count == 0) ClearPersistentCacheFiles("comments-*.json");
        foreach (var taskId in affectedTaskIds) DeletePersistentCache($"comments-{taskId:N}");
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
        if (affectedTaskIds.Count == 0) ClearPersistentCacheFiles("comments-*.json");
        foreach (var taskId in affectedTaskIds) DeletePersistentCache($"comments-{taskId:N}");
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
            if (_timeEntriesByTask.TryGetValue(taskId, out var cached))
            {
                var actualMinutes = Math.Max(1, (int)Math.Ceiling(seconds / 60d));
                var updated = new List<PomodoroSession>(cached)
                {
                    new()
                    {
                        Id = cached.Count + 1,
                        DataverseId = id,
                        StartedAt = startedAt,
                        EndedAt = endedAt,
                        ActualMinutes = actualMinutes,
                        PlannedMinutes = actualMinutes,
                        SessionType = PomodoroSessionType.Work,
                        Completed = true
                    }
                };
                _timeEntriesByTask[taskId] = updated;
            }
        }
        DeletePersistentCache($"time-{taskId:N}");
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
        if (affectedTaskIds.Count == 0) ClearPersistentCacheFiles("time-*.json");
        foreach (var taskId in affectedTaskIds) DeletePersistentCache($"time-{taskId:N}");
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
        if (affectedTaskIds.Count == 0) ClearPersistentCacheFiles("time-*.json");
        foreach (var taskId in affectedTaskIds) DeletePersistentCache($"time-{taskId:N}");
    }

    public void ClearCache()
    {
        InvalidateSnapshot();
        lock (_detailCacheGate)
        {
            _commentsByTask.Clear();
            _timeEntriesByTask.Clear();
        }
        ClearPersistentCacheFiles("comments-*.json");
        ClearPersistentCacheFiles("time-*.json");
    }

    private void InvalidateSnapshot()
    {
        _cachedSnapshot = null;
        ClearPersistentCacheFiles("snapshot-*.json");
    }

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
                TaskRegarding, TaskProject, TaskWorkType, TaskAppStatus, TaskEstimatedMinutes, TaskDueDate, TaskMobileVisible, TaskChecklist, TaskRecurrence),
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
            task.ChecklistItems = DeserializeChecklist(row.GetAttributeValue<string>(TaskChecklist), task.Id);
            task.RecurrenceJson = row.GetAttributeValue<string>(TaskRecurrence);

            tasks.Add(task);
        }

        return tasks;
    }

    private static async Task LoadCalendarBlocksAsync(ServiceClient service, IReadOnlyList<TaskItem> tasks)
    {
        var rows = await RetrieveAllAsync(service, new QueryExpression(CalendarTable)
        {
            ColumnSet = new ColumnSet("jts_bloquecalendarioid", CalendarTask, CalendarStart, CalendarEnd, CalendarSource)
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
                End = end.Value,
                Source = row.GetAttributeValue<string>(CalendarSource)
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
        entity[TaskChecklist] = SerializeChecklist(task.ChecklistItems);
    }

    private static readonly JsonSerializerOptions ChecklistJsonOptions = new() { WriteIndented = false };

    private sealed record ChecklistItemDto(string t, bool d);

    private static string? SerializeChecklist(IReadOnlyList<TaskChecklistItem>? items)
    {
        if (items is null || items.Count == 0) return null;
        var dtos = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Title))
            .Select(i => new ChecklistItemDto(i.Title.Trim(), i.IsCompleted))
            .ToList();
        return dtos.Count == 0 ? null : JsonSerializer.Serialize(dtos, ChecklistJsonOptions);
    }

    private static List<TaskChecklistItem> DeserializeChecklist(string? json, int taskItemId)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var dtos = JsonSerializer.Deserialize<List<ChecklistItemDto>>(json);
            if (dtos is null) return new();
            return dtos
                .Where(d => !string.IsNullOrWhiteSpace(d.t))
                .Select((d, index) => new TaskChecklistItem
                {
                    Id = index + 1,
                    TaskItemId = taskItemId,
                    Title = d.t.Trim(),
                    IsCompleted = d.d,
                    SortOrder = index
                })
                .ToList();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public async Task UpdateTaskChecklistAsync(Guid taskId, IReadOnlyList<TaskChecklistItem> items)
    {
        using var service = await CreateServiceClientAsync();
        var entity = new Entity(TaskTable, taskId)
        {
            [TaskChecklist] = SerializeChecklist(items),
            [TaskMobileVisible] = true
        };
        await Task.Run(() => service.Update(entity));
        InvalidateSnapshot();
    }

    private async Task<T?> ReadPersistentCacheAsync<T>(string cacheName)
    {
        try
        {
            var path = await GetPersistentCachePathAsync(cacheName);
            if (!File.Exists(path)) return default;

            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync<PersistentCacheEnvelope<T>>(stream, PersistentCacheJsonOptions);
            if (envelope is null || envelope.Version != CacheVersion) return default;
            if (DateTime.UtcNow - envelope.CachedAtUtc > PersistentCacheTtl) return default;
            return envelope.Data;
        }
        catch (Exception ex)
        {
            App.Log($"[DataverseAppDataService] Cache read failed for {cacheName}: {ex.Message}");
            return default;
        }
    }

    private async Task WritePersistentCacheAsync<T>(string cacheName, T data)
    {
        try
        {
            var path = await GetPersistentCachePathAsync(cacheName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var envelope = new PersistentCacheEnvelope<T>(CacheVersion, DateTime.UtcNow, data);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, envelope, PersistentCacheJsonOptions);
        }
        catch (Exception ex)
        {
            App.Log($"[DataverseAppDataService] Cache write failed for {cacheName}: {ex.Message}");
        }
    }

    private async Task<string> GetPersistentCachePathAsync(string cacheName)
    {
        AppPaths.EnsureCreated();
        var hash = await GetCacheEnvironmentHashAsync();
        return Path.Combine(AppPaths.AppDataRoot, "cache", $"{cacheName}-{hash}.json");
    }

    private async Task<string> GetCacheEnvironmentHashAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cacheEnvironmentHash)) return _cacheEnvironmentHash;
        var environment = (await _settings.GetD365EnvironmentUrlAsync())?.Trim().ToLowerInvariant() ?? "default";
        _cacheEnvironmentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment)))[..12];
        return _cacheEnvironmentHash;
    }

    private static void DeletePersistentCache(string cacheName) =>
        ClearPersistentCacheFiles($"{cacheName}-*.json");

    private static void ClearPersistentCacheFiles(string pattern)
    {
        try
        {
            var directory = Path.Combine(AppPaths.AppDataRoot, "cache");
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            App.Log($"[DataverseAppDataService] Cache cleanup failed for {pattern}: {ex.Message}");
        }
    }

    private static DataverseTaskSnapshotCache ToCache(DataverseTaskSnapshot snapshot) =>
        new(
            snapshot.Projects.Select(project => new ProjectCache(
                project.Id,
                project.DataverseId,
                project.Name,
                project.Description,
                project.ColorHex,
                project.CreatedAt,
                project.ParentProjectId,
                project.Customer is null
                    ? null
                    : new CustomerCache(
                        project.Customer.Id,
                        project.Customer.DataverseId,
                        project.Customer.Name,
                        project.Customer.ContactInfo,
                        project.Customer.Notes,
                        project.Customer.CreatedAt))).ToList(),
            snapshot.Tasks.Select(task => new TaskItemCache(
                task.Id,
                task.DataverseId,
                task.ProjectId,
                task.ParentTaskId,
                task.Title,
                task.Description,
                task.WorkType,
                task.Priority,
                task.EstimatedPomodoros,
                task.DueDate,
                task.ScheduledStart,
                task.ScheduledEnd,
                task.Status,
                task.CreatedAt,
                task.CompletedAt,
                task.RecurrenceJson,
                task.ChecklistItems.Select(item => new ChecklistItemCache(
                    item.Id,
                    item.DataverseId,
                    item.TaskItemId,
                    item.Title,
                    item.IsCompleted,
                    item.SortOrder,
                    item.CreatedAt,
                    item.CompletedAt)).ToList(),
                task.ScheduleBlocks.Select(block => new ScheduleBlockCache(
                    block.Id,
                    block.DataverseId,
                    block.TaskItemId,
                    block.Start,
                    block.End,
                    block.Source,
                    block.CreatedAt)).ToList())).ToList());

    private static DataverseTaskSnapshot FromCache(DataverseTaskSnapshotCache cache)
    {
        var projects = cache.Projects.Select(project => new Project
        {
            Id = project.Id,
            DataverseId = project.DataverseId,
            Name = project.Name,
            Description = project.Description,
            ColorHex = project.ColorHex,
            CreatedAt = project.CreatedAt,
            ParentProjectId = project.ParentProjectId,
            Customer = project.Customer is null
                ? null
                : new Customer
                {
                    Id = project.Customer.Id,
                    DataverseId = project.Customer.DataverseId,
                    Name = project.Customer.Name,
                    ContactInfo = project.Customer.ContactInfo,
                    Notes = project.Customer.Notes,
                    CreatedAt = project.Customer.CreatedAt
                }
        }).ToList();
        var projectsById = projects.ToDictionary(project => project.Id);

        var tasks = cache.Tasks.Select(task =>
        {
            var project = projectsById.TryGetValue(task.ProjectId, out var foundProject)
                ? foundProject
                : projects.FirstOrDefault();
            var taskItem = new TaskItem
            {
                Id = task.Id,
                DataverseId = task.DataverseId,
                ProjectId = task.ProjectId,
                Project = project,
                ParentTaskId = task.ParentTaskId,
                Title = task.Title,
                Description = task.Description,
                WorkType = task.WorkType,
                Priority = task.Priority,
                EstimatedPomodoros = task.EstimatedPomodoros,
                DueDate = task.DueDate,
                ScheduledStart = task.ScheduledStart,
                ScheduledEnd = task.ScheduledEnd,
                Status = task.Status,
                CreatedAt = task.CreatedAt,
                CompletedAt = task.CompletedAt,
                RecurrenceJson = task.RecurrenceJson
            };
            taskItem.ChecklistItems = task.ChecklistItems.Select(item => new TaskChecklistItem
            {
                Id = item.Id,
                DataverseId = item.DataverseId,
                TaskItemId = taskItem.Id,
                TaskItem = taskItem,
                Title = item.Title,
                IsCompleted = item.IsCompleted,
                SortOrder = item.SortOrder,
                CreatedAt = item.CreatedAt,
                CompletedAt = item.CompletedAt
            }).ToList();
            taskItem.ScheduleBlocks = task.ScheduleBlocks.Select(block => new TaskScheduleBlock
            {
                Id = block.Id,
                DataverseId = block.DataverseId,
                TaskItemId = taskItem.Id,
                TaskItem = taskItem,
                Start = block.Start,
                End = block.End,
                Source = block.Source,
                CreatedAt = block.CreatedAt
            }).ToList();
            project?.Tasks.Add(taskItem);
            return taskItem;
        }).ToList();

        return new DataverseTaskSnapshot(projects, tasks);
    }

    private static List<TaskJournalEntryCache> ToCache(IReadOnlyList<TaskJournalEntry> comments) =>
        comments.Select(comment => new TaskJournalEntryCache(
            comment.Id,
            comment.DataverseId,
            comment.TaskItemId,
            comment.Content,
            comment.CreatedAt,
            comment.TimesheetLineDataverseId)).ToList();

    private static IReadOnlyList<TaskJournalEntry> FromCache(List<TaskJournalEntryCache> comments) =>
        comments.Select(comment => new TaskJournalEntry
        {
            Id = comment.Id,
            DataverseId = comment.DataverseId,
            TaskItemId = comment.TaskItemId,
            Content = comment.Content,
            CreatedAt = comment.CreatedAt,
            TimesheetLineDataverseId = comment.TimesheetLineDataverseId
        }).ToList();

    private static List<PomodoroSessionCache> ToCache(IReadOnlyList<PomodoroSession> entries) =>
        entries.Select(entry => new PomodoroSessionCache(
            entry.Id,
            entry.DataverseId,
            entry.TaskItemId,
            entry.StartedAt,
            entry.EndedAt,
            entry.PlannedMinutes,
            entry.ActualMinutes,
            entry.SessionType,
            entry.Completed,
            entry.InterruptionCount,
            entry.TimesheetLineDataverseId)).ToList();

    private static IReadOnlyList<PomodoroSession> FromCache(List<PomodoroSessionCache> entries) =>
        entries.Select(entry => new PomodoroSession
        {
            Id = entry.Id,
            DataverseId = entry.DataverseId,
            TaskItemId = entry.TaskItemId,
            StartedAt = entry.StartedAt,
            EndedAt = entry.EndedAt,
            PlannedMinutes = entry.PlannedMinutes,
            ActualMinutes = entry.ActualMinutes,
            SessionType = entry.SessionType,
            Completed = entry.Completed,
            InterruptionCount = entry.InterruptionCount,
            TimesheetLineDataverseId = entry.TimesheetLineDataverseId
        }).ToList();

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

    private string? GetCommentTimesheetLineLookup(ServiceClient service)
    {
        if (_commentTimesheetLineLookup is not null) return _commentTimesheetLineLookup;
        _commentTimesheetLineLookup = FindLookupAttribute(RetrieveMetadata(service, CommentTable), TimesheetLineTable)?.LogicalName ?? string.Empty;
        return string.IsNullOrWhiteSpace(_commentTimesheetLineLookup) ? null : _commentTimesheetLineLookup;
    }

    private string? GetTimeTimesheetLineLookup(ServiceClient service)
    {
        if (_timeTimesheetLineLookup is not null) return _timeTimesheetLineLookup;
        _timeTimesheetLineLookup = FindLookupAttribute(RetrieveMetadata(service, TimeTable), TimesheetLineTable)?.LogicalName ?? string.Empty;
        return string.IsNullOrWhiteSpace(_timeTimesheetLineLookup) ? null : _timeTimesheetLineLookup;
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

    private sealed record PersistentCacheEnvelope<T>(int Version, DateTime CachedAtUtc, T Data);

    private sealed record DataverseTaskSnapshotCache(List<ProjectCache> Projects, List<TaskItemCache> Tasks);

    private sealed record ProjectCache(
        int Id,
        Guid? DataverseId,
        string Name,
        string? Description,
        string? ColorHex,
        DateTime CreatedAt,
        int? ParentProjectId,
        CustomerCache? Customer);

    private sealed record CustomerCache(
        int Id,
        Guid? DataverseId,
        string Name,
        string? ContactInfo,
        string? Notes,
        DateTime CreatedAt);

    private sealed record TaskItemCache(
        int Id,
        Guid? DataverseId,
        int ProjectId,
        int? ParentTaskId,
        string Title,
        string? Description,
        WorkType WorkType,
        TaskPriority Priority,
        int EstimatedPomodoros,
        DateTime? DueDate,
        DateTime? ScheduledStart,
        DateTime? ScheduledEnd,
        TaskItemStatus Status,
        DateTime CreatedAt,
        DateTime? CompletedAt,
        string? RecurrenceJson,
        List<ChecklistItemCache> ChecklistItems,
        List<ScheduleBlockCache> ScheduleBlocks);

    private sealed record ChecklistItemCache(
        int Id,
        Guid? DataverseId,
        int TaskItemId,
        string Title,
        bool IsCompleted,
        int SortOrder,
        DateTime CreatedAt,
        DateTime? CompletedAt);

    private sealed record ScheduleBlockCache(
        int Id,
        Guid? DataverseId,
        int TaskItemId,
        DateTime Start,
        DateTime End,
        string? Source,
        DateTime CreatedAt);

    private sealed record TaskJournalEntryCache(
        int Id,
        Guid? DataverseId,
        int TaskItemId,
        string Content,
        DateTime CreatedAt,
        Guid? TimesheetLineDataverseId);

    private sealed record PomodoroSessionCache(
        int Id,
        Guid? DataverseId,
        int? TaskItemId,
        DateTime StartedAt,
        DateTime? EndedAt,
        int PlannedMinutes,
        int ActualMinutes,
        PomodoroSessionType SessionType,
        bool Completed,
        int InterruptionCount,
        Guid? TimesheetLineDataverseId);
}

public sealed record DataverseTaskSnapshot(IReadOnlyList<Project> Projects, IReadOnlyList<TaskItem> Tasks);

public sealed record DataverseTaskDetailsSnapshot(
    IReadOnlyDictionary<Guid, IReadOnlyList<TaskJournalEntry>> CommentsByTask,
    IReadOnlyDictionary<Guid, IReadOnlyList<PomodoroSession>> TimeEntriesByTask);

public sealed record DataverseTimeEntryContext(Guid Id, Guid? TaskDataverseId, DateTime StartedAt, DateTime? EndedAt, int ActualMinutes);

public sealed record DataverseCommentContext(Guid Id, Guid? TaskDataverseId, DateTime CreatedAt, string Content);
