using JTS.Data;
using JTS.Data.Entities;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace JTS_App.Services;

public sealed class DataverseVideoAnalysisService
{
    private const string ProjectTable = "jts_proyecto";
    private const string TaskTable = "task";

    private const string VideoTable = "jts_videoanalisis";
    private const string VideoName = "jts_nombre";
    private const string VideoProject = "jts_proyectoprincipalid";
    private const string VideoDate = "jts_fechaanalisis";
    private const string VideoStatus = "jts_estado";
    private const string VideoUrl = "jts_urlvideo";
    private const string VideoDuration = "jts_duracionsegundos";
    private const string VideoLanguage = "jts_idioma";
    private const string VideoModel = "jts_modelousado";
    private const string VideoTranscript = "jts_transcripcioncompleta";
    private const string VideoOcr = "jts_ocrvisualcompleto";
    private const string VideoContext = "jts_contextousado";
    private const string VideoSummary = "jts_resumenglobal";
    private const string VideoDocumentation = "jts_documentacionglobal";
    private const string VideoResultJson = "jts_resultadojson";

    private const string SegmentTable = "jts_videoanalisissegmento";
    private const string SegmentName = "jts_nombre";
    private const string SegmentVideo = "jts_videoanalisisid";
    private const string SegmentProject = "jts_proyectodetectadoid";
    private const string SegmentStart = "jts_iniciosegundos";
    private const string SegmentEnd = "jts_finsegundos";
    private const string SegmentActionType = "jts_tipoaccion";
    private const string SegmentConfidence = "jts_confianza";
    private const string SegmentAudioText = "jts_textoaudio";
    private const string SegmentOcrText = "jts_ocrdetectado";
    private const string SegmentDetectedAction = "jts_acciondetectada";
    private const string SegmentSummary = "jts_resumensegmento";

    private const string SegmentTaskTable = "jts_videoanalisissegmentotarea";
    private const string SegmentTaskName = "jts_nombre";
    private const string SegmentTaskSegment = "jts_segmentoid";
    private const string SegmentTaskTask = "jts_tareaid";
    private const string SegmentTaskConfidence = "jts_confianza";
    private const string SegmentTaskRelevance = "jts_relevancia";
    private const string SegmentTaskConfirmed = "jts_confirmadousuario";
    private const string SegmentTaskReason = "jts_motivoasociacion";

    private const string DocumentationTable = "jts_documentaciongenerada";
    private const string DocumentationName = "jts_nombre";
    private const string DocumentationVideo = "jts_videoanalisisid";
    private const string DocumentationSegment = "jts_segmentoid";
    private const string DocumentationTask = "jts_tareaid";
    private const string DocumentationProject = "jts_proyectoid";
    private const string DocumentationType = "jts_tipo";
    private const string DocumentationStatus = "jts_estado";
    private const string DocumentationDate = "jts_fecha";
    private const string DocumentationGeneratedByAi = "jts_generadoporia";
    private const string DocumentationReviewedByUser = "jts_revisadousuario";
    private const string DocumentationSource = "jts_fuente";
    private const string DocumentationMarkdown = "jts_contenidomarkdown";
    private const string DocumentationEvidenceJson = "jts_evidenciasjson";

    private const int VideoStatusPending = 100000000;
    private const int VideoStatusProcessing = 100000001;
    private const int VideoStatusReviewed = 100000002;
    private const int VideoStatusError = 100000004;
    private const int SegmentActionOther = 100000006;
    private const int SegmentTaskRelevanceMain = 100000000;
    private const int DocumentationTypeTechnical = 100000001;
    private const int DocumentationStatusDraft = 100000000;

    private readonly AppSettingsService _settings;

