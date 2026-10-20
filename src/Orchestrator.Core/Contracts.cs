namespace Orchestrator.Core;

public enum RiskLevel { Low, Medium, High }
public enum PolicyVerdict { Allow, RequireApproval, Deny }
public enum StageStatus { Pending, Running, AwaitingApproval, Completed, Blocked, Failed, Interrupted, Stale }
public enum RunStatus { Pending, Running, Completed, Blocked, Failed, Paused }

public interface IStage
{
    string Id { get; }
    IReadOnlyList<string> DependsOn { get; }
    RiskLevel Risk { get; }
    Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct);
    Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct);
    Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct);
}

public interface IAgent
{
    string Role { get; }
    AutonomyBoundary Boundary { get; }
    Task<AgentResult> RunAsync(AgentTask task, RunContext ctx, CancellationToken ct);
}

public interface IApprovalService
{
    Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct);
}

public interface IPolicy
{
    string Name { get; }
    PolicyVerdict Evaluate(ProposedChange change, RunContext ctx);
}

public sealed record Decision(string Id, string StageId, string Summary, string Rationale,
    IReadOnlyList<string> ParentDecisionIds, string MadeBy, DateTime AtUtc);
public sealed record GateResult(bool Passed, string Reason)
{
    public static GateResult Pass(string reason = "Gate passed.") => new(true, reason);
    public static GateResult Block(string reason) => new(false, reason);
}
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record StageOutput(Artifact Artifact, IReadOnlyList<Decision> Decisions)
{
    public StageOutput(Artifact artifact) : this(artifact, []) { }
}
public sealed record AutonomyBoundary(IReadOnlyList<string> AllowedPaths, IReadOnlyList<string> AllowedTools, long MaxTokens);
public sealed record AgentTask(string StageId, string Prompt, IReadOnlyList<Artifact> Inputs, int Attempt = 1, bool Simplified = false);
public sealed record AgentResult(bool Success, StageOutput? Output, string? Error = null);
public sealed record ApprovalRequest(Guid RunId, string StageId, RiskLevel Risk, string Reason, string? ArtifactPath = null);
public sealed record ApprovalResult(bool Approved, string Approver, string Reason);
public sealed record ProposedChange(IReadOnlyList<string> Paths, string Diff, IReadOnlyList<string> NewPackages, bool IncludesMigration);

public interface IArtifactReviewStage : IStage
{
    string GetArtifactPath(RunContext context);
    Task<StageOutput> ReadReviewedOutputAsync(RunContext context, StageOutput output, CancellationToken ct);
}

public enum FallbackLevel { SimplerPrompt, Template }

// A stage that can still produce output after its retries are exhausted.
public interface IFallbackStage : IStage
{
    Task<StageOutput> ExecuteFallbackAsync(RunContext ctx, FallbackLevel level, CancellationToken ct);
}

// Branch-per-run versioning of the files agents may change.
public interface IWorkspaceVersioning
{
    Task BeginAsync(RunContext ctx, CancellationToken ct);
    Task<string> CommitStageAsync(RunContext ctx, string stageId, string? decisionId, CancellationToken ct);
    Task<RevertResult> RevertToLastGoodAsync(RunContext ctx, string stageId, CancellationToken ct);
    // Unified diff of everything the run committed since it started (written as diff.patch).
    Task<string> DiffSinceStartAsync(RunContext ctx, CancellationToken ct);
}

public sealed record RevertResult(string Commit, IReadOnlyList<string> Files);

// Thrown when a run's token or time budget is used up; the engine pauses the run instead of retrying.
public sealed class BudgetExceededException(string message) : Exception(message);

public sealed record PolicyResult(PolicyVerdict Verdict, string Reason);

// A policy that explains its verdict for the audit log and the approval prompt.
public interface IExplainablePolicy : IPolicy
{
    PolicyResult Check(ProposedChange change, RunContext ctx);
}

// A change that a policy denied or a human rejected. The stage stops instead of retrying.
public sealed class PolicyViolationException(string message, bool rejectedByHuman = false) : InvalidOperationException(message)
{
    public bool RejectedByHuman { get; } = rejectedByHuman;
}

// The stage cannot continue until a human supplies input (for example unanswered open questions).
// The stage is blocked, its draft is kept, and an explicit resume continues it.
public sealed class HumanInputRequiredException(string message) : Exception(message);

// A reviewing stage found that the work it depends on does not meet the requirements. The engine re-runs that work
// with these findings as feedback (bounded), then reviews again.
public sealed class ReworkRequestedException(string findings) : Exception(findings)
{
    public string Findings { get; } = findings;
}
