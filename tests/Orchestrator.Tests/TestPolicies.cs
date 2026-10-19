using Orchestrator.Core;
using Orchestrator.Policies;

namespace Orchestrator.Tests;

// Test double: isolates role boundaries and engine behaviour from the real policy set.
internal sealed class AllowAllPolicy : IPolicy
{
    public string Name => "AllowAll";
    public PolicyVerdict Evaluate(ProposedChange change, RunContext ctx) => PolicyVerdict.Allow;
}

internal sealed class FakeScanner(VulnerabilityStatus status = VulnerabilityStatus.Clean, string detail = "fake") : IVulnerabilityScanner
{
    public int Calls;
    public VulnerabilityReport Scan(IReadOnlyList<string> packages) { Calls++; return new(status, detail); }
}
