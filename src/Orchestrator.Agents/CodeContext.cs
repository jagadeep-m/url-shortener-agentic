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
    // docs/ is readable too (the Docs agent writes there), but only when a file is asked for by name, for example
    // the Reviewer's list of files the run changed; it is never part of the outline or the whole-source view.
    private IEnumerable<string> ReadableRoots => productRoots.Append("docs");

    [GeneratedRegex(@"(?:src|tests)/UrlShortener\.[A-Za-z]+/[A-Za-z0-9_./-]+\.(?:cs|csproj|json)")]
    private static partial Regex ProductPath();

    public string Outline() => Redact(CodebaseIndex.Build(root, productRoots).Outline());

    // Files are chosen in priority order: role defaults, then files named by path, then files declaring a type the
    // texts mention by name (a design that says "change LinkService" names no path), then, when asked, the rest of
    // the product source (generated migrations excluded) until the budget is used. Within each group, alphabetical.
    // A live design once named no existing code at all, and the Coder could not wire its new class in.
    public IReadOnlyList<FileSnapshot> RelevantFiles(IEnumerable<string> texts, IEnumerable<string>? alwaysInclude = null, bool includeProductSource = false)
    {
        var all = string.Join("\n", texts.Select(t => t ?? ""));
        var index = CodebaseIndex.Build(root, productRoots);
        var byPath = ProductPath().Matches(all).Select(m => m.Value.TrimEnd('.')).Order(StringComparer.Ordinal);
        var byType = index.DeclaringFiles
            .Where(type => Regex.IsMatch(all, $@"\b{Regex.Escape(type.Key)}\b"))
            .SelectMany(type => type.Value).Order(StringComparer.Ordinal);
        var rest = includeProductSource
            ? index.FilePaths.Where(p => p.StartsWith("src/", StringComparison.Ordinal) && !p.Contains("/Migrations/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        var named = (alwaysInclude ?? []).Concat(byPath).Concat(byType).Concat(rest).Distinct(StringComparer.Ordinal);
        var result = new List<FileSnapshot>();
        var total = 0;
        foreach (var path in named)
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

    // Only normalised product or docs paths: no traversal, no build output.
    private bool IsReadable(string path) =>
        !path.Split('/').Any(s => s is ".." or "." or "" or "bin" or "obj") && PathPatterns.IsUnderAny(path, ReadableRoots);
}
