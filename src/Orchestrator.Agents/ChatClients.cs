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
        : this(apiKey, model, endpoint, new HttpClientHandler { AllowAutoRedirect = false }) { }

    // The handler is injectable so tests can stand in for the provider without network access.
    public OpenAiCompatibleChatClient(string apiKey, string model, string endpoint, HttpMessageHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.apiKey = apiKey;
        this.model = model;
        this.endpoint = new Uri(endpoint, UriKind.Absolute);
        if (this.endpoint.Scheme != "https") throw new ArgumentException("LLM endpoint must use HTTPS.");
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
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
            response_format = new { type = "json_object" },
            store = false
        });
        using var response = await http.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Explain((int)response.StatusCode, ProviderMessage(raw)));
        using var body = JsonDocument.Parse(raw);
        var choice = body.RootElement.GetProperty("choices")[0];
        var finish = choice.TryGetProperty("finish_reason", out var reason) ? reason.GetString() : "stop";
        if (finish == "length")
            throw new InvalidDataException($"The model's answer was cut off at the {options?.MaxOutputTokens ?? 4096}-token output limit (finish_reason=length).");
        if (finish is not ("stop" or null)) throw new InvalidDataException($"The model did not complete its answer (finish_reason={finish}).");
        var text = choice.GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The model returned no content.");
        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        { ModelId = body.RootElement.TryGetProperty("model", out var id) ? id.GetString() : model };
        if (body.RootElement.TryGetProperty("usage", out var usage))
            result.Usage = new UsageDetails { InputTokenCount = Count(usage, "prompt_tokens"), OutputTokenCount = Count(usage, "completion_tokens"), TotalTokenCount = Count(usage, "total_tokens") };
        return result;
    }

    // Actionable messages for the common setup mistakes; the provider's own explanation is appended when present.
    private string Explain(int status, string? detail)
    {
        var hint = status switch
        {
            401 => "The API key was rejected. Check LLM:ApiKey.",
            403 => "The API key may not use this model or endpoint.",
            404 => $"Model '{model}' or the endpoint was not found. Check LLM:Model and LLM:Endpoint.",
            429 => "Rate limited or out of credit. Check your provider's billing and limits, then resume the run.",
            >= 500 => "The provider had a server error; the stage will retry.",
            _ => "The request was rejected."
        };
        return $"LLM request failed with HTTP {status}. {hint}" + (detail is null ? "" : $" Provider says: {detail}");
    }

    private static string? ProviderMessage(string raw)
    {
        try
        {
            using var error = JsonDocument.Parse(raw);
            var message = error.RootElement.GetProperty("error").GetProperty("message").GetString();
            return message is null ? null : message.Length > 300 ? message[..300] + "..." : message;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static long? Count(JsonElement usage, string name) => usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) ? count : null;
    public override void Dispose() => http.Dispose();
}
