using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Orchestrator.Core;

public sealed class RunEngine
{
    private static readonly JsonSerializerOptions PlanJson = new(RunJson.Options) { WriteIndented = true };
    private readonly SqliteRunStateStore store;
    private readonly JsonlAuditWriter audit;
    private readonly IApprovalService approvals;
    private readonly TimeProvider clock;
    private readonly IWorkspaceVersioning? versioning;
    private readonly HumanWaitClock? humanWait;

    public RunEngine(SqliteRunStateStore store, JsonlAuditWriter audit, IApprovalService approvals,
        TimeProvider? clock = null, IWorkspaceVersioning? versioning = null, HumanWaitClock? humanWait = null)
    {
        if (store.RunsDirectory != audit.RunsDirectory) throw new ArgumentException("State and audit must use the same runs directory.");
        this.store = store;
        this.audit = audit;
        this.approvals = approvals;
        this.clock = clock ?? TimeProvider.System;
        this.versioning = versioning;
        this.humanWait = humanWait;
    }

    public async Task<RunContext> StartAsync(Scenario scenario, string requirement, IReadOnlyCollection<IStage> stages, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        var plan = RunJson.Clone(scenario);
        var ordered = DagBuilder.Build(plan);
        var implementations = Bind(ordered, stages);
        var context = new RunContext
        {
            Requirement = requirement,
            Scenario = plan,
            Budget = RunJson.Clone(plan.Budget),
            StartedAtUtc = clock.GetUtcNow().UtcDateTime,
            StageStatuses = new ConcurrentDictionary<string, StageState>(ordered.ToDictionary(x => x.Id, _ => new StageState()), StringComparer.Ordinal)
        };
        await using var lease = store.AcquireRunLease(context.RunId);
        return await ExecuteAsync(context, ordered, implementations, false, ct);
    }

    public async Task<RunContext> ResumeAsync(Guid runId, IReadOnlyCollection<IStage> stages, CancellationToken ct = default)
    {
        await using var lease = store.AcquireRunLease(runId);
        var context = await store.LoadAsync(runId, ct) ?? throw new KeyNotFoundException($"Run '{runId}' was not found.");
        var ordered = DagBuilder.Build(context.Scenario);
        var implementations = Bind(ordered, stages);
        ValidateCheckpoint(context, ordered);
        // A completed run resumes only when an accepted artifact was edited since (re-plan); otherwise it is a no-op.
        if (context.Status == RunStatus.Completed && !await HasEditedArtifactsAsync(context, ordered, implementations, ct)) return context;
        return await ExecuteAsync(context, ordered, implementations, true, ct);
    }

