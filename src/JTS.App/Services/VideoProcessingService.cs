using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http.Headers;
using JTS.AI;
using JTS.Data;
using JTS.Data.Entities;
using NAudio.Wave;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace JTS_App.Services;

public sealed class VideoProcessingService
{
    private const int MaxFrameSamples = 36;
    private const int MaxVisualKeyframes = 240;
    private const int MaxDenseScanFrames = 12000;
    private const int MaxRepositoryContextChars = 24000;
    private readonly AppSettingsService _settings;
    private readonly WhisperTranscriber _transcriber;
    private readonly DeepSeekClient _deepSeek;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(8) };

    public VideoProcessingService(AppSettingsService settings, WhisperTranscriber transcriber, DeepSeekClient deepSeek)
    {
        _settings = settings;
        _transcriber = transcriber;
        _deepSeek = deepSeek;
    }

    public async Task<VideoProcessingOutput> ProcessAsync(
        VideoProcessingInput input,
        IProgress<VideoProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.VideoPathOrUrl))
            throw new InvalidOperationException("El analisis no tiene ruta o URL de video.");
        if (!File.Exists(input.VideoPathOrUrl))
            throw new FileNotFoundException("El video local no existe. Para procesarlo debe ser una ruta local accesible.", input.VideoPathOrUrl);

        Report(progress, 2, "Preparing video processing...");
        Report(progress, 8, "Extracting audio...");
        var audioResult = await TryExtractAndTranscribeAudioAsync(input.VideoPathOrUrl, progress, cancellationToken);

        Report(progress, 38, "Sampling frames and running OCR...");
        var visualResult = await TryExtractVisualContextAsync(input.VideoPathOrUrl, progress, cancellationToken);

        Report(progress, 66, "Running visual AI analysis...");
        var visualAiResult = await TryAnalyzeFramesWithVisualAiAsync(input, visualResult.Frames, progress, cancellationToken);

        Report(progress, 76, "Collecting repository context...");
        var repositoryResult = await TryBuildRepositoryContextAsync(input, cancellationToken);

        Report(progress, 80, "Generating documentation with DeepSeek...");
        var documentation = await GenerateDocumentationAsync(input, audioResult.Transcript, visualResult.OcrText, visualAiResult.SceneDescriptions, repositoryResult.Context, progress, cancellationToken);

        var result = new
        {
            video = input.VideoPathOrUrl,
            processedAtUtc = DateTime.UtcNow,
            audio = audioResult.Diagnostics,
            visual = visualResult.Diagnostics,
            visualAi = visualAiResult.Diagnostics,
            repository = repositoryResult.Diagnostics,
            tasks = documentation.TaskDocumentation.Keys
        };

        return new VideoProcessingOutput(
            audioResult.Transcript,
            visualResult.OcrText,
            documentation.GlobalSummary,
            documentation.GlobalDocumentation,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
            documentation.TaskDocumentation);
    }

    private async Task<AudioProcessingResult> TryExtractAndTranscribeAudioAsync(
        string videoPath,
        IProgress<VideoProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var modelPath = await _settings.GetWhisperModelPathAsync();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return new AudioProcessingResult(string.Empty, "Whisper model is not configured.");

        var tempDir = Path.Combine(AppPaths.AppDataRoot, "video-processing");
        Directory.CreateDirectory(tempDir);
        var wavPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wav");

        try
        {
            await Task.Run(() =>
            {
                using var reader = new MediaFoundationReader(videoPath);
                var outFormat = new WaveFormat(16000, 16, 1);
                using var resampler = new MediaFoundationResampler(reader, outFormat)
                {
                    ResamplerQuality = 60
                };
                WaveFileWriter.CreateWaveFile(wavPath, resampler);
            }, cancellationToken);

            Report(progress, 18, "Transcribing audio locally...");
            var transcript = await _transcriber.TranscribeAsync(modelPath, wavPath, cancellationToken);
            Report(progress, 35, "Audio transcription finished.");
            return new AudioProcessingResult(transcript, "Audio extracted and transcribed with local Whisper.");
        }
        catch (Exception ex)
        {
            return new AudioProcessingResult(string.Empty, "Audio processing failed: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        }
    }

    private async Task<VisualProcessingResult> TryExtractVisualContextAsync(
        string videoPath,
        IProgress<VideoProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(videoPath).AsTask(cancellationToken);
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var duration = clip.OriginalDuration;
            if (duration <= TimeSpan.Zero)
                return new VisualProcessingResult(string.Empty, "Video duration could not be detected.", []);

            var ocrEngine = OcrEngine.TryCreateFromLanguage(new Language("es-ES"))
                ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine is null)
                return new VisualProcessingResult(string.Empty, "Windows OCR engine is not available.", []);

            var mode = await _settings.GetVideoAnalysisModeAsync();
            var deepMode = string.Equals(mode, "Deep", StringComparison.OrdinalIgnoreCase);
            var frameInterval = await GetIntSettingAsync("Video.VisualFrameIntervalSeconds", 6, 1, 60);
            var maxFrames = await GetIntSettingAsync("Video.VisualMaxFrames", MaxFrameSamples, 1, MaxVisualKeyframes);
            var scanFps = await GetDoubleSettingAsync("Video.VisualScanFps", 30, 1, 30);
            var samples = deepMode
                ? await SelectKeyFrameTimesByVisualChangeAsync(composition, duration, maxFrames, scanFps, progress, cancellationToken)
                : BuildSampleTimes(duration, maxFrames, frameInterval);
            var sb = new StringBuilder();
            var frames = new List<VideoFrameSample>();
            var index = 1;
            foreach (var sample in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameStart = deepMode ? 55 : 40;
                var frameRange = deepMode ? 9 : 22;
                var framePercent = frameStart + (int)Math.Round((index - 1) / Math.Max(1d, samples.Count) * frameRange);
                Report(progress, framePercent, $"Reading frame {index}/{samples.Count}...");

                using var stream = await composition
                    .GetThumbnailAsync(sample, 1920, 1080, VideoFramePrecision.NearestFrame)
                    .AsTask(cancellationToken);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
                var text = await RecognizeFrameRegionsAsync(decoder, ocrEngine, cancellationToken);
                var dataUrl = await EncodeJpegDataUrlAsync(decoder, cancellationToken);
                frames.Add(new VideoFrameSample(sample, text, dataUrl));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine($"[{FormatTime(sample)}]");
                    sb.AppendLine(text);
                    sb.AppendLine();
                }

                index++;
            }

            var ocrText = sb.ToString().Trim();
            Report(progress, 64, "Visual OCR finished.");
            var diagnostics = deepMode
                ? $"OCR completed from {samples.Count} keyframe(s) selected after scanning at {scanFps:0.##} FPS."
                : $"OCR completed from {samples.Count} frame samples.";

            return new VisualProcessingResult(
                ocrText,
                string.IsNullOrWhiteSpace(ocrText)
                    ? "Frames sampled, but no OCR text was detected."
                    : diagnostics,
                frames);
        }
        catch (Exception ex)
        {
            return new VisualProcessingResult(string.Empty, "Visual processing failed: " + ex.Message, []);
        }
    }

    private async Task<VisualAiProcessingResult> TryAnalyzeFramesWithVisualAiAsync(
        VideoProcessingInput input,
        IReadOnlyList<VideoFrameSample> frames,
        IProgress<VideoProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var mode = await _settings.GetVideoAnalysisModeAsync();
            if (!string.Equals(mode, "Deep", StringComparison.OrdinalIgnoreCase))
                return new VisualAiProcessingResult(string.Empty, "Visual AI skipped because video analysis mode is Fast.");

            var endpoint = NormalizeChatCompletionsEndpoint(await _settings.GetVideoVisualEndpointAsync());
            var model = await _settings.GetVideoVisualModelAsync();
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
                return new VisualAiProcessingResult(string.Empty, "Visual AI skipped because endpoint or model is not configured.");

            var apiKey = await _settings.GetVideoVisualApiKeyAsync();
            var selectedFrames = SelectVisualAiFrames(frames, await GetIntSettingAsync("Video.VisualMaxFrames", 18, 1, MaxVisualKeyframes)).ToList();
            if (selectedFrames.Count == 0)
                return new VisualAiProcessingResult(string.Empty, "Visual AI skipped because no frames were available.");

            var sb = new StringBuilder();
            var batches = selectedFrames.Chunk(4).ToList();
            for (var i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = batches[i];
                Report(progress, 66 + (int)Math.Round(i / Math.Max(1d, batches.Count) * 9d), $"Analyzing visual batch {i + 1}/{batches.Count}...");
                var response = await AnalyzeVisualBatchAsync(endpoint, model!, apiKey, input, batch, cancellationToken);
                if (string.IsNullOrWhiteSpace(response)) continue;

                sb.AppendLine($"## Visual scene batch {i + 1}");
                foreach (var frame in batch)
                    sb.AppendLine($"- frame {FormatTime(frame.Timestamp)}");
                sb.AppendLine(response.Trim());
                sb.AppendLine();
            }

            var sceneDescriptions = sb.ToString().Trim();
            return new VisualAiProcessingResult(
                sceneDescriptions,
                string.IsNullOrWhiteSpace(sceneDescriptions)
                    ? "Visual AI returned no useful scene descriptions."
                    : $"Visual AI analyzed {selectedFrames.Count} frame(s) in {batches.Count} batch(es).");
        }
        catch (Exception ex)
        {
            return new VisualAiProcessingResult(string.Empty, "Visual AI failed: " + ex.Message);
        }
    }

    private async Task<string> AnalyzeVisualBatchAsync(
        string endpoint,
        string model,
        string? apiKey,
        VideoProcessingInput input,
        IReadOnlyList<VideoFrameSample> frames,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        var content = new List<object>
        {
            new
            {
                type = "text",
                text = $"""
Analyze these frames from a screen recording. This can be any application, not only code editors.

Project: {input.Project.Name}
Customer: {input.Project.Customer?.Name}
Initial user context: {input.InitialContext}
Documentation language: {input.DocumentationLanguage}

For each timestamp, describe:
- what application or screen type is visible if you can infer it
- what the user appears to be doing
- important UI changes, forms, fields, dialogs, errors, saves, navigation, tests, or outputs
- confidence level and uncertainty

Do not invent unreadable text. Use OCR snippets only as weak evidence.

Frame OCR snippets:
{string.Join("\n\n", frames.Select(frame => $"[{FormatTime(frame.Timestamp)}]\n{EmptyText(frame.OcrText)}"))}
"""
            }
        };

        foreach (var frame in frames)
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new { url = frame.ImageDataUrl }
            });
        }

        var payload = new
        {
            model,
            temperature = 0.1,
            max_tokens = 2200,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a careful visual analyst for screen recordings. You analyze screenshots from any desktop or web application and produce evidence-grounded observations."
                },
                new
                {
                    role = "user",
                    content
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Visual AI endpoint returned {(int)response.StatusCode}: {TrimTo(body, 1000)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var messageContent))
            {
                return ExtractContentText(messageContent);
            }
        }

        if (doc.RootElement.TryGetProperty("message", out var directMessage) &&
            directMessage.TryGetProperty("content", out var directContent))
        {
            return ExtractContentText(directContent);
        }

        return string.Empty;
    }

    private async Task<DocumentationProcessingResult> GenerateDocumentationAsync(
        VideoProcessingInput input,
        string transcript,
        string visualOcr,
        string visualAiContext,
        string repositoryContext,
        IProgress<VideoProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        var model = await _settings.GetDeepSeekModelAsync();
        var thinking = string.Equals(await _settings.GetDeepSeekThinkingEnabledAsync(), "true", StringComparison.OrdinalIgnoreCase);
        var options = new DeepSeekRequestOptions(
            string.IsNullOrWhiteSpace(model) ? DeepSeekClient.DefaultModel : model!,
            thinking);

        var context = BuildSharedContext(input, transcript, visualOcr, visualAiContext, repositoryContext);
        var languageInstruction = DocumentationLanguageInstruction(input.DocumentationLanguage);
        if (string.IsNullOrWhiteSpace(apiKey))
            return BuildFallbackDocumentation(input, transcript, visualOcr, visualAiContext, repositoryContext);

        try
        {
            var global = await _deepSeek.ChatAsync(apiKey, new[]
            {
                new DeepSeekMessage("system", $"You are a senior technical assistant documenting real work from screen recordings, audio, OCR, visual AI scene analysis and app/repository context. Do not invent unsupported steps. If OCR contains broken or incoherent tokens, treat them as low-confidence evidence. Prioritize visual AI scene observations, audio transcript, repository changes, file names, diffs and task context. {languageInstruction}"),
                new DeepSeekMessage("user", $"""
Genera un resumen global del video y una documentacion tecnica general.

{context}

Devuelve Markdown claro con:
- Resumen ejecutivo
- Acciones observadas
- Evidencias usadas
- Riesgos o huecos si faltan datos
""")
            }, options, cancellationToken);

            var taskDocs = new Dictionary<Guid, string>();
            var dataverseTasks = input.Tasks.Where(t => t.DataverseId is not null).ToList();
            var taskIndex = 0;
            foreach (var task in dataverseTasks)
            {
                taskIndex++;
                var percent = 78 + (int)Math.Round((taskIndex - 1) / Math.Max(1d, dataverseTasks.Count) * 17d);
                Report(progress, percent, $"Generating documentation for {task.Title}...");
                var doc = await _deepSeek.ChatAsync(apiKey, new[]
                {
                new DeepSeekMessage("system", $"Generate concrete documentation for one task. Do not invent. If OCR is noisy, do not turn it into fake commands. Use visual AI scene observations, audio, repository context and diffs when available to explain what changed and why. {languageInstruction}"),
                    new DeepSeekMessage("user", $"""
Tarea objetivo:
- Titulo: {task.Title}
- Proyecto: {input.Project.Name}
- Descripcion de tarea: {task.Description}

Contexto completo del video:
{context}

Devuelve Markdown con:
- Resumen de lo realizado para esta tarea
- Pasos tecnicos observados
- Cambios o configuraciones relevantes
- Evidencias desde audio/OCR/analisis visual
- Pendientes o dudas
""")
                }, options, cancellationToken);

                taskDocs[task.DataverseId!.Value] = string.IsNullOrWhiteSpace(doc) ? BuildFallbackTaskDoc(input, task, transcript, visualOcr, visualAiContext, repositoryContext) : doc.Trim();
            }

            return new DocumentationProcessingResult(
                FirstParagraph(global),
                string.IsNullOrWhiteSpace(global) ? "No global documentation generated." : global.Trim(),
                taskDocs);
        }
        catch (Exception ex)
        {
            var fallback = BuildFallbackDocumentation(input, transcript, visualOcr, visualAiContext, repositoryContext);
            return fallback with
            {
                GlobalDocumentation = fallback.GlobalDocumentation + "\n\n> DeepSeek generation failed: " + ex.Message
            };
        }
    }

    private static DocumentationProcessingResult BuildFallbackDocumentation(VideoProcessingInput input, string transcript, string visualOcr, string visualAiContext, string repositoryContext)
    {
        var summary = $"Video processed for project {input.Project.Name}.";
        var global = $"""
        # Video documentation

        ## Project
        {input.Project.Name}

        ## Initial context
        {input.InitialContext}

        ## Audio transcript
        {EmptyText(transcript)}

        ## Visual OCR
        {EmptyText(visualOcr)}

        ## Visual AI scene analysis
        {EmptyText(visualAiContext)}

        ## Repository context
        {EmptyText(repositoryContext)}
        """;

        var taskDocs = input.Tasks
            .Where(task => task.DataverseId is not null)
            .ToDictionary(
                task => task.DataverseId!.Value,
                task => BuildFallbackTaskDoc(input, task, transcript, visualOcr, visualAiContext, repositoryContext));

        return new DocumentationProcessingResult(summary, global, taskDocs);
    }

    private static string BuildFallbackTaskDoc(VideoProcessingInput input, TaskItem task, string transcript, string visualOcr, string visualAiContext, string repositoryContext) =>
        $"""
        # {task.Title}

        ## Context
        Project: {input.Project.Name}

        {input.InitialContext}

        ## Evidence from audio
        {EmptyText(transcript)}

        ## Evidence from screen OCR
        {EmptyText(visualOcr)}

        ## Evidence from visual AI scene analysis
        {EmptyText(visualAiContext)}

        ## Repository context
        {EmptyText(repositoryContext)}
        """;

    private static string BuildSharedContext(VideoProcessingInput input, string transcript, string visualOcr, string visualAiContext, string repositoryContext)
    {
        var tasks = input.Tasks.Count == 0
            ? "- No task selected."
            : string.Join("\n", input.Tasks.Select(task => $"- {task.Title}: {task.Description}"));
        return $"""
Video: {input.VideoPathOrUrl}
Documentation language: {input.DocumentationLanguage}
Project: {input.Project.Name}
Project description: {input.Project.Description}
Customer: {input.Project.Customer?.Name}
Initial user context:
{input.InitialContext}

Related tasks:
{tasks}

Audio transcript:
{EmptyText(transcript)}

Visual OCR:
{EmptyText(visualOcr)}

Visual AI scene analysis:
{EmptyText(visualAiContext)}

Repository context:
{EmptyText(repositoryContext)}
""";
    }

    private static async Task<RepositoryProcessingResult> TryBuildRepositoryContextAsync(VideoProcessingInput input, CancellationToken cancellationToken)
    {
        try
        {
            var repoRoot = DetectRepositoryRoot(input);
            if (repoRoot is null)
                return new RepositoryProcessingResult(string.Empty, "No local repository was detected for this project.");

            var sb = new StringBuilder();
            sb.AppendLine($"Repository root: {repoRoot}");
            sb.AppendLine();

            var status = await RunGitAsync(repoRoot, "status --short", cancellationToken);
            if (!string.IsNullOrWhiteSpace(status))
            {
                sb.AppendLine("Git status:");
                sb.AppendLine(status);
                sb.AppendLine();
            }

            var diffStat = await RunGitAsync(repoRoot, "diff --stat", cancellationToken);
            if (!string.IsNullOrWhiteSpace(diffStat))
            {
                sb.AppendLine("Git diff stat:");
                sb.AppendLine(diffStat);
                sb.AppendLine();
            }

            var diffNameOnly = await RunGitAsync(repoRoot, "diff --name-only", cancellationToken);
            var changedFiles = diffNameOnly
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(20)
                .ToList();

            if (changedFiles.Count == 0)
                changedFiles = FindRecentlyModifiedFiles(repoRoot, input.VideoPathOrUrl).Take(20).ToList();

            if (changedFiles.Count > 0)
            {
                sb.AppendLine("Relevant changed/recent files:");
                foreach (var file in changedFiles)
                    sb.AppendLine("- " + file);
                sb.AppendLine();
            }

            var diff = await RunGitAsync(repoRoot, "diff -- " + string.Join(' ', changedFiles.Select(QuoteGitPath)), cancellationToken);
            if (!string.IsNullOrWhiteSpace(diff))
            {
                sb.AppendLine("Relevant diff excerpt:");
                sb.AppendLine(TrimTo(diff, 14000));
                sb.AppendLine();
            }

            if (changedFiles.Count > 0)
            {
                sb.AppendLine("Current file excerpts:");
                foreach (var relative in changedFiles.Take(8))
                {
                    var fullPath = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > 120_000) continue;
                    var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    sb.AppendLine($"--- {relative} ---");
                    sb.AppendLine(TrimTo(text, 2500));
                    sb.AppendLine();
                }
            }

            var context = TrimTo(sb.ToString(), MaxRepositoryContextChars);
            return new RepositoryProcessingResult(context, string.IsNullOrWhiteSpace(context) ? "Repository found, but no useful context was extracted." : "Repository context extracted.");
        }
        catch (Exception ex)
        {
            return new RepositoryProcessingResult(string.Empty, "Repository context failed: " + ex.Message);
        }
    }

    private static string? DetectRepositoryRoot(VideoProcessingInput input)
    {
        var candidates = new List<string>();
        if (input.Project.Name.Contains("JTSProject", StringComparison.OrdinalIgnoreCase) ||
            input.Project.Name.Contains("JTS", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(@"C:\Dynamics\Repos\JTSProject");
        }

        candidates.Add(Environment.CurrentDirectory);
        candidates.Add(AppContext.BaseDirectory);

        foreach (var candidate in candidates)
        {
            var root = FindGitRoot(candidate);
            if (root is not null) return root;
        }

        return null;
    }

    private static string? FindGitRoot(string start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        var directory = Directory.Exists(start) ? new DirectoryInfo(start) : Directory.GetParent(start);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static IReadOnlyList<string> FindRecentlyModifiedFiles(string repoRoot, string videoPath)
    {
        var videoTime = File.Exists(videoPath) ? File.GetLastWriteTime(videoPath) : DateTime.Now;
        var ignored = new[] { $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" };
        return Directory.EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories)
            .Where(file => ignored.All(token => !file.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Where(file => IsSourceLike(file))
            .Select(file => new FileInfo(file))
            .Where(info => Math.Abs((info.LastWriteTime - videoTime).TotalHours) <= 12)
            .OrderByDescending(info => info.LastWriteTime)
            .Select(info => Path.GetRelativePath(repoRoot, info.FullName).Replace('\\', '/'))
            .ToList();
    }

    private static bool IsSourceLike(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".cs" or ".xaml" or ".json" or ".xml" or ".csproj" or ".ps1" or ".md" or ".ts" or ".js" or ".css" or ".scss";
    }

    private static async Task<string> RunGitAsync(string repoRoot, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "-C " + QuoteGitPath(repoRoot) + " " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        return process.ExitCode == 0 ? output.Trim() : error.Trim();
    }

    private static string QuoteGitPath(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    private static string TrimTo(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars] + "\n...[truncated]";

    private static IEnumerable<VideoFrameSample> SelectVisualAiFrames(IReadOnlyList<VideoFrameSample> frames, int maxFrames)
    {
        var valid = frames.Where(frame => !string.IsNullOrWhiteSpace(frame.ImageDataUrl)).ToList();
        if (valid.Count <= maxFrames) return valid;

        var selected = new List<VideoFrameSample>();
        for (var i = 0; i < maxFrames; i++)
        {
            var index = (int)Math.Round(i * (valid.Count - 1) / Math.Max(1d, maxFrames - 1));
            selected.Add(valid[index]);
        }

        return selected;
    }

    private static string NormalizeChatCompletionsEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var endpoint = value.Trim().TrimEnd('/');
        if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return endpoint;
        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return endpoint + "/chat/completions";
        return endpoint;
    }

    private static string ExtractContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind != JsonValueKind.Array) return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine(part.GetString());
                continue;
            }

            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                sb.AppendLine(text.GetString());
        }

        return sb.ToString().Trim();
    }

    private static async Task<string> RecognizeFrameRegionsAsync(BitmapDecoder decoder, OcrEngine ocrEngine, CancellationToken cancellationToken)
    {
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        var regions = new (string Name, BitmapBounds Bounds)[]
        {
            ("full", new BitmapBounds { X = 0, Y = 0, Width = width, Height = height }),
            ("editor", new BitmapBounds { X = (uint)(width * 0.10), Y = (uint)(height * 0.08), Width = (uint)(width * 0.78), Height = (uint)(height * 0.72) }),
            ("terminal", new BitmapBounds { X = (uint)(width * 0.05), Y = (uint)(height * 0.60), Width = (uint)(width * 0.90), Height = (uint)(height * 0.35) }),
            ("left-panel", new BitmapBounds { X = 0, Y = (uint)(height * 0.08), Width = (uint)(width * 0.30), Height = (uint)(height * 0.86) })
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var (name, bounds) in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { Bounds = ClampBounds(bounds, width, height) },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            var ocr = await ocrEngine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            var text = NormalizeOcrText(ocr.Text);
            if (string.IsNullOrWhiteSpace(text) || !seen.Add(text)) continue;
            sb.AppendLine($"<{name}>");
            sb.AppendLine(text);
        }

        return sb.ToString().Trim();
    }

    private static async Task<string> EncodeJpegDataUrlAsync(BitmapDecoder decoder, CancellationToken cancellationToken)
    {
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);

        var scale = decoder.PixelWidth > 1280
            ? 1280d / decoder.PixelWidth
            : 1d;
        encoder.BitmapTransform.ScaledWidth = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        encoder.BitmapTransform.ScaledHeight = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
        await encoder.FlushAsync().AsTask(cancellationToken);

        var bytes = new byte[(int)stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
        reader.ReadBytes(bytes);
        return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
    }

    private static BitmapBounds ClampBounds(BitmapBounds bounds, uint width, uint height)
    {
        var x = bounds.X >= width ? width - 1 : bounds.X;
        var y = bounds.Y >= height ? height - 1 : bounds.Y;
        var maxWidth = width - x;
        var maxHeight = height - y;
        return new BitmapBounds
        {
            X = x,
            Y = y,
            Width = Math.Max(1u, Math.Min(bounds.Width, maxWidth)),
            Height = Math.Max(1u, Math.Min(bounds.Height, maxHeight))
        };
    }

    private static string NormalizeOcrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", lines);
    }

    private static async Task<List<TimeSpan>> SelectKeyFrameTimesByVisualChangeAsync(
        MediaComposition composition,
        TimeSpan duration,
        int maxFrames,
        double scanFps,
        IProgress<VideoProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var scanTimes = BuildDenseScanTimes(duration, scanFps);
        if (scanTimes.Count == 0)
            return BuildSampleTimes(duration, maxFrames, 6);

        var candidates = new List<FrameChangeCandidate>();
        byte[]? previousSignature = null;
        var previousTime = TimeSpan.Zero;

        for (var i = 0; i < scanTimes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i % Math.Max(1, scanTimes.Count / 80) == 0)
            {
                var percent = 40 + (int)Math.Round(i / Math.Max(1d, scanTimes.Count) * 14d);
                Report(progress, percent, $"Scanning visual changes {i + 1}/{scanTimes.Count}...");
            }

            var time = scanTimes[i];
            var signature = await ComputeFrameSignatureAsync(composition, time, cancellationToken);
            if (previousSignature is null)
            {
                candidates.Add(new FrameChangeCandidate(time, 1d));
            }
            else
            {
                var difference = SignatureDifference(previousSignature, signature);
                if (difference >= 0.035 || (time - previousTime).TotalSeconds >= 8)
                    candidates.Add(new FrameChangeCandidate(time, difference));
            }

            previousSignature = signature;
            previousTime = time;
        }

        foreach (var anchor in BuildSampleTimes(duration, Math.Min(maxFrames, Math.Max(6, (int)Math.Ceiling(duration.TotalMinutes * 2))), 15))
            candidates.Add(new FrameChangeCandidate(anchor, 0.02));

        var selected = new List<FrameChangeCandidate>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (selected.Count >= maxFrames) break;
            if (selected.Any(existing => Math.Abs((existing.Timestamp - candidate.Timestamp).TotalSeconds) < 0.75)) continue;
            selected.Add(candidate);
        }

        var result = selected
            .OrderBy(candidate => candidate.Timestamp)
            .Select(candidate => candidate.Timestamp)
            .ToList();

        if (result.Count == 0)
            result.Add(TimeSpan.FromSeconds(Math.Min(1, Math.Max(0, duration.TotalSeconds / 2))));

        Report(progress, 54, $"Selected {result.Count} visual keyframe(s) from {scanTimes.Count} scanned frame(s).");
        return result;
    }

    private static List<TimeSpan> BuildDenseScanTimes(TimeSpan duration, double scanFps)
    {
        if (duration <= TimeSpan.Zero) return [];

        var safeFps = Math.Clamp(scanFps, 1, 30);
        var estimated = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * safeFps));
        var stepSeconds = 1d / safeFps;
        if (estimated > MaxDenseScanFrames)
            stepSeconds = duration.TotalSeconds / MaxDenseScanFrames;

        var samples = new List<TimeSpan>(Math.Min(estimated, MaxDenseScanFrames));
        for (var second = Math.Min(0.1, duration.TotalSeconds / 2); second < duration.TotalSeconds; second += stepSeconds)
            samples.Add(TimeSpan.FromSeconds(Math.Max(0, Math.Min(duration.TotalSeconds - 0.05, second))));

        return samples;
    }

    private static async Task<byte[]> ComputeFrameSignatureAsync(MediaComposition composition, TimeSpan sample, CancellationToken cancellationToken)
    {
        using var stream = await composition
            .GetThumbnailAsync(sample, 160, 90, VideoFramePrecision.NearestFrame)
            .AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = 32, ScaledHeight = 18 },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        var bytes = pixels.DetachPixelData();
        var signature = new byte[32 * 18];
        for (var i = 0; i < signature.Length; i++)
        {
            var offset = i * 4;
            var r = bytes[offset];
            var g = bytes[offset + 1];
            var b = bytes[offset + 2];
            signature[i] = (byte)((r * 30 + g * 59 + b * 11) / 100);
        }

        return signature;
    }

    private static double SignatureDifference(byte[] previous, byte[] current)
    {
        var length = Math.Min(previous.Length, current.Length);
        if (length == 0) return 1;

        var total = 0;
        for (var i = 0; i < length; i++)
            total += Math.Abs(previous[i] - current[i]);

        return total / (length * 255d);
    }

    private static List<TimeSpan> BuildSampleTimes(TimeSpan duration, int maxFrames, int intervalSeconds)
    {
        var count = Math.Min(maxFrames, Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds / Math.Max(1d, intervalSeconds))));
        if (count == 1) return [TimeSpan.FromSeconds(Math.Min(2, Math.Max(0, duration.TotalSeconds / 2)))];

        var samples = new List<TimeSpan>();
        for (var i = 0; i < count; i++)
        {
            var ratio = (i + 0.5) / count;
            samples.Add(TimeSpan.FromSeconds(Math.Max(0, Math.Min(duration.TotalSeconds - 0.1, duration.TotalSeconds * ratio))));
        }
        return samples;
    }

    private static string FormatTime(TimeSpan value) => $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    private static string EmptyText(string value) => string.IsNullOrWhiteSpace(value) ? "(no data detected)" : value.Trim();
    private async Task<int> GetIntSettingAsync(string key, int fallback, int min, int max)
    {
        var value = await _settings.GetAsync(key);
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private async Task<double> GetDoubleSettingAsync(string key, double fallback, double min, double max)
    {
        var value = await _settings.GetAsync(key);
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }
    private static string DocumentationLanguageInstruction(string language) =>
        string.Equals(language, "Español", StringComparison.OrdinalIgnoreCase)
            ? "Write the final documentation in Spanish."
            : "Write the final documentation in English.";

    private static string FirstParagraph(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Video processed.";
        var paragraph = value.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(paragraph) ? value.Trim() : paragraph.Trim();
    }

    private static void Report(IProgress<VideoProcessingProgress>? progress, int percent, string message) =>
        progress?.Report(new VideoProcessingProgress(Math.Clamp(percent, 0, 100), message));
}

