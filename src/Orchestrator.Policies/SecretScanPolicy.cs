using System.Text.RegularExpressions;
using Orchestrator.Core;

namespace Orchestrator.Policies;

// Scans added lines only. High-confidence credential formats are denied; a quoted value assigned
// to a secret-looking name needs approval, because it may be a test fixture. Values are never logged.
public sealed class SecretScanPolicy : Policy
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly (string Name, Regex Pattern)[] Definite =
    [
        ("private key", new(@"-----BEGIN ([A-Z]+ )?PRIVATE KEY-----", Options)),
        ("AWS access key", new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", Options)),
        ("API key (sk-)", new(@"\bsk-(ant-)?[A-Za-z0-9_-]{20,}", Options)),
        ("GitHub token", new(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", Options)),
        ("Slack token", new(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", Options)),
        // Needs another key=value; pair first, so C# like `password = GetPassword();` is not matched.
        ("connection string password", new(@"(?i)\b[\w ]+=[^;""'=]*;\s*(password|pwd)\s*=\s*[^;""'\s{$]+", Options)),
    ];
    private static readonly Regex Suspicious = new(@"(?i)\b[\w.-]*(api[_-]?key|secret|token|password|passwd)[\w.-]*[""']?\s*[:=]\s*[""'][^""'\s]{8,}[""']", Options);

    public override string Name => "SecretScan";

    public override PolicyResult Check(ProposedChange change, RunContext ctx)
    {
        var added = UnifiedDiff.AddedLines(change.Diff).ToArray();
        var definite = added.SelectMany(l => Definite.Where(d => d.Pattern.IsMatch(l.Text)).Select(d => $"{d.Name} at {l.Path}:{l.LineNumber}")).ToArray();
        if (definite.Length > 0) return Deny("credential found: " + string.Join(", ", definite.Take(5)));
        var suspicious = added.Where(l => Suspicious.IsMatch(l.Text)).Select(l => $"{l.Path}:{l.LineNumber}").ToArray();
        if (suspicious.Length > 0) return Approve("possible hard-coded secret at " + string.Join(", ", suspicious.Take(5)));
        return Allow($"no secrets in {added.Length} added line(s)");
    }
}
