using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Agents;

public sealed record FileChange(string Path, string Content);
public sealed record AgentDocument(string Summary, string Rationale, string Content, IReadOnlyList<FileChange> Files)
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
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
        // A missing files key means no file changes (a live reworked Docs stage left it out when its document was already right);
        // roles that must change code still fail on an empty list (SdlcAgent.RequiresFiles).
        if (result.Files is null) result = result with { Files = [] };
        // Name exactly what is missing: the message is the retry's feedback, and a generic one cost live Docs runs two attempts.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(result.Summary)) missing.Add("summary");
        if (string.IsNullOrWhiteSpace(result.Rationale)) missing.Add("rationale");
        if (string.IsNullOrWhiteSpace(result.Content)) missing.Add("content (a non-empty markdown artifact, even when the work is in files)");
        if (result.Files.Any(f => f is null || string.IsNullOrWhiteSpace(f.Path) || f.Content is null)) missing.Add("a path and content for every files entry");
        if (missing.Count > 0)
            throw new InvalidDataException("Agent JSON requires summary, rationale, content and a files array with path/content entries. Missing or empty: "
                + string.Join("; ", missing) + ".");
        return result;
    }
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