public sealed record VideoProcessingInput(
    Guid VideoAnalysisId,
    string VideoPathOrUrl,
    Project Project,
    IReadOnlyList<TaskItem> Tasks,
    string InitialContext,
    string DocumentationLanguage);

public sealed record VideoProcessingOutput(
    string Transcript,
    string VisualOcr,
    string GlobalSummary,
    string GlobalDocumentation,
    string ResultJson,
    IReadOnlyDictionary<Guid, string> TaskDocumentation);

public sealed record DocumentationProcessingResult(
    string GlobalSummary,
    string GlobalDocumentation,
    IReadOnlyDictionary<Guid, string> TaskDocumentation);

public sealed record VideoProcessingProgress(int Percent, string Message);

internal sealed record AudioProcessingResult(string Transcript, string Diagnostics);
internal sealed record VisualProcessingResult(string OcrText, string Diagnostics, IReadOnlyList<VideoFrameSample> Frames);
internal sealed record VideoFrameSample(TimeSpan Timestamp, string OcrText, string ImageDataUrl);
internal sealed record VisualAiProcessingResult(string SceneDescriptions, string Diagnostics);
internal sealed record RepositoryProcessingResult(string Context, string Diagnostics);
internal sealed record FrameChangeCandidate(TimeSpan Timestamp, double Score);
