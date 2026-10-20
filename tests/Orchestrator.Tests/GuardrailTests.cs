using Orchestrator.Agents;
using Orchestrator.Core;
using Orchestrator.Policies;
using Xunit;

namespace Orchestrator.Tests;

// End-to-end guardrails: real policies, a real FileChangeService, the real engine and replayed agents.
public sealed class GuardrailTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "guardrails-" + Guid.NewGuid().ToString("N"));
    private static readonly string FakeKey = "sk-" + new string('q', 40);
    private string Runs => Path.Combine(root, "runs");

    private sealed class Approval(bool approved) : IApprovalService
    {
        public readonly List<string> Reasons = [];
        public Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct) { Reasons.Add(r.Reason); return Task.FromResult(new ApprovalResult(approved, "test-reviewer", approved ? "ok" : "no")); }
    }
    private sealed class NoValidation : IValidationRunner
    {
        public Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct) => Task.FromResult(GateResult.Pass());
    }

    private FileChangeService Files(IApprovalService approval, JsonlAuditWriter? audit = null) =>
        new(root, DefaultPolicies.Create(new FakeScanner()), approval, audit);

    [Fact]
    public async Task Agent_cannot_write_to_github_even_if_its_boundary_were_misconfigured()
    {
        var violation = await Assert.ThrowsAsync<PolicyViolationException>(() => Files(new Approval(true))
            .ApplyAsync([new(".github/workflows/deploy.yml", "on: push")], new(["*"], [], 100), new RunContext(), "docs", default));
        Assert.Contains("PathAllowlist", violation.Message);
        Assert.False(violation.Correctable); // outside every agent path: a hard stop, never retried
        Assert.False(File.Exists(Path.Combine(root, ".github/workflows/deploy.yml")));
    }

    [Fact]
    public async Task A_file_in_another_roles_folder_is_a_correctable_denial_that_names_the_allowed_folders()
    {
        var violation = await Assert.ThrowsAsync<PolicyViolationException>(() => Files(new Approval(true)).ApplyAsync(
            [new("src/UrlShortener.Domain/Url.cs", "class Url { }"), new("tests/UrlShortener.UnitTests/UrlTests.cs", "class UrlTests { }")],
            new(["src/UrlShortener.*"], [], 100), new RunContext(), "implement", default));
        Assert.True(violation.Correctable);
        Assert.Equal("Outside this agent's autonomy boundary (src/UrlShortener.*): tests/UrlShortener.UnitTests/UrlTests.cs. "
            + "Return only files under src/UrlShortener.*; other roles write the rest.", violation.Message);
        Assert.False(File.Exists(Path.Combine(root, "src/UrlShortener.Domain/Url.cs"))); // nothing of the change set is written
    }

    [Fact]
    public void Design_and_coder_prompts_assign_each_folder_to_one_role()
    {
        // A live design listed test files among the Coder's tasks, and the Coder wrote them.
        using var client = new ReplayChatClient(root, "unused");
        Assert.Contains("the Coder writes only src/UrlShortener.*, the Tester only tests/UrlShortener.*, the Docs agent only docs/", new ArchitectAgent(client).RolePrompt);
        Assert.Contains("leave any design item for tests/ or docs/ to them", new CoderAgent(client).RolePrompt);
    }

    [Fact]
    public async Task Docs_stage_that_commits_a_fake_api_key_is_denied_stopped_without_retry_and_audited_without_the_key()
    {
        var recordings = Path.Combine(root, "recordings");
        Directory.CreateDirectory(Path.Combine(recordings, "leak"));
        await File.WriteAllTextAsync(Path.Combine(recordings, "leak", "docs.json"), new AgentDocument("Setup guide", "Explain configuration.",
            "# Docs", [new("docs/setup.md", $"# Setup\n\nexport LLM__APIKEY={FakeKey}\n")]).ToJson());
        using var audit = new JsonlAuditWriter(Runs);
        using var client = new ReplayChatClient(recordings, "leak");
        var approval = new Approval(true);
        var definition = new StageDefinition { Id = "docs", Agent = "Docs", Risk = RiskLevel.Low };
        var scenario = new Scenario { Name = "leak", Retry = new() { BaseSeconds = 0.001 }, Stages = [definition] };
        var stage = new AgentStage(definition, new DocsAgent(client), Files(approval, audit), Runs, new NoValidation(), audit);

        var result = await new RunEngine(new(Runs), audit, approval).StartAsync(scenario, "Document setup", [stage]);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(1, result.StageStatuses["docs"].Attempts); // a denial is not retried
        Assert.Contains("SecretScan", result.StageStatuses["docs"].Reason);
        Assert.False(File.Exists(Path.Combine(root, "docs/setup.md")));
        var log = await File.ReadAllTextAsync(audit.GetPath(result.RunId));
        Assert.Contains("\"event\":\"policy_check\"", log);
        Assert.Contains("\"event\":\"policy_denied\"", log);
        Assert.DoesNotContain(FakeKey, log);
        Assert.Empty(approval.Reasons);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Require_approval_shows_reasons_and_diff_and_rejection_writes_nothing(bool approved)
    {
        var approval = new Approval(approved);
        var big = string.Concat(Enumerable.Range(0, 450).Select(i => $"line {i}\n"));
        var apply = () => Files(approval).ApplyAsync([new("docs/big.md", big)], new(["docs"], [], 100), new RunContext(), "docs", default);
        if (approved) await apply();
        else Assert.True((await Assert.ThrowsAsync<PolicyViolationException>(apply)).RejectedByHuman);
        var reason = Assert.Single(approval.Reasons);
        Assert.Contains("DiffSize: 450 changed lines exceed 400", reason);
        Assert.Contains("+++ b/docs/big.md", reason);
        Assert.Equal(approved, File.Exists(Path.Combine(root, "docs/big.md")));
    }

    [Fact]
    public async Task New_package_in_a_project_file_is_detected_and_needs_approval()
    {
        Directory.CreateDirectory(Path.Combine(root, "src/UrlShortener.Api"));
        const string before = """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="Serilog.AspNetCore" Version="8.0.0" /></ItemGroup></Project>""";
        await File.WriteAllTextAsync(Path.Combine(root, "src/UrlShortener.Api/UrlShortener.Api.csproj"), before);
        var after = before.Replace("</ItemGroup>", """<PackageReference Include="FluentValidation" Version="11.9.0" /></ItemGroup>""");
        var approval = new Approval(true);
        await Files(approval).ApplyAsync([new("src/UrlShortener.Api/UrlShortener.Api.csproj", after)], new(["src/UrlShortener.*"], [], 100), new RunContext(), "implement", default);
        var reason = Assert.Single(approval.Reasons);
        Assert.Contains("NuGetAllowlist: new allowlisted packages need approval: FluentValidation@11.9.0", reason);
        Assert.DoesNotContain("Serilog.AspNetCore@", reason);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
