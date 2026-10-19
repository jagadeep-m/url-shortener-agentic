using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Orchestrator.Agents;

public abstract class BufferedChatClient : IChatClient
{
    public abstract Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default);
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public virtual void Dispose() { }
}

public sealed class ReplayChatClient : BufferedChatClient
{
    private readonly string directory;
    public ReplayChatClient(string recordingsRoot, string scenario)
    {
        ValidateSegment(scenario);
        directory = Path.Combine(Path.GetFullPath(recordingsRoot), scenario);
    }
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options?.AdditionalProperties is null || !options.AdditionalProperties.TryGetValue("stageId", out var value) || value is not string stage)
            throw new ArgumentException("Replay requires a stageId in ChatOptions.AdditionalProperties.");
        ValidateSegment(stage);
        var path = Candidates(stage, options.AdditionalProperties).Select(name => Path.Combine(directory, name + ".json")).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Missing replay recording for '{stage}'.", Path.Combine(directory, stage + ".json"));
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var input = string.Join("\n", messages.Select(m => m.Text));
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, Select(document.RootElement, input, path).GetRawText())) { ModelId = "replay" };
    }
    // A recording may hold {"variants":[{"whenInputContains":["..."],"response":{...}}],"default":{...}}: the first
    // variant whose strings all appear in the prompt wins. This lets replay follow human answers deterministically.
    private static JsonElement Select(JsonElement root, string input, string path)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("variants", out var variants)) return root;
        foreach (var variant in variants.EnumerateArray())
            if (variant.GetProperty("whenInputContains").EnumerateArray().All(s => input.Contains(s.GetString()!, StringComparison.Ordinal)))
                return variant.GetProperty("response");
        return root.TryGetProperty("default", out var fallback) ? fallback : throw new InvalidDataException($"No replay variant in {Path.GetFileName(path)} matches the prompt.");
    }

    // Most specific first: <stage>.<variant>.json (fallback prompt), <stage>.attempt<N>.json (scripted failure), <stage>.json.
    private static IEnumerable<string> Candidates(string stage, AdditionalPropertiesDictionary properties)
    {
        if (properties.TryGetValue("variant", out var value) && value is string variant)
        {
            ValidateSegment(variant);
            yield return $"{stage}.{variant}";
        }
        if (properties.TryGetValue("attempt", out var attempt) && attempt is not null)
            yield return $"{stage}.attempt{Convert.ToInt32(attempt, System.Globalization.CultureInfo.InvariantCulture)}";
        yield return stage;
    }
    private static void ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Recording names must contain only ASCII letters, digits, underscores and hyphens.");
    }
}

// A deliberately small text/JSON adapter. Tools and provider-specific features are not exposed to agents.
public sealed class OpenAiCompatibleChatClient : BufferedChatClient
{
    private readonly HttpClient http;
    private readonly string apiKey;
    private readonly string model;
    private readonly Uri endpoint;
    public OpenAiCompatibleChatClient(string apiKey, string model, string endpoint = "https://api.openai.com/v1/chat/completions")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.apiKey = apiKey;
        this.model = model;
        this.endpoint = new Uri(endpoint, UriKind.Absolute);
        if (this.endpoint.Scheme != "https") throw new ArgumentException("LLM endpoint must use HTTPS.");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    }
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = options?.ModelId ?? model,
            messages = messages.Select(m => new { role = m.Role.Value, content = m.Text }),
            max_completion_tokens = options?.MaxOutputTokens ?? 4096,
            response_format = new { type = "json_object" }, store = false
        });
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"LLM request failed with HTTP {(int)response.StatusCode}.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var choice = body.RootElement.GetProperty("choices")[0];
        if (choice.GetProperty("finish_reason").GetString() != "stop") throw new InvalidDataException("LLM output was incomplete or refused.");
        var text = choice.GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("LLM returned no JSON content.");
        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = model };
        if (body.RootElement.TryGetProperty("usage", out var usage))
            result.Usage = new UsageDetails { InputTokenCount = usage.GetProperty("prompt_tokens").GetInt64(), OutputTokenCount = usage.GetProperty("completion_tokens").GetInt64(), TotalTokenCount = usage.GetProperty("total_tokens").GetInt64() };
        return result;
    }
    public override void Dispose() => http.Dispose();
}
