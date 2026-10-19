using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Agents;
using Orchestrator.Cli;
using Orchestrator.Core;
using Orchestrator.Policies;
using Xunit;

namespace Orchestrator.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "workflow-" + Guid.NewGuid().ToString("N"));
    private string Runs => Path.Combine(root, "runs");
    private readonly Validation validation = new();
    private sealed class Validation : IValidationRunner
    {
        public bool Passed = true;
        public int Calls;
        public Func<string, bool>? FailOnce;
        public readonly List<string> Stages = [];
        public readonly List<string> Logs = [];
        public Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct)
        {
            Calls++;
            var stageId = logName.Split('.')[0];
            Logs.Add(logName);
            Stages.Add(stageId);
            if (FailOnce?.Invoke(stageId) == true && Stages.Count(s => s == stageId) == 1)
                return Task.FromResult(GateResult.Block("dotnet test failed: 1 failed; failing: GeneratorProducesTenThousandDistinctSevenCharacterBase62Codes"));
            return Task.FromResult(new GateResult(Passed, "Test validation result"));
        }
    }
    private sealed class Approval : IApprovalService
    {
        public readonly List<string> Stages = [];
        public readonly List<string> Logs = [];
        public bool Approved = true;
        public bool Edit;
        public Func<ApprovalRequest, Task>? Before;
        public async Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct)
        {
            Stages.Add(r.StageId);
            if (Before is not null) await Before(r);
            Assert.NotNull(r.ArtifactPath);
            var doc = AgentDocument.Parse(await File.ReadAllTextAsync(r.ArtifactPath!, ct));
            if (Edit) await File.WriteAllTextAsync(r.ArtifactPath!, (doc with { Content = doc.Content + "\nHuman clarification" }).ToJson(), ct);
            return new(Approved, "test-reviewer", "Test approval");
        }
    }
    private static async Task<Scenario> Scenario()
    {
        var scenario = await new ScenarioLoader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", "greenfield.yaml"));
        scenario.Replay = true;
        scenario.Retry.BaseSeconds = 0.001;
        return scenario;
    }
    private IStage[] Stages(Scenario scenario, IChatClient client, IApprovalService approval, JsonlAuditWriter audit)
    {
        IAgent[] agents = [new RequirementsAgent(client), new ArchitectAgent(client), new CoderAgent(client), new TesterAgent(client), new DocsAgent(client), new ReviewerAgent(client), new ReleaseAgent(client)];
        // The real policy set, so the recordings are proven to pass the guardrails.
        var files = new FileChangeService(root, DefaultPolicies.Create(new FakeScanner()), approval, audit);
        return scenario.Stages.Select(d => (IStage)new AgentStage(d, agents.Single(a => a.Role == d.Agent), files, Runs, validation, audit)).ToArray();
    }
    [Fact]
    public async Task Full_replay_requires_three_reviews_writes_files_and_preserves_lineage()
    {
        var scenario = await Scenario();
        using var client = new ReplayChatClient(Path.Combine(AppContext.BaseDirectory, "scenarios", "recordings"), "greenfield");
        using var audit = new JsonlAuditWriter(Runs);
        var approval = new Approval { Edit = true };
        var store = new SqliteRunStateStore(Runs);
        var context = await new RunEngine(store, audit, approval).StartAsync(scenario, scenario.Requirement, Stages(scenario, client, approval, audit));
        Assert.Equal(RunStatus.Completed, context.Status);
        Assert.Equal(["requirements", "design", "release"], approval.Stages);
        Assert.Contains("Human clarification", context.Artifacts["requirements"].Content);
        Assert.Contains(context.Decisions, d => d.MadeBy == "test-reviewer");
        Assert.True(File.Exists(Path.Combine(root, "src/UrlShortener.Domain/Base62CodeGenerator.cs")));
        Assert.True(File.Exists(Path.Combine(root, "tests/UrlShortener.UnitTests/DomainTests.cs")));
        Assert.True(File.Exists(Path.Combine(root, "docs/generated/greenfield.md")));
        Assert.Equal(new[] { "implement", "tests", "validate" }, validation.Stages.Order());
        Assert.Contains("tests.attempt1", validation.Logs);
        Assert.Equal(3, validation.Calls); // implement and tests change C# files; validate always builds and tests
        Assert.True((await store.LoadAsync(context.RunId))!.Scenario.Replay);
        var report = await RunReport.WriteAsync(context, Runs);
        Assert.Contains("Completed", await File.ReadAllTextAsync(report));
        var lines = await File.ReadAllLinesAsync(audit.GetPath(context.RunId));
        Assert.Contains(lines, l => l.Contains("agent_requested") && !l.Contains("\"promptHash\":null"));
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
    }
    [Fact]
    public async Task Rejected_draft_resumes_without_calling_the_model_again()
    {
        var scenario = await Scenario();
        scenario.Stages = [scenario.Stages[0]];
        using var client = new ReplayChatClient(Path.Combine(AppContext.BaseDirectory, "scenarios", "recordings"), "greenfield");
        using var audit = new JsonlAuditWriter(Runs);
        var approval = new Approval { Approved = false };
        var store = new SqliteRunStateStore(Runs);
        var engine = new RunEngine(store, audit, approval);
        var context = await engine.StartAsync(scenario, scenario.Requirement, Stages(scenario, client, approval, audit));
        Assert.Equal(RunStatus.Blocked, context.Status);
        Assert.Empty(context.Artifacts);
        approval.Approved = true;
        using var missingClient = new ReplayChatClient(root, "no-recordings");
        context = await engine.ResumeAsync(context.RunId, Stages(scenario, missingClient, approval, audit));
        Assert.Equal(RunStatus.Completed, context.Status);
        Assert.Equal(2, context.StageStatuses["requirements"].Attempts);
    }
    [Fact]
    public async Task Failed_real_gate_blocks_release()
    {
        var scenario = await Scenario();
        using var client = new ReplayChatClient(Path.Combine(AppContext.BaseDirectory, "scenarios", "recordings"), "greenfield");
        using var audit = new JsonlAuditWriter(Runs);
        var approval = new Approval();
        validation.Passed = false;
        var context = await new RunEngine(new(Runs), audit, approval).StartAsync(scenario, scenario.Requirement, Stages(scenario, client, approval, audit));
        Assert.Equal(RunStatus.Failed, context.Status);
        Assert.Equal(StageStatus.Failed, context.StageStatuses["implement"].Status);
        Assert.Equal(StageStatus.Pending, context.StageStatuses["validate"].Status);
        Assert.Equal(StageStatus.Pending, context.StageStatuses["release"].Status);
        Assert.DoesNotContain("release", approval.Stages);
    }
    [Fact]
    public async Task Tester_failure_is_retried_and_uses_the_next_recorded_attempt()
    {
        var scenario = await Scenario();
        using var client = new ReplayChatClient(Path.Combine(AppContext.BaseDirectory, "scenarios", "recordings"), "greenfield");
        using var audit = new JsonlAuditWriter(Runs);
        var approval = new Approval();
        validation.FailOnce = stage => stage == "tests";
        var context = await new RunEngine(new(Runs), audit, approval).StartAsync(scenario, scenario.Requirement, Stages(scenario, client, approval, audit));
        Assert.Equal(RunStatus.Completed, context.Status);
        Assert.Equal(2, context.StageStatuses["tests"].Attempts);
        Assert.Equal(1, context.StageStatuses["implement"].Attempts);
        Assert.Contains("Assert.Equal(7, code.Length);", await File.ReadAllTextAsync(Path.Combine(root, "tests/UrlShortener.UnitTests/DomainTests.cs")));
        var lines = await File.ReadAllLinesAsync(audit.GetPath(context.RunId));
        Assert.Contains(lines, l => l.Contains("\"attempt_failed\"") && l.Contains("\"stage\":\"tests\""));
        Assert.Single(lines, l => l.Contains("\"retry_scheduled\""));
    }
    private string DesignPath(Guid runId) => Path.Combine(Runs, runId.ToString("D"), "design.json");
    private static async Task EditDesignAsync(string path, string note)
    {
        var design = AgentDocument.Parse(await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, (design with { Content = design.Content + "\n" + note }).ToJson());
    }

    [Fact]
    public async Task Editing_design_at_release_replans_only_downstream_stages()
    {
        var scenario = await Scenario();
        using var client = new ReplayChatClient(Path.Combine(AppContext.BaseDirectory, "scenarios", "recordings"), "greenfield");
        using var audit = new JsonlAuditWriter(Runs);
        var approval = new Approval();
        var edited = false;
        approval.Before = async r =>
        {
            if (r.StageId != "release" || edited) return;
            edited = true;
            await EditDesignAsync(DesignPath(r.RunId), "Human change: add an expiry index.");
        };
        var context = await new RunEngine(new(Runs), audit, approval).StartAsync(scenario, scenario.Requirement, Stages(scenario, client, approval, audit));

        Assert.Equal(RunStatus.Completed, context.Status);
        Assert.Equal(1, context.StageStatuses["requirements"].Attempts);
        Assert.Equal(2, context.StageStatuses["design"].Attempts); // re-landed the edited draft
        foreach (var id in new[] { "implement", "tests", "docs", "validate", "release" })
            Assert.Equal(2, context.StageStatuses[id].Attempts);
        Assert.Equal(new[] { "requirements", "design", "release", "design", "release" }, approval.Stages);
        Assert.Contains("add an expiry index", context.Artifacts["design"].Content);

        var edit = context.Decisions.Single(d => d.StageId == "design" && d.Rationale.StartsWith("Human-edited"));
        var implement = context.Decisions.Where(d => d.StageId == "implement").ToArray();
        Assert.Equal(2, implement.Length);
        Assert.Contains(implement[0].Id, context.SupersededDecisions);
        Assert.Equal(new[] { edit.Id }, implement[1].ParentDecisionIds); // newest design decision only
        Assert.StartsWith("human-editor", edit.MadeBy);
        Assert.DoesNotContain(context.Decisions.Single(d => d.StageId == "requirements").Id, context.SupersededDecisions);
        var lines = await File.ReadAllLinesAsync(audit.GetPath(context.RunId));
        var replan = Assert.Single(lines, l => l.Contains("\"event\":\"replan\""));
        Assert.Contains("Re-running: implement, tests, docs, validate, release", replan);
    }

    [Fact]
    public async Task Resuming_after_an_artifact_edit_reruns_downstream_and_an_invalid_edit_blocks()
    {
        var scenario = await Scenario();
        using var client = new ReplayChatClient(Path.Combine(AppContext.BaseDirectory, "scenarios", "recordings"), "greenfield");
        using var audit = new JsonlAuditWriter(Runs);
        var approval = new Approval();
        var engine = new RunEngine(new(Runs), audit, approval);
        var first = await engine.StartAsync(scenario, scenario.Requirement, Stages(scenario, client, approval, audit));
        Assert.Equal(RunStatus.Completed, first.Status);
        var unchanged = await engine.ResumeAsync(first.RunId, Stages(scenario, client, approval, audit));
        Assert.Equal(1, unchanged.StageStatuses["implement"].Attempts); // nothing edited: resume is a no-op

        await File.WriteAllTextAsync(DesignPath(first.RunId), "{ not json");
        var blocked = await engine.ResumeAsync(first.RunId, Stages(scenario, client, approval, audit));
        Assert.Equal(RunStatus.Blocked, blocked.Status);
        Assert.Contains("Edited artifact for stage 'design' is invalid", blocked.StopReason);

        await File.WriteAllTextAsync(DesignPath(first.RunId), first.Artifacts["design"].Content);
        await EditDesignAsync(DesignPath(first.RunId), "Resume-time change.");
        var replanned = await engine.ResumeAsync(first.RunId, Stages(scenario, client, approval, audit));
        Assert.Equal(RunStatus.Completed, replanned.Status);
        Assert.Equal(1, replanned.StageStatuses["requirements"].Attempts);
        Assert.Equal(2, replanned.StageStatuses["validate"].Attempts);
        Assert.Contains("Resume-time change.", replanned.Artifacts["design"].Content);
    }

    [Fact]
    public void Replay_registration_needs_no_credentials()
    {
        using var provider = new ServiceCollection().AddScenarioChatClient(new() { Replay = true, Name = "greenfield" }, root, new ConfigurationBuilder().Build()).BuildServiceProvider();
        Assert.IsType<ReplayChatClient>(provider.GetRequiredService<IChatClient>());
    }
    [Theory]
    [InlineData("approve\n", true)][InlineData("reject\n", false)][InlineData("", false)]
    public async Task Console_requires_explicit_approval(string input, bool approved)
    {
        var service = new ConsoleApprovalService(false, new StringReader(input));
        Assert.Equal(approved, (await service.RequestAsync(new(Guid.NewGuid(), "requirements", RiskLevel.High, "Review"), default)).Approved);
    }
    [Fact]
    public async Task Console_edit_opens_exact_artifact_then_requires_approval()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "draft.json");
        await File.WriteAllTextAsync(path, "{}");
        string? opened = null;
        var service = new ConsoleApprovalService(false, new StringReader("edit\napprove\n"), p => opened = p);
        Assert.True((await service.RequestAsync(new(Guid.NewGuid(), "design", RiskLevel.High, "Review", path), default)).Approved);
        Assert.Equal(path, opened);
    }
    [Fact]
    public async Task Cancelled_console_prompt_does_not_approve()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var service = new ConsoleApprovalService(false, new StringReader("approve\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RequestAsync(new(Guid.NewGuid(), "design", RiskLevel.High, "Review"), stop.Token));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
