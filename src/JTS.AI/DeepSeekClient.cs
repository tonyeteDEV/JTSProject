using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JTS.AI;

public sealed class DeepSeekClient
{
    public const string DefaultModel = "deepseek-v4-flash";
    private readonly HttpClient _httpClient;

    public DeepSeekClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> ChatAsync(
        string apiKey,
        IReadOnlyList<DeepSeekMessage> messages,
        DeepSeekRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DeepSeekRequestOptions.Default;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(CreatePayload(messages, options, stream: false));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string apiKey,
        IReadOnlyList<DeepSeekMessage> messages,
        DeepSeekRequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= DeepSeekRequestOptions.Default;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(CreatePayload(messages, options, stream: true));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") yield break;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var choice = doc.RootElement.GetProperty("choices")[0];
                if (choice.TryGetProperty("delta", out var deltaElement) &&
                    deltaElement.TryGetProperty("content", out var contentElement) &&
                    contentElement.ValueKind == JsonValueKind.String)
                {
                    delta = contentElement.GetString();
                }
            }
            catch
            {
                delta = null;
            }

            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
    }

    private static Dictionary<string, object> CreatePayload(
        IReadOnlyList<DeepSeekMessage> messages,
        DeepSeekRequestOptions options,
        bool stream)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = string.IsNullOrWhiteSpace(options.Model) ? DefaultModel : options.Model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["thinking"] = new { type = options.ThinkingEnabled ? "enabled" : "disabled" },
            ["stream"] = stream
        };

        if (options.ThinkingEnabled)
            payload["reasoning_effort"] = options.ReasoningEffort;

        return payload;
    }
}

public sealed record DeepSeekRequestOptions(
    string Model,
    bool ThinkingEnabled,
    string ReasoningEffort = "high")
{
    public static DeepSeekRequestOptions Default { get; } = new(DeepSeekClient.DefaultModel, ThinkingEnabled: false);
}

public sealed record DeepSeekMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);
