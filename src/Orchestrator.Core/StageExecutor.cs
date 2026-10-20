using Polly;
using Polly.Retry;

namespace Orchestrator.Core;

internal enum StageOutcome { Completed, Blocked, Failed, Rework }

internal sealed class StageExecutionException(string message, Exception? inner = null) : Exception(message, inner);
internal sealed class AttemptFailedException(string message) : Exception(message);
internal sealed class StageBlockedException(string message) : Exception(message);

// Runs one stage: entry gate, approval, bounded retries, fallback chain, then exit gate and commit.
internal sealed class StageExecutor(RunSession session, IApprovalService approvals)
{
    // A failed review sends upstream work back at most this many times before the run stops as Failed.
    // Two cycles: live reviews found real gaps a few at a time, and one cycle left a real, late finding unfixed.
    internal const int MaxReworks = 2;
    // A stage that writes outside its role's folders gets this many retries with the denial as feedback.
    internal const int MaxBoundaryCorrections = 1;

    private RunContext Context => session.Context;

    public async Task<StageOutcome> RunAsync(StageDefinition definition, IStage stage, CancellationToken ct)
    {
        var state = Context.StageStatuses[definition.Id];
        await session.UpdateAsync(_ => { state.Reason = null; state.FinishedAtUtc = null; state.Approved = false; state.Approver = null; }, save: false);
        try
        {
            await EnterAsync(definition, stage, ct);
            try
            {
                await RetryPipeline(definition).ExecuteAsync(token => new ValueTask(AttemptAsync(definition, stage, null, token)), ct);
            }
            catch (AttemptFailedException failure)
            {
                if (!await TryFallbackAsync(definition, stage, ct))
                {
                    return await StopAsync(definition, StageStatus.Failed, state.LastFailure ?? failure.Message);
                }
            }
            return StageOutcome.Completed;
        }
        catch (StageBlockedException blocked) { return await StopAsync(definition, StageStatus.Blocked, blocked.Message); }
        catch (StageExecutionException failure) { return await StopAsync(definition, StageStatus.Failed, failure.Message); }
        catch (ReworkRequestedException rework)
        {
            if (state.Reworks >= MaxReworks)
                return await StopAsync(definition, StageStatus.Failed, $"The review still failed after {state.Reworks} rework(s): {rework.Findings}");
            return StageOutcome.Rework;   // the engine marks the reviewed stages stale and re-runs them
        }
    }

    private async Task EnterAsync(StageDefinition definition, IStage stage, CancellationToken ct)
    {
        var missing = definition.DependsOn.FirstOrDefault(id => Context.StageStatuses[id].Status != StageStatus.Completed || !Context.Artifacts.ContainsKey(id));
        var entry = missing is null ? await InvokeAsync(() => stage.CheckEntryAsync(Context, ct), ct)
            : GateResult.Block($"Dependency '{missing}' is incomplete or has no approved artifact.");
        await session.Audit.WriteAsync(Context.RunId, "entry_gate", definition, verdict: entry.Passed ? "pass" : "block", message: entry.Reason, ct: ct);
        if (!entry.Passed) throw new StageBlockedException(entry.Reason);
        if (definition.Risk == RiskLevel.High && stage is not IArtifactReviewStage)
            await ApproveAsync(definition, "High-risk stage requires human approval.", null, ct);
    }

