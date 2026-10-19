using Orchestrator.Core;

namespace Orchestrator.Policies;

public abstract class Policy : IExplainablePolicy
{
    public abstract string Name { get; }
    public abstract PolicyResult Check(ProposedChange change, RunContext ctx);
    public PolicyVerdict Evaluate(ProposedChange change, RunContext ctx) => Check(change, ctx).Verdict;
    protected static PolicyResult Allow(string reason) => new(PolicyVerdict.Allow, reason);
    protected static PolicyResult Approve(string reason) => new(PolicyVerdict.RequireApproval, reason);
    protected static PolicyResult Deny(string reason) => new(PolicyVerdict.Deny, reason);
}
