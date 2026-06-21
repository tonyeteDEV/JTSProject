using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public partial class VideoAnalysisViewModel : ObservableObject
{
    private readonly DataverseAppDataService _data;
    private readonly DataverseVideoAnalysisService _video;
    private readonly VideoProcessingService _processor;
    private IReadOnlyList<TaskItem> _allTasks = [];

    public ObservableCollection<Project> Projects { get; } = new();
    public ObservableCollection<VideoTaskOption> Tasks { get; } = new();
    public ObservableCollection<VideoAnalysisRecord> RecentAnalyses { get; } = new();
    public ObservableCollection<VideoAnalysisDocumentEditor> SelectedAnalysisDocuments { get; } = new();
    public IReadOnlyList<string> DocumentationLanguages { get; } = ["English", "Español"];

    [ObservableProperty]
    private Project? _selectedProject;

    [ObservableProperty]
    private string _videoPathOrUrl = string.Empty;

    [ObservableProperty]
    private string _context = string.Empty;

    [ObservableProperty]
    private string _selectedDocumentationLanguage = "English";

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _selectedTaskCount;

    [ObservableProperty]
    private Guid? _editingAnalysisId;

    [ObservableProperty]
    private VideoAnalysisDetails? _selectedAnalysisDetails;

    [ObservableProperty]
    private string _selectedAnalysisGlobalDocumentation = string.Empty;

    [ObservableProperty]
    private bool _isMarkdownPreviewEnabled;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private int _processingPercent;

    [ObservableProperty]
    private string _processingProgressText = string.Empty;

    [ObservableProperty]
    private string _processingTimingText = string.Empty;

    private DateTime? _processingStartedAtUtc;

    public bool IsEditing => EditingAnalysisId is not null;
    public bool HasSelectedAnalysisDetails => SelectedAnalysisDetails is not null;
    public bool HasProcessingProgress => IsProcessing || ProcessingPercent > 0;
    public bool IsMarkdownEditorVisible => !IsMarkdownPreviewEnabled;
    public string SelectedAnalysisName => SelectedAnalysisDetails?.Name ?? string.Empty;
    public string SelectedAnalysisProjectName => SelectedAnalysisDetails?.ProjectName ?? string.Empty;
    public string SelectedAnalysisStatusText => SelectedAnalysisDetails?.StatusText ?? string.Empty;
    public string SelectedAnalysisCreatedAtText => SelectedAnalysisDetails?.CreatedAtText ?? string.Empty;
    public string SelectedAnalysisVideoPathOrUrl => SelectedAnalysisDetails?.VideoPathOrUrl ?? string.Empty;
    public string SelectedAnalysisSummary => SelectedAnalysisDetails?.Summary ?? string.Empty;
    public string SelectedAnalysisTranscript => SelectedAnalysisDetails?.Transcript ?? string.Empty;
    public string SelectedAnalysisVisualOcr => SelectedAnalysisDetails?.VisualOcr ?? string.Empty;
    public string FormTitle => IsEditing ? "Edit pending video analysis" : "New video analysis";
    public string PrimaryButtonText => IsEditing ? "Save draft" : "Create draft";

    public VideoAnalysisViewModel(DataverseAppDataService data, DataverseVideoAnalysisService video, VideoProcessingService processor)
    {
        _data = data;
        _video = video;
        _processor = processor;
    }

    partial void OnSelectedProjectChanged(Project? value)
    {
        RebuildTaskOptions();
    }

    partial void OnEditingAnalysisIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }

    partial void OnSelectedAnalysisDetailsChanged(VideoAnalysisDetails? value)
    {
        SelectedAnalysisDocuments.Clear();
        if (value is not null)
        {
            foreach (var document in value.Documents)
                SelectedAnalysisDocuments.Add(new VideoAnalysisDocumentEditor(document));
        }

        SelectedAnalysisGlobalDocumentation = value?.GlobalDocumentation ?? string.Empty;
        OnPropertyChanged(nameof(HasSelectedAnalysisDetails));
        OnPropertyChanged(nameof(SelectedAnalysisName));
        OnPropertyChanged(nameof(SelectedAnalysisProjectName));
        OnPropertyChanged(nameof(SelectedAnalysisStatusText));
        OnPropertyChanged(nameof(SelectedAnalysisCreatedAtText));
        OnPropertyChanged(nameof(SelectedAnalysisVideoPathOrUrl));
        OnPropertyChanged(nameof(SelectedAnalysisSummary));
        OnPropertyChanged(nameof(SelectedAnalysisTranscript));
        OnPropertyChanged(nameof(SelectedAnalysisVisualOcr));
    }

    partial void OnIsMarkdownPreviewEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMarkdownEditorVisible));
    }

    partial void OnIsProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasProcessingProgress));
    }

    partial void OnProcessingPercentChanged(int value)
    {
        OnPropertyChanged(nameof(HasProcessingProgress));
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = "Loading video workspace...";
        try
        {
            var previouslySelected = SelectedProject?.DataverseId;
            var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
            _allTasks = snapshot.Tasks;

            Projects.Clear();
            foreach (var project in snapshot.Projects.OrderBy(project => project.Name))
                Projects.Add(project);

            SelectedProject = previouslySelected is Guid id
                ? Projects.FirstOrDefault(project => project.DataverseId == id) ?? Projects.FirstOrDefault()
                : Projects.FirstOrDefault();

            await LoadRecentAsync();
            Status = "Ready";
        }
        catch (Exception ex)
        {
            Status = $"Could not load video workspace: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadRecentAsync()
    {
        var selectedId = SelectedAnalysisDetails?.Id;
        RecentAnalyses.Clear();
        foreach (var row in await _video.LoadRecentAsync())
            RecentAnalyses.Add(row);
        if (selectedId is Guid id && RecentAnalyses.Any(row => row.Id == id))
            await LoadAnalysisDetailsAsync(id);
    }

    public async Task SaveDraftAsync()
    {
        if (IsBusy) return;
        if (SelectedProject is null)
        {
            Status = "Select a project first.";
            return;
        }

        var selectedTasks = Tasks
            .Where(task => task.IsSelected && task.Task.DataverseId is not null)
            .Select(task => task.Task)
            .ToList();

        IsBusy = true;
        Status = IsEditing ? "Updating pending video analysis..." : "Creating video analysis draft in Dataverse...";
        try
        {
            var draft = new VideoAnalysisDraft(VideoPathOrUrl, SelectedProject, selectedTasks, Context, 0, SelectedDocumentationLanguage);
            var result = EditingAnalysisId is Guid editingId
                ? await _video.UpdateDraftAsync(editingId, draft)
                : await _video.CreateDraftAsync(draft);

            ClearForm();
            await LoadRecentAsync();
            Status = $"Saved analysis {result.VideoId} with {result.DocumentationIds.Count} documentation draft(s).";
        }
        catch (Exception ex)
        {
            Status = $"Could not save video analysis: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task EditDraftAsync(Guid analysisId)
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = "Loading pending analysis...";
        try
        {
            var details = await _video.LoadDraftDetailsAsync(analysisId);
            var project = Projects.FirstOrDefault(project => project.DataverseId == details.ProjectDataverseId);
            if (project is null)
                throw new InvalidOperationException("No se encontro el proyecto del analisis en la cache actual.");

            EditingAnalysisId = analysisId;
            SelectedProject = project;
            VideoPathOrUrl = details.VideoPathOrUrl;
            Context = details.Context;
            SelectedDocumentationLanguage = NormalizeDocumentationLanguage(details.DocumentationLanguage);

            var selectedTaskIds = details.TaskDataverseIds.ToHashSet();
            foreach (var task in Tasks)
                task.IsSelected = task.Task.DataverseId is Guid id && selectedTaskIds.Contains(id);
            UpdateSelectedTaskCount();
            Status = "Editing pending analysis. Save or cancel when ready.";
        }
        catch (Exception ex)
        {
            Status = $"Could not edit analysis: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteDraftAsync(Guid analysisId)
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = "Deleting video analysis...";
        try
        {
            await _video.DeleteAnalysisAsync(analysisId);
            if (EditingAnalysisId == analysisId) ClearForm();
            if (SelectedAnalysisDetails?.Id == analysisId) SelectedAnalysisDetails = null;
            await LoadRecentAsync();
            Status = "Video analysis deleted.";
        }
        catch (Exception ex)
        {
            Status = $"Could not delete analysis: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAnalysisDocumentationAsync()
    {
        if (IsBusy) return;
        if (SelectedAnalysisDetails is null)
        {
            Status = "Select an analysis first.";
            return;
        }

        IsBusy = true;
        Status = "Saving documentation changes...";
        try
        {
            await _video.SaveAnalysisDocumentationAsync(
                SelectedAnalysisDetails.Id,
                SelectedAnalysisGlobalDocumentation,
                SelectedAnalysisDocuments.ToDictionary(document => document.Id, document => document.Markdown));

            SelectedAnalysisDetails = await _video.LoadAnalysisDetailsAsync(SelectedAnalysisDetails.Id);
            Status = "Documentation changes saved.";
        }
        catch (Exception ex)
        {
            Status = $"Could not save documentation changes: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ProcessDraftAsync(Guid analysisId)
    {
        if (IsBusy) return;

        IsBusy = true;
        IsProcessing = true;
        ProcessingPercent = 0;
        ProcessingProgressText = "Starting...";
        ProcessingTimingText = "Elapsed 00:00 - estimating remaining time";
        _processingStartedAtUtc = DateTime.UtcNow;
        Status = "Preparing video processing...";
        try
        {
            var details = await _video.LoadDraftDetailsAsync(analysisId);
            var project = Projects.FirstOrDefault(project => project.DataverseId == details.ProjectDataverseId)
                ?? throw new InvalidOperationException("No se encontro el proyecto del analisis en la cache actual.");
            var selectedTaskIds = details.TaskDataverseIds.ToHashSet();
            var tasks = _allTasks
                .Where(task => task.DataverseId is Guid id && selectedTaskIds.Contains(id))
                .ToList();

            await _video.MarkProcessingAsync(analysisId);
            await LoadRecentAsync();

            var progress = new Progress<VideoProcessingProgress>(UpdateProcessingProgress);
            var output = await _processor.ProcessAsync(new VideoProcessingInput(
                analysisId,
                details.VideoPathOrUrl,
                project,
                tasks,
                details.Context,
                NormalizeDocumentationLanguage(details.DocumentationLanguage)), progress);

            UpdateProcessingProgress(new VideoProcessingProgress(96, "Saving processing result in Dataverse..."));
            await _video.SaveProcessingResultAsync(analysisId, output, "Whisper local + Windows OCR + DeepSeek");
            UpdateProcessingProgress(new VideoProcessingProgress(100, "Processing complete."));
            await LoadRecentAsync();
            await LoadAnalysisDetailsAsync(analysisId);
            if (EditingAnalysisId == analysisId) ClearForm();
            Status = "Video processed and documentation saved in Dataverse.";
        }
        catch (Exception ex)
        {
            try
            {
                await _video.MarkErrorAsync(analysisId, ex.ToString());
                await LoadRecentAsync();
            }
            catch
            {
                // Keep the original processing error visible.
            }

            Status = $"Could not process video: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            IsBusy = false;
        }
    }

    public async Task LoadAnalysisDetailsAsync(Guid analysisId)
    {
        if (IsBusy && !IsProcessing) return;

        try
        {
            SelectedAnalysisDetails = await _video.LoadAnalysisDetailsAsync(analysisId);
            Status = "Analysis details loaded.";
        }
        catch (Exception ex)
        {
            Status = $"Could not load analysis details: {ex.Message}";
        }
    }

    public void CancelEdit()
    {
        ClearForm();
        Status = "Edit cancelled.";
    }

    public void UpdateSelectedTaskCount()
    {
        SelectedTaskCount = Tasks.Count(task => task.IsSelected);
    }

    private void ClearForm()
    {
        EditingAnalysisId = null;
        VideoPathOrUrl = string.Empty;
        Context = string.Empty;
        SelectedDocumentationLanguage = "English";
        foreach (var task in Tasks) task.IsSelected = false;
        UpdateSelectedTaskCount();
    }

    private void UpdateProcessingProgress(VideoProcessingProgress progress)
    {
        ProcessingPercent = progress.Percent;
        ProcessingProgressText = progress.Message;
        Status = progress.Message;

        var elapsed = DateTime.UtcNow - (_processingStartedAtUtc ?? DateTime.UtcNow);
        var remaining = progress.Percent <= 0
            ? (TimeSpan?)null
            : TimeSpan.FromSeconds(Math.Max(0, elapsed.TotalSeconds / progress.Percent * (100 - progress.Percent)));
        ProcessingTimingText = remaining is null
            ? $"Elapsed {FormatDuration(elapsed)} - estimating remaining time"
            : $"Elapsed {FormatDuration(elapsed)} - remaining approx. {FormatDuration(remaining.Value)}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
        return $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static string NormalizeDocumentationLanguage(string? value) =>
        string.Equals(value, "Español", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Spanish", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "es-ES", StringComparison.OrdinalIgnoreCase)
            ? "Español"
            : "English";

    private void RebuildTaskOptions()
    {
        Tasks.Clear();
        if (SelectedProject is null)
        {
            UpdateSelectedTaskCount();
            return;
        }

        foreach (var task in _allTasks
            .Where(task => task.ProjectId == SelectedProject.Id)
            .OrderBy(task => task.Status)
            .ThenBy(task => task.DueDate ?? DateTime.MaxValue)
            .ThenBy(task => task.Title))
        {
            var option = new VideoTaskOption(task);
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(VideoTaskOption.IsSelected))
                    UpdateSelectedTaskCount();
            };
            Tasks.Add(option);
        }

        UpdateSelectedTaskCount();
    }
}

public partial class VideoTaskOption : ObservableObject
{
    public TaskItem Task { get; }

    public string Title => Task.Title;
    public string Detail => $"{Task.Status} - {Task.Priority} - {(Task.DueDate?.ToString("dd/MM/yyyy") ?? "No due date")}";

    [ObservableProperty]
    private bool _isSelected;

    public VideoTaskOption(TaskItem task)
    {
        Task = task;
    }
}

public partial class VideoAnalysisDocumentEditor : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }
    public string TaskName { get; }
    public string StatusText { get; }
    public string CreatedAtText { get; }

    [ObservableProperty]
    private string _markdown;

    public VideoAnalysisDocumentEditor(VideoAnalysisDocumentDetails details)
    {
        Id = details.Id;
        Name = details.Name;
        TaskName = details.TaskName;
        StatusText = details.StatusText;
        CreatedAtText = details.CreatedAtText;
        _markdown = details.Markdown;
    }
}
