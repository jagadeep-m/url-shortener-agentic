using System.Text.RegularExpressions;
using Orchestrator.Core;

namespace Orchestrator.Policies;

// Agents may write only under the allowed roots. Secret-bearing files are denied everywhere;
// build and configuration files inside the roots need a human to look at them.
public sealed class PathAllowlistPolicy(IReadOnlyList<string> allowedRoots) : Policy
{
    private static readonly Regex SecretFile = new(@"(^|/)(\.env(\..*)?|secrets\.json|appsettings\.[^/]*\.local\.json|[^/]*\.(pfx|p12|pem|key|snk))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BuildConfig = new(@"(^|/)([^/]*\.(csproj|props|targets|sln)|appsettings[^/]*\.json|launchSettings\.json|global\.json|nuget\.config)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public override string Name => "PathAllowlist";

    public override PolicyResult Check(ProposedChange change, RunContext ctx)
    {
        var outside = change.Paths.Where(p => !PathPatterns.IsUnderAny(p, allowedRoots)).ToArray();
        if (outside.Length > 0) return Deny($"outside the allowed roots ({string.Join(", ", allowedRoots)}): {string.Join(", ", outside)}");
        var secrets = change.Paths.Where(p => SecretFile.IsMatch(p)).ToArray();
        if (secrets.Length > 0) return Deny($"secret-bearing file types are never written by agents: {string.Join(", ", secrets)}");
        var config = change.Paths.Where(p => BuildConfig.IsMatch(p)).ToArray();
        if (config.Length > 0) return Approve($"build/configuration files changed: {string.Join(", ", config)}");
        return Allow($"{change.Paths.Count} path(s) inside allowed roots");
    }
}
