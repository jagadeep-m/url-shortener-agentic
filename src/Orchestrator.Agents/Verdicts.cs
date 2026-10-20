using System.Text.RegularExpressions;

namespace Orchestrator.Agents;

// Structured decisions inside agent artifacts, so the orchestrator can act on them instead of only showing them.
public static partial class Verdicts
{
    private const int MaxFindings = 2000;

    [GeneratedRegex(@"^[\s>*_#-]*verdict[\s*_]*:[\s*_]*(pass|fail)\b[*_]*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ReviewLine();

    [GeneratedRegex(@"^[\s>*_#-]*recommendation[\s*_]*:[\s*_]*(not ready|ready)\b[*_]*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ReleaseLine();

    // The Reviewer must end with "Verdict: PASS" or "Verdict: FAIL"; the text after a FAIL is the rework feedback.
    public static (bool Passed, string Findings) Review(string content)
    {
        var match = ReviewLine().Matches(content).LastOrDefault()
            ?? throw new InvalidDataException("The review must include a line 'Verdict: PASS' or 'Verdict: FAIL' (with the findings after a FAIL).");
        var passed = match.Groups[1].Value.Equals("pass", StringComparison.OrdinalIgnoreCase);
        var after = content[(match.Index + match.Length)..].Trim();
        var findings = after.Length > 0 ? after : content.Trim();
        return (passed, findings.Length > MaxFindings ? findings[..MaxFindings] + "..." : findings);
    }

    // The Release agent must state "Recommendation: READY" or "Recommendation: NOT READY".
    public static bool Ready(string content)
    {
        var match = ReleaseLine().Match(content);
        if (!match.Success) throw new InvalidDataException("The release assessment must include a line 'Recommendation: READY' or 'Recommendation: NOT READY'.");
        return match.Groups[1].Value.Equals("ready", StringComparison.OrdinalIgnoreCase);
    }
}