    public DataverseVideoAnalysisService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<VideoAnalysisRecord>> LoadRecentAsync(int take = 30)
    {
        using var service = await CreateServiceClientAsync();
        var query = new QueryExpression(VideoTable)
        {
            ColumnSet = new ColumnSet(VideoName, VideoProject, VideoDate, VideoStatus, VideoUrl, VideoSummary),
            TopCount = Math.Max(1, take),
            Orders = { new OrderExpression("createdon", OrderType.Descending) }
        };

        var rows = await Task.Run(() => service.RetrieveMultiple(query).Entities);
        return rows.Select(row =>
        {
            var project = row.GetAttributeValue<EntityReference>(VideoProject);
            return new VideoAnalysisRecord(
                row.Id,
                row.GetAttributeValue<string>(VideoName) ?? "(sin nombre)",
                project?.Name ?? string.Empty,
                StatusText(row.GetAttributeValue<OptionSetValue>(VideoStatus)?.Value),
                row.GetAttributeValue<DateTime?>(VideoDate),
                row.GetAttributeValue<string>(VideoUrl) ?? string.Empty,
                row.GetAttributeValue<string>(VideoSummary) ?? string.Empty);
        }).ToList();
    }

    public async Task<VideoAnalysisDetails> LoadAnalysisDetailsAsync(Guid videoAnalysisId)
    {
        using var service = await CreateServiceClientAsync();
        var video = await Task.Run(() => service.Retrieve(
            VideoTable,
            videoAnalysisId,
            new ColumnSet(
                VideoName,
                VideoProject,
                VideoDate,
                VideoStatus,
                VideoUrl,
                VideoModel,
                VideoTranscript,
                VideoOcr,
                VideoContext,
                VideoSummary,
                VideoDocumentation,
                VideoResultJson)));

        var project = video.GetAttributeValue<EntityReference>(VideoProject);
        var documents = await RetrieveAllAsync(service, new QueryExpression(DocumentationTable)
        {
            ColumnSet = new ColumnSet(
                DocumentationName,
                DocumentationTask,
                DocumentationStatus,
                DocumentationDate,
                DocumentationGeneratedByAi,
                DocumentationReviewedByUser,
                DocumentationSource,
                DocumentationMarkdown,
                DocumentationEvidenceJson),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(DocumentationVideo, ConditionOperator.Equal, videoAnalysisId) }
            },
            Orders = { new OrderExpression(DocumentationDate, OrderType.Descending) }
        });

        var documentViews = documents.Select(document =>
        {
            var task = document.GetAttributeValue<EntityReference>(DocumentationTask);
            return new VideoAnalysisDocumentDetails(
                document.Id,
                document.GetAttributeValue<string>(DocumentationName) ?? "(sin nombre)",
                task?.Name ?? string.Empty,
                DocumentationStatusText(document.GetAttributeValue<OptionSetValue>(DocumentationStatus)?.Value),
                document.GetAttributeValue<DateTime?>(DocumentationDate),
                document.GetAttributeValue<bool?>(DocumentationGeneratedByAi) == true,
                document.GetAttributeValue<bool?>(DocumentationReviewedByUser) == true,
                document.GetAttributeValue<string>(DocumentationSource) ?? string.Empty,
                document.GetAttributeValue<string>(DocumentationMarkdown) ?? string.Empty,
                document.GetAttributeValue<string>(DocumentationEvidenceJson) ?? string.Empty);
        }).ToList();

        return new VideoAnalysisDetails(
            video.Id,
            video.GetAttributeValue<string>(VideoName) ?? "(sin nombre)",
            project?.Name ?? string.Empty,
            StatusText(video.GetAttributeValue<OptionSetValue>(VideoStatus)?.Value),
            video.GetAttributeValue<DateTime?>(VideoDate),
            video.GetAttributeValue<string>(VideoUrl) ?? string.Empty,
            video.GetAttributeValue<string>(VideoModel) ?? string.Empty,
            video.GetAttributeValue<string>(VideoContext) ?? string.Empty,
            video.GetAttributeValue<string>(VideoSummary) ?? string.Empty,
            video.GetAttributeValue<string>(VideoDocumentation) ?? string.Empty,
            video.GetAttributeValue<string>(VideoTranscript) ?? string.Empty,
            video.GetAttributeValue<string>(VideoOcr) ?? string.Empty,
            video.GetAttributeValue<string>(VideoResultJson) ?? string.Empty,
            documentViews);
    }

    public async Task<IReadOnlyList<VideoDocumentationContext>> LoadRecentDocumentationContextAsync(DateTime sinceUtc, int take = 80)
    {
        using var service = await CreateServiceClientAsync();
        var query = new QueryExpression(DocumentationTable)
        {
            ColumnSet = new ColumnSet(
                DocumentationName,
                DocumentationVideo,
                DocumentationTask,
                DocumentationProject,
                DocumentationStatus,
                DocumentationDate,
                DocumentationMarkdown),
            TopCount = Math.Max(1, take),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(DocumentationDate, ConditionOperator.OnOrAfter, sinceUtc)
                }
            },
            Orders = { new OrderExpression(DocumentationDate, OrderType.Descending) }
        };

        var rows = await Task.Run(() => service.RetrieveMultiple(query).Entities);
        return rows.Select(row =>
        {
            var video = row.GetAttributeValue<EntityReference>(DocumentationVideo);
            var task = row.GetAttributeValue<EntityReference>(DocumentationTask);
            var project = row.GetAttributeValue<EntityReference>(DocumentationProject);
            return new VideoDocumentationContext(
                row.Id,
                video?.Id,
                video?.Name ?? string.Empty,
                task?.Id,
                task?.Name ?? string.Empty,
                project?.Id,
                project?.Name ?? string.Empty,
                DocumentationStatusText(row.GetAttributeValue<OptionSetValue>(DocumentationStatus)?.Value),
                row.GetAttributeValue<DateTime?>(DocumentationDate),
                row.GetAttributeValue<string>(DocumentationMarkdown) ?? string.Empty);
        }).ToList();
    }

    public async Task<VideoAnalysisDraftDetails> LoadDraftDetailsAsync(Guid videoAnalysisId)
    {
        using var service = await CreateServiceClientAsync();
        var video = await Task.Run(() => service.Retrieve(
            VideoTable,
            videoAnalysisId,
            new ColumnSet(VideoProject, VideoStatus, VideoUrl, VideoContext, VideoLanguage)));

        var projectId = video.GetAttributeValue<EntityReference>(VideoProject)?.Id
            ?? throw new InvalidOperationException("El analisis no tiene proyecto principal.");
        var segments = await RetrieveAllAsync(service, new QueryExpression(SegmentTable)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(SegmentVideo, ConditionOperator.Equal, videoAnalysisId) }
            }
        });

        var taskIds = new List<Guid>();
        if (segments.Count > 0)
        {
            var segmentIds = segments.Select(segment => (object)segment.Id).ToArray();
            var relations = await RetrieveAllAsync(service, new QueryExpression(SegmentTaskTable)
            {
                ColumnSet = new ColumnSet(SegmentTaskTask),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression(SegmentTaskSegment, ConditionOperator.In, segmentIds) }
                }
            });
            taskIds.AddRange(relations
                .Select(row => row.GetAttributeValue<EntityReference>(SegmentTaskTask)?.Id)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct());
        }

        return new VideoAnalysisDraftDetails(
            videoAnalysisId,
            projectId,
            video.GetAttributeValue<string>(VideoUrl) ?? string.Empty,
            video.GetAttributeValue<string>(VideoContext) ?? string.Empty,
            NormalizeDocumentationLanguage(video.GetAttributeValue<string>(VideoLanguage)),
            taskIds);
    }

    public async Task MarkProcessingAsync(Guid videoAnalysisId)
    {
        using var service = await CreateServiceClientAsync();
        await Task.Run(() => service.Update(new Entity(VideoTable, videoAnalysisId)
        {
            [VideoStatus] = new OptionSetValue(VideoStatusProcessing),
            [VideoSummary] = "Procesando video desde JTS."
        }));
    }

    public async Task MarkErrorAsync(Guid videoAnalysisId, string error)
    {
        using var service = await CreateServiceClientAsync();
        await Task.Run(() => service.Update(new Entity(VideoTable, videoAnalysisId)
        {
            [VideoStatus] = new OptionSetValue(VideoStatusError),
            [VideoSummary] = "Error procesando el video.",
            [VideoResultJson] = error
        }));
    }

    public async Task SaveProcessingResultAsync(Guid videoAnalysisId, VideoProcessingOutput output, string modelName)
    {
        using var service = await CreateServiceClientAsync();

        await Task.Run(() => service.Update(new Entity(VideoTable, videoAnalysisId)
        {
            [VideoStatus] = new OptionSetValue(VideoStatusReviewed),
            [VideoModel] = modelName,
            [VideoTranscript] = output.Transcript,
            [VideoOcr] = output.VisualOcr,
            [VideoSummary] = output.GlobalSummary,
            [VideoDocumentation] = output.GlobalDocumentation,
            [VideoResultJson] = output.ResultJson
        }));

        var segments = await RetrieveAllAsync(service, new QueryExpression(SegmentTable)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(SegmentVideo, ConditionOperator.Equal, videoAnalysisId) }
            }
        });

        foreach (var segment in segments)
        {
            await Task.Run(() => service.Update(new Entity(SegmentTable, segment.Id)
            {
                [SegmentAudioText] = output.Transcript,
                [SegmentOcrText] = output.VisualOcr,
                [SegmentDetectedAction] = "Documentacion generada desde el procesamiento del video.",
                [SegmentSummary] = output.GlobalSummary,
                [SegmentConfidence] = 100m
            }));
        }

        var documents = await RetrieveAllAsync(service, new QueryExpression(DocumentationTable)
        {
            ColumnSet = new ColumnSet(DocumentationTask),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(DocumentationVideo, ConditionOperator.Equal, videoAnalysisId) }
            }
        });

        foreach (var document in documents)
        {
            var taskId = document.GetAttributeValue<EntityReference>(DocumentationTask)?.Id;
            if (taskId is null || !output.TaskDocumentation.TryGetValue(taskId.Value, out var markdown)) continue;

            await Task.Run(() => service.Update(new Entity(DocumentationTable, document.Id)
            {
                [DocumentationStatus] = new OptionSetValue(DocumentationStatusDraft),
                [DocumentationGeneratedByAi] = true,
                [DocumentationReviewedByUser] = false,
                [DocumentationMarkdown] = markdown,
                [DocumentationEvidenceJson] = output.ResultJson,
                [DocumentationDate] = DateTime.UtcNow
            }));
        }
    }

    public async Task SaveAnalysisDocumentationAsync(
        Guid videoAnalysisId,
        string globalDocumentation,
        IReadOnlyDictionary<Guid, string> documentMarkdownById)
    {
        using var service = await CreateServiceClientAsync();

        await Task.Run(() => service.Update(new Entity(VideoTable, videoAnalysisId)
        {
            [VideoDocumentation] = globalDocumentation
        }));

        foreach (var (documentId, markdown) in documentMarkdownById)
        {
            await Task.Run(() => service.Update(new Entity(DocumentationTable, documentId)
            {
                [DocumentationMarkdown] = markdown,
                [DocumentationReviewedByUser] = true,
                [DocumentationDate] = DateTime.UtcNow
            }));
        }
    }

    public async Task<VideoAnalysisCreateResult> CreateDraftAsync(VideoAnalysisDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.VideoPathOrUrl))
            throw new InvalidOperationException("Indica la ruta o URL del video.");
        if (draft.Project.DataverseId is not Guid projectId)
            throw new InvalidOperationException("El proyecto seleccionado no tiene identificador de Dataverse.");

        var selectedTasks = SelectedDataverseTasks(draft).ToList();

        using var service = await CreateServiceClientAsync();
        var now = DateTime.UtcNow;
        var name = $"Video docs - {draft.Project.Name} - {DisplayFormat.Date(DateTime.Now)}";

        var video = new Entity(VideoTable)
        {
            [VideoName] = name,
            [VideoProject] = new EntityReference(ProjectTable, projectId),
            [VideoDate] = now,
            [VideoStatus] = new OptionSetValue(VideoStatusPending),
            [VideoUrl] = draft.VideoPathOrUrl.Trim(),
            [VideoDuration] = Math.Max(0, draft.DurationSeconds),
            [VideoLanguage] = NormalizeDocumentationLanguage(draft.DocumentationLanguage),
            [VideoContext] = draft.Context?.Trim() ?? string.Empty,
            [VideoSummary] = "Analisis creado desde JTS. Pendiente de procesar audio, imagen y documentacion final."
        };
        var videoId = await Task.Run(() => service.Create(video));

        return await RecreateDraftChildrenAsync(service, videoId, projectId, selectedTasks, draft, now);
    }

    public async Task<VideoAnalysisCreateResult> UpdateDraftAsync(Guid videoAnalysisId, VideoAnalysisDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.VideoPathOrUrl))
            throw new InvalidOperationException("Indica la ruta o URL del video.");
        if (draft.Project.DataverseId is not Guid projectId)
            throw new InvalidOperationException("El proyecto seleccionado no tiene identificador de Dataverse.");

        var selectedTasks = SelectedDataverseTasks(draft).ToList();

        using var service = await CreateServiceClientAsync();
        var existing = await Task.Run(() => service.Retrieve(VideoTable, videoAnalysisId, new ColumnSet(VideoStatus)));
        EnsureEditable(existing);

        var video = new Entity(VideoTable, videoAnalysisId)
        {
            [VideoProject] = new EntityReference(ProjectTable, projectId),
            [VideoUrl] = draft.VideoPathOrUrl.Trim(),
            [VideoDuration] = Math.Max(0, draft.DurationSeconds),
            [VideoLanguage] = NormalizeDocumentationLanguage(draft.DocumentationLanguage),
            [VideoContext] = draft.Context?.Trim() ?? string.Empty
        };
        await Task.Run(() => service.Update(video));

        await DeleteDraftChildrenAsync(service, videoAnalysisId);
        return await RecreateDraftChildrenAsync(service, videoAnalysisId, projectId, selectedTasks, draft, DateTime.UtcNow);
    }

    public async Task DeleteAnalysisAsync(Guid videoAnalysisId)
    {
        using var service = await CreateServiceClientAsync();

        await DeleteDraftChildrenAsync(service, videoAnalysisId);
        await Task.Run(() => service.Delete(VideoTable, videoAnalysisId));
    }

    private static IEnumerable<TaskItem> SelectedDataverseTasks(VideoAnalysisDraft draft) =>
        draft.Tasks
            .Where(task => task.DataverseId is not null)
            .DistinctBy(task => task.DataverseId!.Value);

    private static async Task<VideoAnalysisCreateResult> RecreateDraftChildrenAsync(
        ServiceClient service,
        Guid videoId,
        Guid projectId,
        IReadOnlyList<TaskItem> selectedTasks,
        VideoAnalysisDraft draft,
        DateTime now)
    {
        var segment = new Entity(SegmentTable)
        {
            [SegmentName] = "Segmento inicial",
            [SegmentVideo] = new EntityReference(VideoTable, videoId),
            [SegmentProject] = new EntityReference(ProjectTable, projectId),
            [SegmentStart] = 0,
            [SegmentEnd] = Math.Max(0, draft.DurationSeconds),
            [SegmentActionType] = new OptionSetValue(SegmentActionOther),
            [SegmentConfidence] = selectedTasks.Count > 0 ? 100m : 0m,
            [SegmentSummary] = "Segmento inicial creado a partir de la seleccion manual de tareas."
        };
        var segmentId = await Task.Run(() => service.Create(segment));

        var relationIds = new List<Guid>();
        var documentationIds = new List<Guid>();
        foreach (var task in selectedTasks)
        {
            var taskId = task.DataverseId!.Value;
            var relation = new Entity(SegmentTaskTable)
            {
                [SegmentTaskName] = $"{task.Title} - relacion manual",
                [SegmentTaskSegment] = new EntityReference(SegmentTable, segmentId),
                [SegmentTaskTask] = new EntityReference(TaskTable, taskId),
                [SegmentTaskConfidence] = 100m,
                [SegmentTaskRelevance] = new OptionSetValue(SegmentTaskRelevanceMain),
                [SegmentTaskConfirmed] = true,
                [SegmentTaskReason] = "Asociacion indicada manualmente al registrar el video."
            };
            relationIds.Add(await Task.Run(() => service.Create(relation)));

            var documentation = new Entity(DocumentationTable)
            {
                [DocumentationName] = $"Borrador - {task.Title}",
                [DocumentationVideo] = new EntityReference(VideoTable, videoId),
                [DocumentationSegment] = new EntityReference(SegmentTable, segmentId),
                [DocumentationTask] = new EntityReference(TaskTable, taskId),
                [DocumentationProject] = new EntityReference(ProjectTable, projectId),
                [DocumentationType] = new OptionSetValue(DocumentationTypeTechnical),
                [DocumentationStatus] = new OptionSetValue(DocumentationStatusDraft),
                [DocumentationDate] = now,
                [DocumentationGeneratedByAi] = false,
                [DocumentationReviewedByUser] = false,
                [DocumentationSource] = "JTS video intake",
                [DocumentationMarkdown] = BuildInitialMarkdown(draft, task)
            };
            documentationIds.Add(await Task.Run(() => service.Create(documentation)));
        }

        return new VideoAnalysisCreateResult(videoId, segmentId, relationIds, documentationIds);
    }

    private static async Task DeleteDraftChildrenAsync(ServiceClient service, Guid videoAnalysisId)
    {
        var documents = await RetrieveAllAsync(service, new QueryExpression(DocumentationTable)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(DocumentationVideo, ConditionOperator.Equal, videoAnalysisId) }
            }
        });
        foreach (var document in documents)
            await Task.Run(() => service.Delete(DocumentationTable, document.Id));

        var segments = await RetrieveAllAsync(service, new QueryExpression(SegmentTable)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(SegmentVideo, ConditionOperator.Equal, videoAnalysisId) }
            }
        });

        if (segments.Count > 0)
        {
            var segmentIds = segments.Select(segment => (object)segment.Id).ToArray();
            var relations = await RetrieveAllAsync(service, new QueryExpression(SegmentTaskTable)
            {
                ColumnSet = new ColumnSet(false),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression(SegmentTaskSegment, ConditionOperator.In, segmentIds) }
                }
            });
            foreach (var relation in relations)
                await Task.Run(() => service.Delete(SegmentTaskTable, relation.Id));
        }

        foreach (var segment in segments)
            await Task.Run(() => service.Delete(SegmentTable, segment.Id));
    }

    private static string BuildInitialMarkdown(VideoAnalysisDraft draft, TaskItem task)
    {
        var context = string.IsNullOrWhiteSpace(draft.Context)
            ? "Pendiente de completar con el analisis del video."
            : draft.Context.Trim();
        return $"""
        # Documentacion pendiente de analisis

        **Proyecto:** {draft.Project.Name}
        **Tarea:** {task.Title}
        **Video:** {draft.VideoPathOrUrl}

        ## Contexto inicial

        {context}

        ## Resultado

        Pendiente de procesar el video para generar documentacion tecnica asociada a esta tarea.
        """;
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

    private static string StatusText(int? status) => status switch
    {
        VideoStatusPending => "Pendiente",
        VideoStatusProcessing => "Procesando",
        VideoStatusReviewed => "Revisado",
        100000003 => "Publicado",
        VideoStatusError => "Error",
        _ => "Sin estado"
    };

    private static string DocumentationStatusText(int? status) => status switch
    {
        DocumentationStatusDraft => "Borrador",
        100000001 => "Aprobado",
        100000002 => "Aplicado",
        100000003 => "Descartado",
        _ => "Sin estado"
    };

    private static string NormalizeDocumentationLanguage(string? value) =>
        string.Equals(value, "Español", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Spanish", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "es-ES", StringComparison.OrdinalIgnoreCase)
            ? "Español"
            : "English";

    private static void EnsureEditable(Entity video)
    {
        var status = video.GetAttributeValue<OptionSetValue>(VideoStatus)?.Value;
        if (status is not (VideoStatusPending or VideoStatusError))
            throw new InvalidOperationException("Solo se pueden modificar o eliminar analisis pendientes o en error.");
    }

    private static async Task<List<Entity>> RetrieveAllAsync(ServiceClient service, QueryExpression query)
    {
        query.PageInfo = new PagingInfo { PageNumber = 1, Count = 5000 };
        var rows = new List<Entity>();
        while (true)
        {
            var page = await Task.Run(() => service.RetrieveMultiple(query));
            rows.AddRange(page.Entities);
            if (!page.MoreRecords) return rows;
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }
    }
}

public sealed record VideoAnalysisDraft(
    string VideoPathOrUrl,
    Project Project,
    IReadOnlyList<TaskItem> Tasks,
    string? Context,
    int DurationSeconds,
    string DocumentationLanguage);

public sealed record VideoAnalysisCreateResult(
    Guid VideoId,
    Guid SegmentId,
    IReadOnlyList<Guid> RelationIds,
    IReadOnlyList<Guid> DocumentationIds);

public sealed record VideoAnalysisDraftDetails(
    Guid Id,
    Guid ProjectDataverseId,
    string VideoPathOrUrl,
    string Context,
    string DocumentationLanguage,
    IReadOnlyList<Guid> TaskDataverseIds);

public sealed record VideoAnalysisRecord(
    Guid Id,
    string Name,
    string ProjectName,
    string StatusText,
    DateTime? CreatedAt,
    string VideoPathOrUrl,
    string Summary)
{
    public string CreatedAtText => CreatedAt is DateTime value ? DisplayFormat.DateTimeFromUtc(value) : string.Empty;
    public bool IsPending => string.Equals(StatusText, "Pendiente", StringComparison.OrdinalIgnoreCase);
    public bool IsError => string.Equals(StatusText, "Error", StringComparison.OrdinalIgnoreCase);
    public bool IsReviewed => string.Equals(StatusText, "Revisado", StringComparison.OrdinalIgnoreCase);
    public bool CanModify => IsPending || IsError;
    public bool CanProcess => IsPending || IsError || IsReviewed;
    public bool CanDelete => true;
}

public sealed record VideoAnalysisDetails(
    Guid Id,
    string Name,
    string ProjectName,
    string StatusText,
    DateTime? CreatedAt,
    string VideoPathOrUrl,
    string ModelUsed,
    string Context,
    string Summary,
    string GlobalDocumentation,
    string Transcript,
    string VisualOcr,
    string ResultJson,
    IReadOnlyList<VideoAnalysisDocumentDetails> Documents)
{
    public string CreatedAtText => CreatedAt is DateTime value ? DisplayFormat.DateTimeFromUtc(value) : string.Empty;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasGlobalDocumentation => !string.IsNullOrWhiteSpace(GlobalDocumentation);
    public bool HasTranscript => !string.IsNullOrWhiteSpace(Transcript);
    public bool HasVisualOcr => !string.IsNullOrWhiteSpace(VisualOcr);
    public bool HasResultJson => !string.IsNullOrWhiteSpace(ResultJson);
    public bool HasDocuments => Documents.Count > 0;
}

public sealed record VideoAnalysisDocumentDetails(
    Guid Id,
    string Name,
    string TaskName,
    string StatusText,
    DateTime? CreatedAt,
    bool GeneratedByAi,
    bool ReviewedByUser,
    string Source,
    string Markdown,
    string EvidenceJson)
{
    public string CreatedAtText => CreatedAt is DateTime value ? DisplayFormat.DateTimeFromUtc(value) : string.Empty;
    public bool HasMarkdown => !string.IsNullOrWhiteSpace(Markdown);
}

public sealed record VideoDocumentationContext(
    Guid Id,
    Guid? VideoAnalysisId,
    string VideoAnalysisName,
    Guid? TaskDataverseId,
    string TaskName,
    Guid? ProjectDataverseId,
    string ProjectName,
    string StatusText,
    DateTime? CreatedAt,
    string Markdown);
