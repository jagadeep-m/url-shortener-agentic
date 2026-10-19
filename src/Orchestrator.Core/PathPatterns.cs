using System.Text.RegularExpressions;

namespace Orchestrator.Core;

// Agent path roots such as "src/UrlShortener.*" or "docs": '*' matches within one segment.
public static class PathPatterns
{
    public static bool IsUnder(string relativePath, string root) =>
        Regex.IsMatch(relativePath, "\\A" + Regex.Escape(root).Replace("\\*", "[^/]*") + "/.+\\z", RegexOptions.CultureInvariant);

    public static bool IsUnderAny(string relativePath, IEnumerable<string> roots) => roots.Any(root => IsUnder(relativePath, root));
}
