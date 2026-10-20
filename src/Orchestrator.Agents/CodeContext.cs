using System.Text.RegularExpressions;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public sealed record FileSnapshot(string Path, string Content);

// Read-only view of the product code for agents. A real model cannot change code it has never seen, so agents
// receive an outline and the current contents of the files the run's own artifacts name (for example the impact
// report). Only files under the product roots are read, sizes are capped, and the machine's absolute path is
// redacted from everything sent to the model. Writing still goes only through FileChangeService.
public sealed partial class CodeContext(string workspace, IReadOnlyList<string>? roots = null, int maxFileChars = 24_000, int maxTotalChars = 120_000)
{
    private readonly string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar);
    private readonly IReadOnlyList<string> productRoots = roots ?? ["src/UrlShortener.*", "tests/UrlShortener.*"];

    [GeneratedRegex(@"(?:src|tests)/UrlShortener\.[A-Za-z]+/[A-Za-z0-9_./-]+\.(?:cs|csproj|json)")]
    private static partial Regex ProductPath();

    public string Outline() => Redact(CodebaseIndex.Build(root, productRoots).Outline());

    public IReadOnlyList<FileSnapshot> RelevantFiles(IEnumerable<string> texts, IEnumerable<string>? alwaysInclude = null)
    {
        var named = texts.SelectMany(t => ProductPath().Matches(t ?? "").Select(m => m.Value.TrimEnd('.')))
            .Concat(alwaysInclude ?? []).Distinct(StringComparer.Ordinal);
        var result = new List<FileSnapshot>();
        var total = 0;
        foreach (var path in named.Order(StringComparer.Ordinal))
        {
            if (!IsReadable(path)) continue;
            var full = Path.Combine(root, path);
            if (!File.Exists(full)) continue; // a new file the design proposes: nothing to read yet
            var content = File.ReadAllText(full);
            if (content.Length > maxFileChars) { result.Add(new(path, $"(omitted: {content.Length} characters exceeds the {maxFileChars}-character limit)")); continue; }
            if (total + content.Length > maxTotalChars) { result.Add(new(path, "(omitted: context budget reached)")); continue; }
            total += content.Length;
            result.Add(new(path, Redact(content)));
        }
        return result;
    }

    public string Redact(string text) => text.Replace(root, "<workspace>", StringComparison.Ordinal);

    // Only normalised product paths: no traversal, no build output.
    private bool IsReadable(string path) =>
        !path.Split('/').Any(s => s is ".." or "." or "" or "bin" or "obj") && PathPatterns.IsUnderAny(path, productRoots);
}