    private async Task AttemptAsync(StageDefinition definition, IStage stage, FallbackLevel? level, CancellationToken ct)
    {
        var state = Context.StageStatuses[definition.Id];
        await session.UpdateAsync(_ => { state.Status = StageStatus.Running; state.Attempts++; state.FallbackLevel = level; state.StartedAtUtc = session.Now; });
        await session.Audit.WriteAsync(Context.RunId, "stage_started", definition, message: level is null ? null : $"Fallback: {level}", ct: ct);
        var tags = Tags(definition);
        OrchestratorTelemetry.StageAttempts.Add(1, tags);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var outcome = "failed";
        try
        {
            var output = await InvokeAsync(() => level is { } fallback
                ? ((IFallbackStage)stage).ExecuteFallbackAsync(Context, fallback, ct) : stage.ExecuteAsync(Context, ct), ct);
            ValidateOutput(definition, output);
            await session.Audit.WriteAsync(Context.RunId, "stage_executed", definition, output.Artifact.ContentHash, ct: ct);
            if (definition.Risk == RiskLevel.High && stage is IArtifactReviewStage review)
            {
                await ApproveAsync(definition, "Review the generated artifact before completing this stage.", review.GetArtifactPath(Context), ct);
                output = await InvokeAsync(() => review.ReadReviewedOutputAsync(Context, output, ct), ct);
                ValidateOutput(definition, output);
            }
            await LandAsync(definition, stage, output, ct);
            outcome = "completed";
        }
        catch (ReworkRequestedException rework)
        {
            await session.UpdateAsync(_ => { state.LastFailure = null; state.Reason = rework.Findings; });
            await session.Audit.WriteAsync(Context.RunId, "review_failed", definition, verdict: "fail", message: rework.Findings, ct: CancellationToken.None);
            throw;
        }
        catch (HumanInputRequiredException needed)
        {
            await session.UpdateAsync(_ => { state.LastFailure = null; state.Reason = needed.Message; });
            await session.Audit.WriteAsync(Context.RunId, "human_input_required", definition, verdict: "blocked", message: needed.Message, ct: CancellationToken.None);
            throw new StageBlockedException(needed.Message);
        }
        catch (PolicyViolationException violation) when (violation.Correctable && state.BoundaryCorrections < MaxBoundaryCorrections)
        {
            // Writing outside the role's folders is retried once with the denial as feedback; nothing was written.
            await session.UpdateAsync(_ => { state.BoundaryCorrections++; state.LastFailure = violation.Message; state.Reason = violation.Message; });
            await session.Audit.WriteAsync(Context.RunId, "policy_denied", definition, verdict: "deny",
                message: $"{violation.Message} (one correction allowed)", ct: CancellationToken.None);
            throw new AttemptFailedException(violation.Message);
        }
        catch (PolicyViolationException violation)
        {
            // Any other policy denial, a repeated boundary violation or a human rejection stops the stage: no retry, no fallback.
            await session.UpdateAsync(_ => { state.LastFailure = violation.RejectedByHuman ? null : violation.Message; state.Reason = violation.Message; });
            await session.Audit.WriteAsync(Context.RunId, violation.RejectedByHuman ? "policy_rejected" : "policy_denied", definition,
                verdict: "deny", message: violation.Message, ct: CancellationToken.None);
            if (violation.RejectedByHuman) throw new StageBlockedException(violation.Message);
            throw new StageExecutionException(violation.Message, violation);
        }
        catch (StageExecutionException failure)
        {
            await session.UpdateAsync(_ => { state.LastFailure = failure.Message; state.Reason = failure.Message; });
            await session.Audit.WriteAsync(Context.RunId, "attempt_failed", definition, verdict: "fail", message: failure.Message, ct: CancellationToken.None);
            throw new AttemptFailedException(failure.Message);
        }
        finally
        {
            OrchestratorTelemetry.StageDuration.Record(timer.Elapsed.TotalSeconds, [.. tags, OrchestratorTelemetry.Tag("outcome", outcome)]);
        }
    }

    // Exit gates run real builds against the shared workspace, so changes land one stage at a time.
    // A failed gate reverts the agent paths, so siblings only ever see changes that passed a gate.
    private async Task LandAsync(StageDefinition definition, IStage stage, StageOutput output, CancellationToken ct)
    {
        await session.Workspace.WaitAsync(ct);
        try
        {
            string? commit = null;
            GateResult exit;
            try
            {
                exit = await InvokeAsync(() => stage.CheckExitAsync(Context, output, ct), ct);
                await session.Audit.WriteAsync(Context.RunId, "exit_gate", definition, output.Artifact.ContentHash,
                    verdict: exit.Passed ? "pass" : "fail", message: exit.Reason, ct: ct);
                if (!exit.Passed) throw new StageExecutionException(exit.Reason);
                ct.ThrowIfCancellationRequested();
                if (session.Versioning is { } git)
                    commit = await git.CommitStageAsync(Context, definition.Id, output.Decisions.LastOrDefault()?.Id, CancellationToken.None);
            }
            catch (Exception) when (commit is null)
            {
                await RollbackAsync(definition);
                throw;
            }
            await PublishAsync(definition, output, exit.Reason, commit);
        }
        finally { session.Workspace.Release(); }
    }

    private async Task PublishAsync(StageDefinition definition, StageOutput output, string reason, string? commit)
    {
        var state = Context.StageStatuses[definition.Id];
        await session.UpdateAsync(ctx =>
        {
            ctx.Artifacts[definition.Id] = output.Artifact;
            ctx.Decisions = ctx.Decisions.AddRange(output.Decisions);
            if (commit is not null)
            {
                ctx.LastGoodCommit = commit;
                ctx.DecisionCommits = ctx.DecisionCommits.SetItems(output.Decisions.Select(d => KeyValuePair.Create(d.Id, commit)));
            }
            state.ReplanReason = null;
            state.InRework = false;
            state.Status = StageStatus.Completed;
            state.Reason = reason;
            state.LastFailure = null;
            state.CommitSha = commit;
            state.FinishedAtUtc = session.Now;
        });
        if (commit is not null)
            await session.Audit.WriteAsync(Context.RunId, "stage_committed", definition, output.Artifact.ContentHash, message: $"{Context.GitBranch} @ {commit}");
        await session.Audit.WriteAsync(Context.RunId, "stage_completed", definition, output.Artifact.ContentHash, verdict: "pass");
    }

    private async Task<bool> TryFallbackAsync(StageDefinition definition, IStage stage, CancellationToken ct)
    {
        if (stage is not IFallbackStage) return false;
        foreach (var level in new[] { FallbackLevel.SimplerPrompt, FallbackLevel.Template })
        {
            await session.Audit.WriteAsync(Context.RunId, "fallback_started", definition, message: level.ToString(), ct: ct);
            try
            {
                await AttemptAsync(definition, stage, level, ct);
                return true;
            }
            catch (AttemptFailedException) { }
        }
        return false;
    }