    private async Task<RunContext> ExecuteAsync(RunContext context, IReadOnlyList<StageDefinition> ordered,
        IReadOnlyDictionary<string, IStage> stages, bool resume, CancellationToken ct)
    {
        using var activity = OrchestratorTelemetry.StartRun(context, resume);
        if (versioning is not null) await versioning.BeginAsync(context, ct);
        using var session = new RunSession(context, store, audit, clock, versioning);
        // Safe-stop: the caller's token (stop command, Ctrl+C), the time budget and the token budget all cancel one source.
        // The time budget counts machine time: time spent waiting for human approvals is not counted (HumanWaitClock).
        using var timeBudget = new CancellationTokenSource();
        using var tokenBudget = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, timeBudget.Token, tokenBudget.Token);
        using var watcherStop = new CancellationTokenSource();
        var watcher = WatchTimeBudgetAsync(session, BudgetWindow(context.Budget), timeBudget, watcherStop.Token);
        try
        {
            return await RunAsync(context, ordered, stages, resume, session, timeBudget, tokenBudget, stop, ct);
        }
        finally
        {
            await watcherStop.CancelAsync();
            await watcher;
        }
    }

    private async Task<RunContext> RunAsync(RunContext context, IReadOnlyList<StageDefinition> ordered, IReadOnlyDictionary<string, IStage> stages,
        bool resume, RunSession session, CancellationTokenSource timeBudget, CancellationTokenSource tokenBudget, CancellationTokenSource stop, CancellationToken ct)
    {
        await session.UpdateAsync(c => { c.Status = RunStatus.Running; c.FinishedAtUtc = null; c.StopReason = null; });
        await audit.WriteAsync(context.RunId, resume ? "run_resumed" : "run_started", ct: CancellationToken.None);
        if (context.GitBranch is not null)
            await audit.WriteAsync(context.RunId, "workspace_ready", message: $"{context.GitBranch} @ {context.LastGoodCommit}", ct: CancellationToken.None);
        await WritePlanAsync(context, ordered);
        if (!resume) OrchestratorTelemetry.RunsStarted.Add(1, OrchestratorTelemetry.Tag("scenario", context.Scenario.Name));
        var executor = new StageExecutor(session, approvals);
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (await ReplanAsync(session, ordered, stages, stop.Token) is { } invalidEdit)
                    return await FinishAsync(session, RunStatus.Blocked, invalidEdit);
                if (context.Budget.TokensUsed >= context.Budget.MaxTokens)
                    throw new BudgetExceededException($"Token budget exhausted ({context.Budget.TokensUsed}/{context.Budget.MaxTokens}).");
                var wave = ordered.Where(d => context.StageStatuses[d.Id].Status != StageStatus.Completed
                    && d.DependsOn.All(id => context.StageStatuses[id].Status == StageStatus.Completed)).ToArray();
                if (wave.Length == 0) break;
                var outcomes = await RunWaveAsync(executor, wave, stages, tokenBudget, stop.Token);
                var stopped = wave.Zip(outcomes).Where(x => x.Second is StageOutcome.Failed or StageOutcome.Blocked).ToArray();
                if (stopped.Length > 0)
                {
                    var status = stopped.Any(x => x.Second == StageOutcome.Failed) ? RunStatus.Failed : RunStatus.Blocked;
                    var first = stopped.First(x => status == RunStatus.Failed ? x.Second == StageOutcome.Failed : true).First;
                    return await FinishAsync(session, status, context.StageStatuses[first.Id].Reason);
                }
                foreach (var reviewer in wave.Zip(outcomes).Where(x => x.Second == StageOutcome.Rework).Select(x => x.First))
                    await ReworkAsync(session, ordered, reviewer);
            }
            return await FinishAsync(session, RunStatus.Completed, null);
        }
        catch (BudgetExceededException exception)
        {
            return await PauseAsync(session, "budget_exceeded", exception.Message);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return timeBudget.IsCancellationRequested && !ct.IsCancellationRequested
                ? await PauseAsync(session, "budget_exceeded", $"Time budget of {context.Budget.MaxMinutes} minutes of machine time exhausted (time waiting for approvals is not counted).")
                : await PauseAsync(session, "cancelled", "Cancellation requested; explicit resume is required.");
        }
    }

    // Every stage whose dependencies are complete runs concurrently; the next wave waits for all of them.
    private static async Task<StageOutcome[]> RunWaveAsync(StageExecutor executor, IReadOnlyList<StageDefinition> wave,
        IReadOnlyDictionary<string, IStage> stages, CancellationTokenSource tokenBudget, CancellationToken ct)
    {
        var tasks = wave.Select(definition => Task.Run(async () =>
        {
            try { return await executor.RunAsync(definition, stages[definition.Id], ct); }
            catch (BudgetExceededException) { await tokenBudget.CancelAsync(); throw; }
        })).ToArray();
        try { return await Task.WhenAll(tasks); }
        catch
        {
            var errors = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).ToArray();
            var preferred = errors.OfType<BudgetExceededException>().FirstOrDefault<Exception>()
                ?? errors.FirstOrDefault(e => e is not OperationCanceledException)
                ?? new OperationCanceledException(ct);
            ExceptionDispatchInfo.Throw(preferred);
            throw;
        }
    }

    private async Task<RunContext> FinishAsync(RunSession session, RunStatus status, string? reason)
    {
        var context = session.Context;
        await session.UpdateAsync(c => { c.Status = status; c.FinishedAtUtc = session.Now; c.StopReason = reason; });
        var (name, verdict) = status switch
        {
            RunStatus.Completed => ("run_completed", "pass"),
            RunStatus.Blocked => ("run_blocked", "blocked"),
            _ => ("run_failed", "failed")
        };
        await audit.WriteAsync(context.RunId, name, verdict: verdict, message: reason, ct: CancellationToken.None);
        if (versioning is not null)
            await File.WriteAllTextAsync(Path.Combine(store.RunsDirectory, context.RunId.ToString("D"), "diff.patch"), await versioning.DiffSinceStartAsync(context, CancellationToken.None));
        RecordFinished(session, status.ToString());
        return context;
    }

    private static void RecordFinished(RunSession session, string status)
    {
        var scenario = OrchestratorTelemetry.Tag("scenario", session.Context.Scenario.Name);
        OrchestratorTelemetry.RunsFinished.Add(1, scenario, OrchestratorTelemetry.Tag("status", status));
        OrchestratorTelemetry.RunDuration.Record(session.Elapsed.TotalSeconds, scenario, OrchestratorTelemetry.Tag("status", status));
    }

    // Edited artifacts: the edited stage re-lands its draft through its own gates (and approval when
    // high-risk); every stage downstream of it is marked stale and regenerated. Unaffected stages are kept.
    // Returns a blocking reason when an edited artifact is invalid.
    private async Task<string?> ReplanAsync(RunSession session, IReadOnlyList<StageDefinition> ordered,
        IReadOnlyDictionary<string, IStage> stages, CancellationToken ct)
    {
        var context = session.Context;
        IReadOnlyList<Replanner.Change> changes;
        try { changes = await Replanner.DetectAsync(context, ordered, stages, ct); }
        catch (InvalidDataException exception)
        {
            var reason = exception.Message + " Fix the file, then resume.";
            await audit.WriteAsync(context.RunId, "replan_blocked", verdict: "blocked", message: reason, ct: CancellationToken.None);
            return reason;
        }
        if (changes.Count == 0) return null;
        var edited = changes.Select(c => c.StageId).ToArray();
        var stale = Replanner.Downstream(ordered, edited).Where(id => context.StageStatuses[id].Status != StageStatus.Pending).ToArray();
        var why = "Upstream artifact changed: " + string.Join(", ", changes.Select(c => $"{c.StageId} {c.OldHash[..8]} -> {c.NewHash[..8]}"));
        await session.UpdateAsync(c =>
        {
            foreach (var id in edited) MarkStale(c.StageStatuses[id], null, "Artifact edited after completion; re-landing the edited draft.");
            foreach (var id in stale) MarkStale(c.StageStatuses[id], why, why);
            c.SupersededDecisions = c.SupersededDecisions.Union(c.Decisions.Where(d => stale.Contains(d.StageId)).Select(d => d.Id));
        });
        OrchestratorTelemetry.Replans.Add(1, OrchestratorTelemetry.Tag("scenario", context.Scenario.Name));
        await audit.WriteAsync(context.RunId, "replan", verdict: "stale", ct: CancellationToken.None,
            message: $"{why}. Re-landing edited: {string.Join(", ", edited)}. Re-running: {(stale.Length > 0 ? string.Join(", ", stale) : "none")}. Other stages are kept.");
        return null;
    }

    // A failed review: the reviewed stages are re-run with the findings as their re-plan reason, everything after them
    // that already ran is refreshed, and the reviewer reviews again. Bounded by StageExecutor.MaxReworks.
    private async Task ReworkAsync(RunSession session, IReadOnlyList<StageDefinition> ordered, StageDefinition reviewer)
    {
        var context = session.Context;
        var state = context.StageStatuses[reviewer.Id];
        var findings = state.Reason ?? "The review found unmet requirements.";
        var reworked = reviewer.DependsOn.Where(id => context.StageStatuses[id].Status == StageStatus.Completed).ToArray();
        var downstream = Replanner.Downstream(ordered, reworked)
            .Where(id => id != reviewer.Id && context.StageStatuses[id].Status != StageStatus.Pending).ToArray();
        await session.UpdateAsync(c =>
        {
            state.Reworks++;
            foreach (var id in reworked) MarkStale(c.StageStatuses[id], $"The review failed. Fix these findings: {findings}", "Reworking after a failed review.");
            foreach (var id in downstream) MarkStale(c.StageStatuses[id], "Upstream work was reworked after a failed review.", "Re-running after rework.");
            MarkStale(state, "Review the reworked implementation again.", "Re-reviewing after rework.");
            c.SupersededDecisions = c.SupersededDecisions.Union(c.Decisions.Where(d => reworked.Contains(d.StageId)).Select(d => d.Id));
        });
        await audit.WriteAsync(context.RunId, "rework", verdict: "rework", ct: CancellationToken.None,
            message: $"Review '{reviewer.Id}' failed; re-running {string.Join(", ", reworked)} with its findings (rework {state.Reworks} of {StageExecutor.MaxReworks}).");
    }

    private static void MarkStale(StageState state, string? replanReason, string reason)
    {
        state.Status = StageStatus.Stale;
        state.Approved = false;
        state.LastFailure = null;
        state.ReplanReason = replanReason;
        state.Reason = reason;
    }

    private static async Task<bool> HasEditedArtifactsAsync(RunContext context, IReadOnlyList<StageDefinition> ordered,
        IReadOnlyDictionary<string, IStage> stages, CancellationToken ct)
    {
        try { return (await Replanner.DetectAsync(context, ordered, stages, ct)).Count > 0; }
        catch (InvalidDataException) { return true; } // let the run report the invalid edit
    }

    private async Task<RunContext> PauseAsync(RunSession session, string verdict, string reason)
    {
        var context = session.Context;
        await session.UpdateAsync(c =>
        {
            c.Status = RunStatus.Paused;
            c.FinishedAtUtc = null;
            c.StopReason = reason;
            foreach (var state in c.StageStatuses.Values.Where(s => s.Status is StageStatus.Running or StageStatus.AwaitingApproval))
            {
                state.Status = StageStatus.Interrupted;
                state.Reason = reason;
            }
        });
        await audit.WriteAsync(context.RunId, "run_paused", verdict: verdict, message: reason, ct: CancellationToken.None);
        RecordFinished(session, RunStatus.Paused.ToString());
        return context;
    }

    // Checks elapsed machine time (run time minus human waits) against the budget and trips the time-budget token.
    private async Task WatchTimeBudgetAsync(RunSession session, TimeSpan budget, CancellationTokenSource timeBudget, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
            while (await timer.WaitForNextTickAsync(ct))
                if (session.Elapsed - (humanWait?.Total ?? TimeSpan.Zero) >= budget)
                {
                    await timeBudget.CancelAsync();
                    return;
                }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static TimeSpan BudgetWindow(RunBudget budget) =>
        TimeSpan.FromMilliseconds(Math.Min(budget.MaxMinutes * 60_000, int.MaxValue - 1));

    private async Task WritePlanAsync(RunContext context, IReadOnlyList<StageDefinition> ordered)
    {
        var waves = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stage in ordered) waves[stage.Id] = stage.DependsOn.Count == 0 ? 1 : stage.DependsOn.Max(id => waves[id]) + 1;
        var plan = new
        {
            context.RunId, Scenario = context.Scenario.Name, context.Scenario.Replay, context.Scenario.Model, context.Requirement,
            context.Budget.MaxMinutes, context.Budget.MaxTokens, context.Scenario.Retry,
            Stages = ordered.Select(s => new { s.Id, s.Agent, s.DependsOn, s.Risk, Wave = waves[s.Id], Retry = s.Retry ?? context.Scenario.Retry })
        };
        var directory = Path.Combine(store.RunsDirectory, context.RunId.ToString("D"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(plan, PlanJson));
        var input = Path.Combine(directory, "input.md");
        if (!File.Exists(input))
            await File.WriteAllTextAsync(input, $"# Input\n\nScenario: {context.Scenario.Name}\n\nStarted: {context.StartedAtUtc:O}\n\n## Requirement\n\n{context.Requirement.Trim()}\n");
    }

    private static Dictionary<string, IStage> Bind(IReadOnlyList<StageDefinition> ordered, IReadOnlyCollection<IStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var bindings = new Dictionary<string, IStage>(StringComparer.Ordinal);
        foreach (var stage in stages)
            if (stage is null || !bindings.TryAdd(stage.Id, stage)) throw new ScenarioException("Stage implementations contain null or duplicate IDs.");
        if (bindings.Count != ordered.Count) throw new ScenarioException("Stage implementations do not match the saved scenario.");
        foreach (var definition in ordered)
            if (!bindings.TryGetValue(definition.Id, out var stage) || stage.Risk != definition.Risk
                || stage.DependsOn.Count != definition.DependsOn.Count || !stage.DependsOn.ToHashSet(StringComparer.Ordinal).SetEquals(definition.DependsOn))
                throw new ScenarioException($"Implementation for stage '{definition.Id}' must match its saved ID, dependencies and risk.");
        return bindings;
    }

    private static void ValidateCheckpoint(RunContext context, IReadOnlyList<StageDefinition> ordered)
    {
        if (!context.StageStatuses.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ordered.Select(x => x.Id)))
            throw new InvalidDataException("Checkpoint stage statuses do not match its scenario.");
        foreach (var stage in ordered)
        {
            var state = context.StageStatuses[stage.Id];
            if (state.Status != StageStatus.Completed) continue;
            if (!context.Artifacts.ContainsKey(stage.Id) || (stage.Risk == RiskLevel.High && !state.Approved)
                || stage.DependsOn.Any(id => context.StageStatuses[id].Status != StageStatus.Completed))
                throw new InvalidDataException($"Completed stage '{stage.Id}' has missing artifacts, approval or dependencies.");
        }
        if (context.Status == RunStatus.Completed && context.StageStatuses.Values.Any(x => x.Status != StageStatus.Completed))
            throw new InvalidDataException("Completed run contains unfinished stages.");
    }
}
