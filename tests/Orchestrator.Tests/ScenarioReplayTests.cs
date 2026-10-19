using System.Text.Json;
using Orchestrator.Agents;
using Orchestrator.Core;
using Orchestrator.Policies;
using Xunit;

namespace Orchestrator.Tests;

// Brownfield and ambiguous scenarios replayed through the real engine, stages, agents and policies.
// Build/test gates are doubles here; the CLI runs them for real (see TESTING.md).
public sealed class ScenarioReplayTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "scenario-" + Guid.NewGuid().ToString("N"));
    private string Runs => Path.Combine(root, "runs");
    private static string Recordings => Path.Combine(AppContext.BaseDirectory, "scenarios", "recordings");

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "UrlShortenerAgentic.sln"))) return dir.FullName;
        throw new InvalidOperationException("Solution root not found.");
    }

    private sealed class Validation : IValidationRunner
    {
        public readonly List<string> Logs = [];
        public Func<string, GateResult?>? Override;
        public Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct)
        {
            Logs.Add(logName);
            return Task.FromResult(Override?.Invoke(logName) ?? GateResult.Pass("test gate"));
        }
    }

    private sealed class Approvals : IApprovalService
    {
        public readonly List<(string Stage, string Reason)> Requests = [];
        public Func<ApprovalRequest, int, Task<bool>>? Decide;
        public async Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct)
        {
            Requests.Add((r.StageId, r.Reason));
            var approved = Decide is null || await Decide(r, Requests.Count(x => x.Stage == r.StageId));
            return new(approved, "test-reviewer", approved ? "ok" : "no");
        }
    }

    private async Task<(Scenario Scenario, IStage[] Stages, JsonlAuditWriter Audit)> Load(string name, Approvals approvals, Validation validation, string? recordings = null)
    {
        var scenario = await new ScenarioLoader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".yaml"));
        scenario.Name = name;
        scenario.Replay = true;
        scenario.Retry.BaseSeconds = 0.001;
        var client = new ReplayChatClient(recordings ?? Recordings, recordings is null ? name : "none");
        var audit = new JsonlAuditWriter(Runs);
        var files = new FileChangeService(root, DefaultPolicies.Create(new FakeScanner()), approvals, audit);
        // Impact analysis reads the real solution; agent file writes go to a temporary workspace.
        var agents = StageFactory.Agents(client, RepositoryRoot());
        return (scenario, scenario.Stages.Select(d => StageFactory.Create(d, agents, files, Runs, validation, audit)).ToArray(), audit);
    }

    private async Task<string[]> Events(JsonlAuditWriter audit, Guid run) => (await File.ReadAllLinesAsync(audit.GetPath(run)))
        .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()!).ToArray();

    [Fact]
    public async Task Brownfield_runs_impact_analysis_asks_for_migration_approval_and_recovers_from_a_failed_migration()
    {
        var approvals = new Approvals();
        var validation = new Validation { Override = log => log == "implement.attempt1" ? GateResult.Block("dotnet test failed: 26 passed, 15 failed; SQLite Error 19: NOT NULL constraint failed: ShortLinks.MaxClicks") : null };
        var (scenario, stages, audit) = await Load("brownfield", approvals, validation);
        using var _ = audit;
        // A real git workspace, so the failed migration attempt is rolled back before the retry.
        GitWorkspaceTests.InitRepo(root, ("README.md", "base"));
        var git = new GitWorkspace(root, DefaultPolicies.AgentRoots);
        var result = await new RunEngine(new(Runs), audit, approvals, versioning: git).StartAsync(scenario, scenario.Requirement, stages);

        Assert.True(result.Status == RunStatus.Completed, result.StopReason);
        var impact = AgentDocument.Parse(result.Artifacts["impact"].Content).Content;
        Assert.Contains("`ShortLinks`: maps entity ShortLink", impact);
        Assert.Contains("`GET /{code}`", impact);
        Assert.Contains("`POST /api/v1/links`", impact);
        Assert.Contains("src/UrlShortener.Application/Links/LinkService.cs", impact);
        Assert.Contains("tests/UrlShortener.IntegrationTests/ApiTests.cs", impact);
        Assert.Equal(2, result.StageStatuses["implement"].Attempts);
        Assert.Contains("nullable: true", await File.ReadAllTextAsync(Path.Combine(root, "src/UrlShortener.Infrastructure/Migrations/20261019090000_AddMaxClicks.cs")));
        var migrationApprovals = approvals.Requests.Where(r => r.Stage == "implement").ToArray();
        Assert.Equal(2, migrationApprovals.Length); // one per attempt: a retried schema change is approved again
        Assert.All(migrationApprovals, r => Assert.Contains("MigrationApproval: EF Core migration", r.Reason));
        Assert.Equal(new[] { "requirements", "impact", "design", "implement", "implement", "release" }, approvals.Requests.Select(r => r.Stage).Where(s => s != "tests" && s != "docs"));
        Assert.True(result.StageStatuses["tests"].StartedAtUtc >= result.StageStatuses["implement"].FinishedAtUtc);
        var events = await Events(audit, result.RunId);
        Assert.Contains("retry_scheduled", events);
        Assert.Single(events, e => e == "rollback");
        var patch = await File.ReadAllTextAsync(Path.Combine(Runs, result.RunId.ToString("D"), "diff.patch"));
        Assert.Contains("+++ b/src/UrlShortener.Infrastructure/Migrations/20261019090000_AddMaxClicks.cs", patch);
        Assert.Contains("+                nullable: true);", patch);
        Assert.DoesNotContain("nullable: false", patch);
        Assert.True(File.Exists(Path.Combine(Runs, result.RunId.ToString("D"), "input.md")));
    }

    private string QuestionsPath(Guid run) => Path.Combine(Runs, run.ToString("D"), "open-questions.md");
    private static async Task AnswerAsync(string path, params (string Id, string Answer)[] answers)
    {
        var questions = OpenQuestions.Parse(await File.ReadAllTextAsync(path));
        var text = OpenQuestions.Render("Make the links more secure and give marketing better analytics.",
            questions.Select(q => q with { Answer = answers.FirstOrDefault(a => a.Id == q.Id).Answer ?? q.Answer }).ToArray());
        await File.WriteAllTextAsync(path, text);
    }

    [Fact]
    public async Task Ambiguous_answers_become_decisions_and_changing_one_replans_downstream()
    {
        var approvals = new Approvals();
        Guid run = default;
        approvals.Decide = async (r, nth) =>
        {
            run = r.RunId;
            if (r.StageId == "clarify" && nth == 1) await AnswerAsync(QuestionsPath(r.RunId), ("Q1", "A"), ("Q2", "b"), ("Q3", "A"));
            if (r.StageId == "design" && nth == 1) await AnswerAsync(QuestionsPath(r.RunId), ("Q1", "C")); // the human changes their mind mid-run
            return true;
        };
        var (scenario, stages, audit) = await Load("ambiguous", approvals, new Validation());
        using var _ = audit;
        var result = await new RunEngine(new(Runs), audit, approvals).StartAsync(scenario, scenario.Requirement, stages);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(new[] { "clarify", "requirements", "design", "clarify", "requirements", "design", "release" }, approvals.Requests.Select(r => r.Stage));
        Assert.Contains("Security scope: API key for link creation", result.Artifacts["requirements"].Content);
        Assert.Contains("(Q1 = C, changed from A during the run)", AgentDocument.Parse(result.Artifacts["docs"].Content).Files.Single().Content);
        var answers = result.Decisions.Where(d => d.StageId == "clarify" && d.Summary.StartsWith("Q")).ToArray();
        Assert.Equal(new[] { "Q1 = A", "Q2 = B", "Q3 = A", "Q1 = C" }, answers.Select(d => d.Summary));
        Assert.Contains(answers[0].Id, answers[3].ParentDecisionIds); // the changed answer descends from the original
        Assert.All(answers, d => Assert.Equal("test-reviewer", d.MadeBy));
        var requirements = result.Decisions.Where(d => d.StageId == "requirements").ToArray();
        Assert.Contains(requirements[0].Id, result.SupersededDecisions);
        Assert.Contains(answers[3].Id, requirements[1].ParentDecisionIds);
        Assert.Equal(1, result.StageStatuses["docs"].Attempts); // docs had not run yet, so it was not stale
        var replan = (await File.ReadAllLinesAsync(audit.GetPath(result.RunId))).Single(l => l.Contains("\"event\":\"replan\""));
        Assert.Contains("Re-running: requirements, design", replan);
    }

    [Fact]
    public async Task Ambiguous_run_pauses_until_every_question_is_answered()
    {
        var approvals = new Approvals();
        var (scenario, stages, audit) = await Load("ambiguous", approvals, new Validation());
        using var _ = audit;
        var engine = new RunEngine(new(Runs), audit, approvals);
        var blocked = await engine.StartAsync(scenario, scenario.Requirement, stages);

        Assert.Equal(RunStatus.Blocked, blocked.Status);
        Assert.Equal("Answer Q1, Q2, Q3 in open-questions.md, then approve (or resume).", blocked.StopReason);
        Assert.Contains("human_input_required", await Events(audit, blocked.RunId));
        Assert.DoesNotContain(blocked.Decisions, d => d.Summary.StartsWith("Q"));

        await AnswerAsync(QuestionsPath(blocked.RunId), ("Q1", "A"), ("Q2", "B"), ("Q3", "A"));
        var resumed = await engine.ResumeAsync(blocked.RunId, stages); // the clarifier is not called again
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Contains("Security scope: target URL screening", resumed.Artifacts["requirements"].Content);
        Assert.Equal(1, (await File.ReadAllLinesAsync(audit.GetPath(blocked.RunId))).Count(l => l.Contains("\"agent_requested\"") && l.Contains("\"stage\":\"clarify\"")));
    }

    public void Dispose() => GitWorkspaceTests.DeleteDirectory(root);
}