    private ResiliencePipeline RetryPipeline(StageDefinition definition)
    {
        var retry = definition.Retry ?? Context.Scenario.Retry;
        if (retry.MaxAttempts <= 1) return ResiliencePipeline.Empty;
        return new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<AttemptFailedException>(),
            MaxRetryAttempts = retry.MaxAttempts - 1,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(retry.BaseSeconds),
            UseJitter = false,
            OnRetry = async args =>
            {
                OrchestratorTelemetry.Retries.Add(1, Tags(definition));
                await session.Audit.WriteAsync(Context.RunId, "retry_scheduled", definition,
                    message: $"Retry {args.AttemptNumber + 1} of {retry.MaxAttempts - 1} in {args.RetryDelay.TotalSeconds:0.###}s after: {args.Outcome.Exception?.Message}");
            }
        }).Build();
    }

    private async Task ApproveAsync(StageDefinition definition, string reason, string? artifactPath, CancellationToken ct)
    {
        var state = Context.StageStatuses[definition.Id];
        await session.UpdateAsync(_ => state.Status = StageStatus.AwaitingApproval);
        await session.Audit.WriteAsync(Context.RunId, "approval_requested", definition, ct: ct);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var approval = await InvokeAsync(() => approvals.RequestAsync(new(Context.RunId, definition.Id, definition.Risk, reason, artifactPath), ct), ct);
        OrchestratorTelemetry.ApprovalWait.Record(waited.Elapsed.TotalSeconds, Tags(definition));
        if (approval.Approved && string.IsNullOrWhiteSpace(approval.Approver))
            throw new StageExecutionException("An approval must identify its approver.");
        // A rejected draft is kept for the next review rather than regenerated.
        await session.UpdateAsync(_ => { state.Approved = approval.Approved; state.Approver = approval.Approver; if (!approval.Approved) state.LastFailure = null; }, save: false);
        await session.Audit.WriteAsync(Context.RunId, "approval_decided", definition, verdict: approval.Approved ? "allow" : "deny",
            approver: approval.Approver, message: approval.Reason, ct: ct);
        if (!approval.Approved) throw new StageBlockedException(approval.Reason);
    }

    // Every failed gate resets the agent paths to the last good commit; a reset that restored files is a rollback.
    private async Task RollbackAsync(StageDefinition definition)
    {
        if (session.Versioning is not { } git) return;
        var reverted = await git.RevertToLastGoodAsync(Context, definition.Id, CancellationToken.None);
        if (reverted.Files.Count == 0) return;
        OrchestratorTelemetry.Rollbacks.Add(1, Tags(definition));
        await session.Audit.WriteAsync(Context.RunId, "rollback", definition, verdict: "reverted",
            message: $"Rolled back {reverted.Files.Count} file(s) to last good commit {reverted.Commit}: {string.Join(", ", reverted.Files.Take(8))}{(reverted.Files.Count > 8 ? ", ..." : "")}");
    }

    private KeyValuePair<string, object?>[] Tags(StageDefinition definition) =>
        [OrchestratorTelemetry.Tag("scenario", Context.Scenario.Name), OrchestratorTelemetry.Tag("stage", definition.Id)];

    private async Task<StageOutcome> StopAsync(StageDefinition definition, StageStatus status, string reason)
    {
        var state = Context.StageStatuses[definition.Id];
        await session.UpdateAsync(_ => { state.Status = status; state.Reason = reason; state.FinishedAtUtc = session.Now; });
        await session.Audit.WriteAsync(Context.RunId, status == StageStatus.Blocked ? "stage_blocked" : "stage_failed", definition,
            verdict: status.ToString().ToLowerInvariant(), message: reason);
        return status == StageStatus.Blocked ? StageOutcome.Blocked : StageOutcome.Failed;
    }

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try
        {
            var result = await action();
            return result ?? throw new StageExecutionException("Stage or approval callback returned null.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not (StageExecutionException or StageBlockedException or BudgetExceededException or PolicyViolationException or HumanInputRequiredException or ReworkRequestedException))
        {
            throw new StageExecutionException(exception.Message, exception);
        }
    }

    private void ValidateOutput(StageDefinition stage, StageOutput output)
    {
        if (output?.Artifact is null || output.Decisions is null) throw new StageExecutionException("Stage output requires an artifact and a decision list.");
        var known = Context.Decisions.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var decision in output.Decisions)
        {
            if (decision is null || string.IsNullOrWhiteSpace(decision.Id) || decision.StageId != stage.Id
                || decision.AtUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(decision.MadeBy)
                || string.IsNullOrWhiteSpace(decision.Summary) || string.IsNullOrWhiteSpace(decision.Rationale)
                || decision.ParentDecisionIds is null || decision.ParentDecisionIds.Any(id => !known.Contains(id)) || !known.Add(decision.Id))
                throw new StageExecutionException($"Stage '{stage.Id}' returned invalid decision lineage or timestamps.");
        }
    }
}
