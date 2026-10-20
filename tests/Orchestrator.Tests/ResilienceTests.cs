using System.Text.Json;
using LibGit2Sharp;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

public sealed class ResilienceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"resilience-{Guid.NewGuid():N}");
    private string Runs => Path.Combine(directory, "runs");
    private readonly JsonlAuditWriter audit;
    private readonly SqliteRunStateStore store;

    public ResilienceTests()
    {
        store = new(Runs);
        audit = new(Runs);
    }

    private sealed class Approvals : IApprovalService
    {
        public Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct) => Task.FromResult(new ApprovalResult(true, "test-reviewer", "ok"));
    }

    private class Stage(string id, params string[] dependencies) : IStage
    {
        public string Id => id;
        public IReadOnlyList<string> DependsOn => dependencies;
        public RiskLevel Risk => RiskLevel.Low;
        public int Calls;
        public readonly List<string?> FeedbackSeen = [];
        public Func<RunContext, CancellationToken, Task>? Body { get; init; }
        public Func<RunContext, GateResult> Exit { get; init; } = _ => GateResult.Pass();
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(GateResult.Pass());
        public async Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (FeedbackSeen) FeedbackSeen.Add(ctx.StageStatuses[Id].LastFailure);
            if (Body is not null) await Body(ctx, ct);
            return Output(ctx, "normal");
        }
        protected StageOutput Output(RunContext ctx, string variant) => new(new Artifact(Id + ".md", $"{Id} {variant}"),
            [new Decision(Guid.NewGuid().ToString("N"), Id, $"{Id} done", "Test.", ctx.Decisions.Where(d => DependsOn.Contains(d.StageId)).Select(d => d.Id).ToArray(), "TestAgent", DateTime.UtcNow)]);
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) => Task.FromResult(Exit(ctx));
    }

    private sealed class FallbackStage(string id, bool templateWorks) : Stage(id), IFallbackStage
    {
        public readonly List<FallbackLevel> Levels = [];
        public Task<StageOutput> ExecuteFallbackAsync(RunContext ctx, FallbackLevel level, CancellationToken ct)
        {
            Levels.Add(level);
            if (level == FallbackLevel.Template && templateWorks) return Task.FromResult(Output(ctx, "template"));
            throw new InvalidOperationException($"{level} failed too.");
        }
    }

    private static Scenario Plan(params Stage[] stages) => new()
    {
        Retry = new() { MaxAttempts = 3, BaseSeconds = 0.001 },
        Stages = stages.Select(s => new StageDefinition { Id = s.Id, Agent = "TestAgent", DependsOn = s.DependsOn.ToList(), Risk = s.Risk }).ToList()
    };

    private RunEngine Engine(IWorkspaceVersioning? git = null) => new(store, audit, new Approvals(), versioning: git);

    private async Task<string[]> Events(Guid runId) =>
        (await File.ReadAllLinesAsync(audit.GetPath(runId))).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()!).ToArray();

    [Fact]
    public async Task Independent_stages_run_concurrently_and_the_join_waits_for_all()
    {
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Barrier(RunContext _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref started) == 3) allStarted.SetResult();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); // deadlocks unless all three run at once
        }
        var design = new Stage("design");
        Stage[] branches = [new("implement", "design") { Body = Barrier }, new("tests", "design") { Body = Barrier }, new("docs", "design") { Body = Barrier }];
        var validate = new Stage("validate", "implement", "tests", "docs");
        var result = await Engine().StartAsync(Plan([design, .. branches, validate]), "Build", [design, .. branches, validate]);

        Assert.Equal(RunStatus.Completed, result.Status);
        var states = branches.Select(b => result.StageStatuses[b.Id]).ToArray();
        Assert.True(states.Max(s => s.StartedAtUtc) < states.Min(s => s.FinishedAtUtc), "parallel stage intervals must overlap");
        Assert.True(result.StageStatuses["validate"].StartedAtUtc >= states.Max(s => s.FinishedAtUtc));
        var plan = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Runs, result.RunId.ToString("D"), "plan.json"))).RootElement;
        Assert.Equal(new[] { 1, 2, 2, 2, 3 }, plan.GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("wave").GetInt32()));
    }

    [Fact]
    public async Task Retries_are_bounded_and_feed_the_failure_reason_into_the_next_attempt()
    {
        var attempt = 0;
        var stage = new Stage("implement") { Exit = _ => GateResult.Block($"dotnet test failed on attempt {++attempt}") };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(3, stage.Calls);
        Assert.Equal(new string?[] { null, "dotnet test failed on attempt 1", "dotnet test failed on attempt 2" }, stage.FeedbackSeen);
        Assert.Equal("dotnet test failed on attempt 3", result.StageStatuses["implement"].Reason);
        var events = await Events(result.RunId);
        Assert.Equal(3, events.Count(e => e == "attempt_failed"));
        Assert.Equal(2, events.Count(e => e == "retry_scheduled"));
    }

    // A live Coder followed a design that listed test files and the run stopped; a write outside the role's
    // folders now gets one retry with the denial as feedback. Nothing was written, so retrying is safe.
    private const string Outside = "Outside this agent's autonomy boundary (src/UrlShortener.*): tests/UrlShortener.UnitTests/LinkServiceTests.cs.";

    [Fact]
    public async Task A_write_outside_the_roles_folders_is_retried_once_with_the_denial_as_feedback()
    {
        var calls = 0;
        var stage = new Stage("implement") { Exit = _ => ++calls == 1 ? throw new PolicyViolationException(Outside, correctable: true) : GateResult.Pass("green") };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(new string?[] { null, Outside }, stage.FeedbackSeen);
        Assert.Equal(1, result.StageStatuses["implement"].BoundaryCorrections);
        var events = await Events(result.RunId);
        Assert.Equal(new[] { "policy_denied", "retry_scheduled" }, events.SkipWhile(e => e != "policy_denied").Take(2));
        Assert.DoesNotContain("stage_failed", events);
    }

    [Fact]
    public async Task A_second_boundary_violation_or_any_other_denial_stops_the_stage()
    {
        var repeated = new Stage("implement") { Exit = _ => throw new PolicyViolationException(Outside, correctable: true) };
        var result = await Engine().StartAsync(Plan(repeated), "Build", [repeated]);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(2, repeated.Calls); // one correction, then stop: no third attempt

        var secret = new Stage("docs") { Exit = _ => throw new PolicyViolationException("Denied by policy: SecretScan: a key-like value") };
        var stopped = await Engine().StartAsync(Plan(secret), "Build", [secret]);
        Assert.Equal(RunStatus.Failed, stopped.Status);
        Assert.Equal(1, secret.Calls);
        Assert.Equal(0, stopped.StageStatuses["docs"].BoundaryCorrections);
    }

    [Fact]
    public async Task A_transient_failure_succeeds_on_retry()
    {
        var calls = 0;
        var stage = new Stage("tests") { Exit = _ => ++calls == 1 ? GateResult.Block("flaky") : GateResult.Pass("green") };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, result.StageStatuses["tests"].Attempts);
        Assert.Null(result.StageStatuses["tests"].LastFailure);
    }

    [Fact]
    public async Task Fallback_tries_a_simpler_prompt_then_a_template_after_retries()
    {
        var stage = new FallbackStage("docs", templateWorks: true) { Body = (_, _) => throw new InvalidOperationException("model output invalid") };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, stage.Calls);
        Assert.Equal(new[] { FallbackLevel.SimplerPrompt, FallbackLevel.Template }, stage.Levels);
        Assert.Equal(FallbackLevel.Template, result.StageStatuses["docs"].FallbackLevel);
        Assert.Equal("docs template", result.Artifacts["docs"].Content);
        Assert.Equal(2, (await Events(result.RunId)).Count(e => e == "fallback_started"));
    }

    [Fact]
    public async Task Stage_fails_when_every_fallback_fails()
    {
        var stage = new FallbackStage("implement", templateWorks: false) { Body = (_, _) => throw new InvalidOperationException("broken") };
        var result = await Engine().StartAsync(Plan(stage), "Build", [stage]);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(5, result.StageStatuses["implement"].Attempts);
        Assert.Contains("Template failed too", result.StageStatuses["implement"].Reason);
    }

    [Fact]
    public async Task Token_budget_exceeded_pauses_the_run_cleanly_without_retrying()
    {
        var first = new Stage("first") { Body = (ctx, _) => { ctx.Budget.AddTokens(ctx.Budget.MaxTokens + 1); throw new BudgetExceededException("Run token budget exceeded."); } };
        var next = new Stage("next", "first");
        var result = await Engine().StartAsync(Plan(first, next), "Build", [first, next]);

        Assert.Equal(RunStatus.Paused, result.Status);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, next.Calls);
        Assert.Equal(StageStatus.Interrupted, result.StageStatuses["first"].Status);
        Assert.Equal("Run token budget exceeded.", result.StopReason);
        var saved = (await store.LoadAsync(result.RunId))!;
        Assert.Equal(RunStatus.Paused, saved.Status);
        var last = JsonDocument.Parse((await File.ReadAllLinesAsync(audit.GetPath(result.RunId)))[^1]).RootElement;
        Assert.Equal("run_paused", last.GetProperty("event").GetString());
        Assert.Equal("budget_exceeded", last.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Time_budget_stops_a_long_stage_and_saves_a_resumable_checkpoint()
    {
        var slow = new Stage("slow") { Body = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var plan = Plan(slow);
        plan.Budget = new() { MaxMinutes = 0.02 }; // about 1.2 s: long enough to reach the stage even under load
        var result = await Engine().StartAsync(plan, "Build", [slow]);

        Assert.Equal(RunStatus.Paused, result.Status);
        Assert.Contains("Time budget", result.StopReason);
        Assert.Equal(StageStatus.Interrupted, result.StageStatuses["slow"].Status);
        var saved = (await store.LoadAsync(result.RunId))!;
        saved.Budget.MaxMinutes = 5; // the operator raises the budget, then resumes
        await store.SaveAsync(saved);
        var resumed = await Engine().ResumeAsync(result.RunId, [new Stage("slow")]);
        Assert.Equal(RunStatus.Completed, resumed.Status);
    }

    [Fact]
    public async Task Failed_stage_rolls_back_to_the_last_good_commit_on_the_run_branch()
    {
        var repoPath = Path.Combine(directory, "repo");
        var initial = GitWorkspaceTests.InitRepo(repoPath, ("docs/guide.md", "v0"), ("README.md", "user file"));
        var git = new GitWorkspace(repoPath, ["docs"]);
        var good = new Stage("good") { Body = (_, _) => File.WriteAllTextAsync(Path.Combine(repoPath, "docs/guide.md"), "v1") };
        var bad = new Stage("bad", "good")
        {
            Body = async (_, _) =>
            {
                await File.WriteAllTextAsync(Path.Combine(repoPath, "docs/guide.md"), "broken");
                await File.WriteAllTextAsync(Path.Combine(repoPath, "docs/new.md"), "partial");
            },
            Exit = _ => GateResult.Block("dotnet build failed")
        };
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "user edit outside agent paths");
        var result = await Engine(git).StartAsync(Plan(good, bad), "Build", [good, bad]);

        Assert.Equal(RunStatus.Failed, result.Status);
        using var repo = new Repository(repoPath);
        Assert.Equal(GitWorkspace.BranchName(result.RunId), repo.Head.FriendlyName);
        Assert.Equal(result.StageStatuses["good"].CommitSha, repo.Head.Tip.Sha);
        Assert.Equal(initial, repo.Head.Tip.Parents.Single().Sha);
        Assert.Contains($"run {result.RunId:D}", repo.Head.Tip.Message);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(repoPath, "docs/guide.md")));
        Assert.False(File.Exists(Path.Combine(repoPath, "docs/new.md")));
        Assert.Equal("user edit outside agent paths", await File.ReadAllTextAsync(Path.Combine(repoPath, "README.md")));
        var events = await Events(result.RunId);
        Assert.Equal(3, events.Count(e => e == "rollback")); // each failed gate restored the agent paths
    }

    public void Dispose()
    {
        audit.Dispose();
        GitWorkspaceTests.DeleteDirectory(directory);
    }
}
