using Orchestrator.Core;

namespace Orchestrator.Policies;

public enum VulnerabilityStatus { Clean, Vulnerable, Unknown }
public sealed record VulnerabilityReport(VulnerabilityStatus Status, string Detail);

public interface IVulnerabilityScanner
{
    VulnerabilityReport Scan(IReadOnlyList<string> packages);
}

// Proposed packages are checked for known vulnerabilities before they land. When the check cannot
// run (offline, feed error), a human decides instead of the change passing silently.
public sealed class VulnerablePackagePolicy(IVulnerabilityScanner scanner) : Policy
{
    public override string Name => "VulnerablePackages";

    public override PolicyResult Check(ProposedChange change, RunContext ctx)
    {
        if (change.NewPackages.Count == 0) return Allow("no new packages to scan");
        var report = scanner.Scan(change.NewPackages);
        return report.Status switch
        {
            VulnerabilityStatus.Clean => Allow("no known vulnerabilities: " + report.Detail),
            VulnerabilityStatus.Vulnerable => Deny("vulnerable packages: " + report.Detail),
            _ => Approve("vulnerability check unavailable: " + report.Detail)
        };
    }
}
