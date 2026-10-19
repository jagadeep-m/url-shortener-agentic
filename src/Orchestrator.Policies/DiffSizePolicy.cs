using Orchestrator.Core;

namespace Orchestrator.Policies;

// Large diffs are hard to review: above the approval limit a human must look, above the hard limit
// the change is denied and should be split.
public sealed class DiffSizePolicy(int approvalLimit = 400, int hardLimit = 2000) : Policy
{
    public override string Name => "DiffSize";

    public override PolicyResult Check(ProposedChange change, RunContext ctx)
    {
        var lines = UnifiedDiff.ChangedLineCount(change.Diff);
        if (lines > hardLimit) return Deny($"{lines} changed lines exceed the hard limit of {hardLimit}; split the change");
        if (lines > approvalLimit) return Approve($"{lines} changed lines exceed {approvalLimit}");
        return Allow($"{lines} changed line(s)");
    }
}
