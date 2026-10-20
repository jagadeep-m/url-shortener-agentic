using System.Diagnostics;
using System.Text.Json;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

public sealed class RunEngineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"orchestrator-{Guid.NewGuid():N}");
    private readonly SqliteRunStateStore store;
    private readonly JsonlAuditWriter audit;
    private readonly ApprovalService approvals = new();

    public RunEngineTests()
    {
        store = new(directory);
        audit = new(directory);
    }

    private RunEngine Engine() => new(store, audit, approvals);

    private sealed class ApprovalService : IApprovalService
    {
        public bool Approved { get; set; } = true;
        public int Calls { get; private set; }
        public Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ApprovalResult(Approved, "test-reviewer", Approved ? "Reviewed." : "Needs revision."));
        }
    }

    private sealed class Stage(string id, params string[] dependencies) : IStage
    {
        public string Id => id;
        public IReadOnlyList<string> DependsOn => dependencies;
        public RiskLevel Risk { get; init; } = RiskLevel.Low;
        public int Calls { get; private set; }
        public Func<RunContext, CancellationToken, Task<GateResult>> Entry { get; set; } = (_, _) => Task.FromResult(GateResult.Pass());
        public Func<RunContext, CancellationToken, Task<StageOutput>>? Body { get; set; }
        public Func<RunContext, StageOutput, CancellationToken, Task<GateResult>> Exit { get; set; } = (_, _, _) => Task.FromResult(GateResult.Pass());
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Entry(ctx, ct);
        public Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct)
        {
            Calls++;
            return Body?.Invoke(ctx, ct) ?? Task.FromResult(new StageOutput(new Artifact($"{Id}.md", $"Output from {Id}\nSecond line."),
                [new Decision($"decision-{Id}", Id, $"Complete {Id}", "Required by the scenario.",
                    ctx.Decisions.Select(x => x.Id).ToArray(), "TestAgent", DateTime.UtcNow)]));
        }
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) => Exit(ctx, output, ct);
    }

    private static Scenario Plan(params Stage[] stages) => new()
    {
        Retry = new() { BaseSeconds = 0.001 },
        Stages = stages.Select(stage => new StageDefinition
        { Id = stage.Id, Agent = "TestAgent", DependsOn = stage.DependsOn.ToList(), Risk = stage.Risk }).ToList()
    };

    [Fact]
    public async Task RunsInDependencyWavesAndPersistsEachCompletedStage()
    {
        var calls = new List<string>();
        var first = new Stage("first") { Risk = RiskLevel.High };
        var second = new Stage("second", "first");
        var third = new Stage("third", "first");
        first.Body = async (ctx, ct) =>
        {
            calls.Add("first");
            var saved = await store.LoadAsync(ctx.RunId, ct);
            Assert.Equal(StageStatus.Running, saved!.StageStatuses["first"].Status);
            Assert.True(saved.StageStatuses["first"].Approved);
            return new(new Artifact("first.md", "input"));
        };
        second.Entry = async (ctx, ct) =>
        {
            var saved = await new SqliteRunStateStore(directory).LoadAsync(ctx.RunId, ct);
            Assert.Equal(StageStatus.Completed, saved!.StageStatuses["first"].Status);
            Assert.Equal("input", saved.Artifacts["first"].Content);
            lock (calls) calls.Add("second");
            return GateResult.Pass();
        };
        third.Entry = (_, _) => { lock (calls) calls.Add("third"); return Task.FromResult(GateResult.Pass()); };
        var result = await Engine().StartAsync(Plan(second, third, first), "Build a shortener", [third, first, second]);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("first", calls[0]);
        Assert.Equal(new[] { "second", "third" }, calls.Skip(1).Order());
        Assert.Equal(1, approvals.Calls);
        Assert.All(result.StageStatuses.Values, state => Assert.Equal(StageStatus.Completed, state.Status));
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task EntryGateBlocksExecutionAndDownstreamStages()
    {
        var first = new Stage("first") { Entry = (_, _) => Task.FromResult(GateResult.Block("Missing approved requirements.")) };
        var next = new Stage("next", "first");
        var result = await Engine().StartAsync(Plan(first, next), "Build", [first, next]);
        Assert.Equal(RunStatus.Blocked, result.Status);
        Assert.Equal(StageStatus.Blocked, result.StageStatuses["first"].Status);
        Assert.Equal("Missing approved requirements.", result.StageStatuses["first"].Reason);
        Assert.Equal(0, first.Calls);
        Assert.Equal(0, next.Calls);
        Assert.Equal(RunStatus.Blocked, (await store.LoadAsync(result.RunId))!.Status);
    }

    [Fact]
    public async Task ExitGateFailureDoesNotPublishArtifactOrDecisionsOrRunDependents()
    {
        var first = new Stage("first") { Exit = (_, _, _) => Task.FromResult(GateResult.Block("dotnet test failed.")) };
        var next = new Stage("next", "first");
        var result = await Engine().StartAsync(Plan(first, next), "Build", [first, next]);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(StageStatus.Failed, result.StageStatuses["first"].Status);
        Assert.Equal(3, first.Calls); // maxAttempts defaults to 3; retries are bounded.
        Assert.Equal(0, next.Calls);
        Assert.Empty(result.Artifacts);
        Assert.Empty(result.Decisions);
        Assert.Equal(StageStatus.Failed, (await store.LoadAsync(result.RunId))!.StageStatuses["first"].Status);
    }

    [Fact]
    public async Task ResumeRestoresContextAndSkipsCompletedStages()
    {
        var first = new Stage("first") { Risk = RiskLevel.High };
        var second = new Stage("second", "first") { Entry = (_, _) => Task.FromResult(GateResult.Block("Waiting for input.")) };
        var original = await Engine().StartAsync(Plan(first, second), "Build", [first, second]);
        Assert.Equal(RunStatus.Blocked, original.Status);
        var restartedFirst = new Stage("first") { Risk = RiskLevel.High, Body = (_, _) => throw new InvalidOperationException("Must not run again") };
        var restartedSecond = new Stage("second", "first");
        using var restartedAudit = new JsonlAuditWriter(directory);
        var resumed = await new RunEngine(new SqliteRunStateStore(directory), restartedAudit, approvals)
            .ResumeAsync(original.RunId, [restartedFirst, restartedSecond]);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(0, restartedFirst.Calls);
        Assert.Equal(1, restartedSecond.Calls);
        Assert.Equal(original.TraceId, resumed.TraceId);
        Assert.Equal(original.Artifacts["first"].ContentHash, resumed.Artifacts["first"].ContentHash);
        Assert.Equal("decision-first", Assert.Single(resumed.Decisions.Single(x => x.StageId == "second").ParentDecisionIds));
        Assert.Equal(1, approvals.Calls); // Completed approval is not requested again.
    }

    [Fact]
    public async Task FailedStageRequiresExplicitResumeAndCanThenComplete()
    {
        var stage = new Stage("first") { Exit = (_, _, _) => Task.FromResult(GateResult.Block("Broken test")) };
        var failed = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        var repaired = new Stage("first");
        var result = await Engine().ResumeAsync(failed.RunId, [repaired]);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(4, result.StageStatuses["first"].Attempts); // three failed attempts, then the explicit resume
        Assert.Single(result.Decisions);
    }

    [Fact]
    public async Task StageExceptionProducesReadableFailedCheckpoint()
    {
        var stage = new Stage("first") { Body = (_, _) => throw new InvalidOperationException("Tool invocation failed.") };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("Tool invocation failed.", result.StageStatuses["first"].Reason);
        Assert.Equal(RunStatus.Failed, (await store.LoadAsync(result.RunId))!.Status);
    }

    [Fact]
    public async Task DeniedApprovalBlocksHighRiskStageUntilExplicitResume()
    {
        approvals.Approved = false;
        var stage = new Stage("release") { Risk = RiskLevel.High };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        Assert.Equal(RunStatus.Blocked, result.Status);
        Assert.Equal(0, stage.Calls);
        approvals.Approved = true;
        var resumed = await Engine().ResumeAsync(result.RunId, [stage]);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal("test-reviewer", resumed.StageStatuses["release"].Approver);
        Assert.Equal(2, approvals.Calls);
    }

    [Fact]
    public async Task CancellationSavesInterruptedStateAndSupportsResume()
    {
        using var cancellation = new CancellationTokenSource();
        var stage = new Stage("first") { Body = async (_, ct) => { await cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); return new(new Artifact("x", "x")); } };
        var paused = await Engine().StartAsync(Plan(stage), "Build", [stage], cancellation.Token);
        Assert.Equal(RunStatus.Paused, paused.Status);
        Assert.Equal(StageStatus.Interrupted, (await store.LoadAsync(paused.RunId))!.StageStatuses["first"].Status);
        Assert.Equal(RunStatus.Completed, (await Engine().ResumeAsync(paused.RunId, [new Stage("first")])).Status);
    }

    [Fact]
    public async Task InvalidDecisionLineageIsRejectedBeforePublication()
    {
        var stage = new Stage("first")
        {
            Body = (_, _) => Task.FromResult(new StageOutput(new Artifact("x", "x"),
            [new Decision("d", "first", "Summary", "Rationale", ["missing-parent"], "Agent", DateTime.UtcNow)]))
        };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("lineage", result.StageStatuses["first"].Reason);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task ResumeRejectsChangedRiskOrDependencies()
    {
        var stage = new Stage("first") { Risk = RiskLevel.High, Entry = (_, _) => Task.FromResult(GateResult.Block("wait")) };
        var original = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        await Assert.ThrowsAsync<ScenarioException>(() => Engine().ResumeAsync(original.RunId, [new Stage("first")]));
    }

    [Fact]
    public async Task CompletedResumeIsANoOpAndUnknownIdIsClear()
    {
        var stage = new Stage("first");
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        var before = await File.ReadAllTextAsync(audit.GetPath(result.RunId));
        var resumed = await Engine().ResumeAsync(result.RunId, [stage]);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(1, stage.Calls);
        Assert.Equal(before, await File.ReadAllTextAsync(audit.GetPath(result.RunId)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine().ResumeAsync(Guid.NewGuid(), [stage]));
    }

    [Fact]
    public async Task ConcurrentResumeCannotAcquireAnExecutingRun()
    {
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = new Stage("first") { Body = async (ctx, _) => { started.SetResult(ctx.RunId); await release.Task; return new(new Artifact("x", "x")); } };
        var execution = Engine().StartAsync(Plan(stage), "Build", [stage]);
        var runId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Assert.ThrowsAsync<RunConcurrencyException>(() => Engine().ResumeAsync(runId, [stage])); }
        finally { release.SetResult(); }
        Assert.Equal(RunStatus.Completed, (await execution).Status);
    }

    // A live brownfield run paused at 431,508 of 400,000 tokens; since tokens count for the whole run, a plain resume
    // paused again. The person resuming can now raise the budget, and the change is audited with their name.
    [Fact]
    public async Task A_token_budget_pause_continues_only_after_an_audited_budget_raise()
    {
        var calls = 0;
        var stage = new Stage("review")
        {
            Body = (ctx, _) =>
        {
            if (++calls == 1)
            {
                ctx.Budget.AddTokens(150);
                throw new BudgetExceededException("Run token budget exceeded (150/100) by the Reviewer call.");
            }
            return Task.FromResult(new StageOutput(new Artifact("review.md", "PASS")));
        }
        };
        var plan = Plan(stage);
        plan.Budget = new() { MaxTokens = 100 };
        var paused = await Engine().StartAsync(plan, "Build", [stage]);
        Assert.Equal(RunStatus.Paused, paused.Status);

        Assert.Equal(RunStatus.Paused, (await Engine().ResumeAsync(paused.RunId, [stage])).Status); // the same budget stops again
        var tooLow = await Assert.ThrowsAsync<ArgumentException>(() => Engine().ResumeAsync(paused.RunId, [stage], maxTokens: 150, changedBy: "alice"));
        Assert.Equal("The token budget must be above the 150 tokens this run has already used.", tooLow.Message);

        var resumed = await Engine().ResumeAsync(paused.RunId, [stage], maxTokens: 1000, changedBy: "alice");
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(1000, (await store.LoadAsync(paused.RunId))!.Budget.MaxTokens);
        var change = (await File.ReadAllLinesAsync(audit.GetPath(paused.RunId))).Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("event").GetString() == "token_budget_changed");
        Assert.Equal("alice", change.GetProperty("approver").GetString());
        Assert.Equal("Token budget changed from 100 to 1,000 on resume (150 already used).", change.GetProperty("message").GetString());
    }

    [Fact]
    public async Task StaleSnapshotCannotOverwriteNewerState()
    {
        var stage = new Stage("first");
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        var first = (await store.LoadAsync(result.RunId))!;
        var stale = (await store.LoadAsync(result.RunId))!;
        first.Budget.TokensUsed = 42;
        await store.SaveAsync(first);
        await Assert.ThrowsAsync<RunConcurrencyException>(() => store.SaveAsync(stale));
        Assert.Equal(42, (await store.LoadAsync(result.RunId))!.Budget.TokensUsed);
    }

    [Fact]
    public async Task AuditIsOneLinePerEventAppendOnlyAndCorrelatedAcrossResume()
    {
        var stage = new Stage("requirements") { Risk = RiskLevel.High, Entry = (_, _) => Task.FromResult(GateResult.Block("Missing input\nReview needed.")) };
        var blocked = await Engine().StartAsync(Plan(stage), "Build a URL shortener", [stage]);
        var originalLines = await File.ReadAllLinesAsync(audit.GetPath(blocked.RunId));
        Assert.Equal(4, originalLines.Length);
        stage.Entry = (_, _) => Task.FromResult(GateResult.Pass());
        var completed = await Engine().ResumeAsync(blocked.RunId, [stage]);
        var lines = await File.ReadAllLinesAsync(audit.GetPath(blocked.RunId));
        Assert.Equal(13, lines.Length);
        Assert.Equal(originalLines, lines.Take(originalLines.Length));
        var events = lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(events.Length, events.Select(x => x.GetProperty("eventId").GetGuid()).Distinct().Count());
        Assert.All(events, entry =>
        {
            Assert.Equal(completed.TraceId, entry.GetProperty("traceId").GetString());
            Assert.Equal(32, entry.GetProperty("traceId").GetString()!.Length);
            Assert.Equal(DateTimeKind.Utc, entry.GetProperty("timestampUtc").GetDateTime().Kind);
            Assert.Equal(completed.RunId, entry.GetProperty("runId").GetGuid());
        });
        Assert.Contains(events, entry => entry.GetProperty("event").GetString() == "approval_decided" && entry.GetProperty("approver").GetString() == "test-reviewer");
        Assert.Equal("run_completed", events[^1].GetProperty("event").GetString());
        var completedEvent = events.Single(entry => entry.GetProperty("event").GetString() == "stage_completed");
        Assert.Equal(completed.Artifacts["requirements"].ContentHash, completedEvent.GetProperty("outputHash").GetString());
        if (Environment.GetEnvironmentVariable("ORCHESTRATOR_TEST_ARTIFACTS") is { Length: > 0 } output)
        {
            var target = Path.Combine(output, completed.RunId.ToString("D"));
            Directory.CreateDirectory(target);
            File.Copy(audit.GetPath(completed.RunId), Path.Combine(target, "audit.jsonl"), overwrite: true);
        }
    }

    [Fact]
    public async Task AuditSerializesConcurrentCallsWithoutInterleavingLines()
    {
        using var activity = OrchestratorTelemetry.Source.StartActivity("audit-test");
        Assert.NotNull(activity);
        var runId = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => audit.WriteAsync(runId, $"event_{i}", message: "Line one\nLine two")));
        var lines = await File.ReadAllLinesAsync(audit.GetPath(runId));
        Assert.Equal(50, lines.Length);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal(activity.TraceId.ToHexString(), json.RootElement.GetProperty("traceId").GetString());
        }
    }

    public void Dispose()
    {
        audit.Dispose();
        Directory.Delete(directory, recursive: true);
    }
}
