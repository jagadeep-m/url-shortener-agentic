using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Agents;

public sealed record FileChange(string Path, string Content);
public sealed record AgentDocument(string Summary, string Rationale, string Content, IReadOnlyList<FileChange> Files)
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    // Models sometimes wrap JSON in a markdown fence (```json ... ```); the fence is removed before parsing.
    public static string Unwrap(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return text;
        var start = trimmed.IndexOf('\n');
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return start < 0 || end <= start ? text : trimmed[(start + 1)..end].Trim();
    }

    public static AgentDocument Parse(string json)
    {
        var result = JsonSerializer.Deserialize<AgentDocument>(json, Json) ?? throw new InvalidDataException("Agent JSON is null.");
        if (string.IsNullOrWhiteSpace(result.Summary) || string.IsNullOrWhiteSpace(result.Rationale)
            || string.IsNullOrWhiteSpace(result.Content) || result.Files is null
            || result.Files.Any(f => f is null || string.IsNullOrWhiteSpace(f.Path) || f.Content is null))
            throw new InvalidDataException("Agent JSON requires summary, rationale, content and a files array with path/content entries.");
        return result;
    }
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
