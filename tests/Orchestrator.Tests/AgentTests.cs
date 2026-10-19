using Microsoft.Extensions.AI;
using Orchestrator.Agents;
using Orchestrator.Core;
using Orchestrator.Policies;
using Xunit;

namespace Orchestrator.Tests;

public sealed class AgentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agents-" + Guid.NewGuid().ToString("N"));
    public AgentTests() => Directory.CreateDirectory(Path.Combine(root, "demo"));
    private Task Recording(string json) => File.WriteAllTextAsync(Path.Combine(root, "demo", "stage.json"), json);
    private static AgentDocument Document(params FileChange[] files) => new("Summary", "Because constraints matter", "# Artifact", files);
    private static RunContext Context() => new() { Requirement = "Shorten links", Scenario = new() { Stages = [new() { Id = "stage", Agent = "Requirements", DependsOn = ["upstream"] }] },
        Decisions = [new("parent", "upstream", "Approved", "Reviewed", [], "human", DateTime.UtcNow)] };
    private sealed class Approval(bool approved = true) : IApprovalService
    {
        public int Calls;
        public Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct) { Calls++; return Task.FromResult(new ApprovalResult(approved, "test", "Test decision")); }
    }
    private sealed class Policy(PolicyVerdict verdict) : IPolicy
    {
        public string Name => "Test";
        public PolicyVerdict Evaluate(ProposedChange change, RunContext ctx) => verdict;
    }
    [Theory]
    [InlineData("Requirements")][InlineData("Architect")][InlineData("Coder")][InlineData("Tester")][InlineData("Docs")][InlineData("Reviewer")][InlineData("Release")]
    public async Task Roles_return_structured_artifacts_and_parent_decisions(string role)
    {
        await Recording(Document().ToJson());
        using var client = new ReplayChatClient(root, "demo");
        SdlcAgent agent = role switch { "Requirements" => new RequirementsAgent(client), "Architect" => new ArchitectAgent(client),
            "Coder" => new CoderAgent(client), "Tester" => new TesterAgent(client), "Docs" => new DocsAgent(client), "Reviewer" => new ReviewerAgent(client), _ => new ReleaseAgent(client) };
        var result = await agent.RunAsync(new("stage", "Work", []), Context(), default);
        Assert.True(result.Success);
        Assert.Equal(role, result.Output!.Decisions.Single().MadeBy);
        Assert.Equal(["parent"], result.Output.Decisions.Single().ParentDecisionIds);
        Assert.Equal("Because constraints matter", AgentDocument.Parse(result.Output.Artifact.Content).Rationale);
        Assert.True(agent.Boundary.MaxTokens > 0);
        Assert.False(string.IsNullOrWhiteSpace(agent.RolePrompt));
    }
    [Fact]
    public async Task Replay_is_repeatable_and_supports_buffered_streaming()
    {
        await Recording(Document().ToJson());
        using var client = new ReplayChatClient(root, "demo");
        var options = new ChatOptions { AdditionalProperties = new() { ["stageId"] = "stage" } };
        var first = await client.GetResponseAsync([], options);
        Assert.Equal(first.Text, (await client.GetResponseAsync([], options)).Text);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([], options)) updates.Add(update);
        Assert.Equal(first.Text, updates.ToChatResponse().Text);
        Assert.Same(client, client.GetService(typeof(IChatClient)));
    }
    [Fact]
    public async Task Missing_recording_fails_without_fallback()
    {
        using var client = new ReplayChatClient(root, "demo");
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.GetResponseAsync([], new() { AdditionalProperties = new() { ["stageId"] = "absent" } }));
        Assert.Throws<ArgumentException>(() => new ReplayChatClient(root, "../secret"));
    }
    [Theory]
    [InlineData("{}")] [InlineData("null")] [InlineData("{\"summary\":\"x\",\"rationale\":\"y\",\"content\":\"z\",\"files\":[],\"extra\":true}")]
    public async Task Invalid_schema_cannot_reach_agent_output(string json)
    {
        await Recording(json);
        using var client = new ReplayChatClient(root, "demo");
        await Assert.ThrowsAnyAsync<Exception>(() => new RequirementsAgent(client).RunAsync(new("stage", "", []), Context(), default));
    }
    [Fact]
    public async Task Readonly_agent_cannot_propose_writes()
    {
        await Recording(Document(new FileChange("docs/x.md", "bad")).ToJson());
        using var client = new ReplayChatClient(root, "demo");
        Assert.False((await new RequirementsAgent(client).RunAsync(new("stage", "", []), Context(), default)).Success);
    }
    [Theory]
    [InlineData("../outside")][InlineData("docs/../secret")][InlineData("/tmp/secret")][InlineData("docs\\secret")]
    public async Task Malformed_paths_are_rejected_even_with_allow_all_policy(string path)
    {
        var files = new FileChangeService(root, [new AllowAllPolicy()], new Approval());
        await Assert.ThrowsAsync<InvalidDataException>(() => files.ApplyAsync([new(path, "bad")], new(["docs"], [], 100), Context(), "stage", default));
    }
    [Theory]
    [InlineData("src/Orchestrator.Core/x.cs")][InlineData(".github/workflows/ci.yml")]
    public async Task Role_boundary_violations_are_policy_violations_even_with_allow_all_policy(string path)
    {
        var files = new FileChangeService(root, [new AllowAllPolicy()], new Approval());
        await Assert.ThrowsAsync<PolicyViolationException>(() => files.ApplyAsync([new(path, "bad")], new(["docs"], [], 100), Context(), "stage", default));
        Assert.False(File.Exists(Path.Combine(root, path)));
    }
    [Fact]
    public async Task Policies_check_whole_batch_before_any_write()
    {
        var files = new FileChangeService(root, [new Policy(PolicyVerdict.Deny)], new Approval());
        await Assert.ThrowsAsync<PolicyViolationException>(() => files.ApplyAsync([new("docs/test.md", "text")], new(["docs"], [], 100), Context(), "stage", default));
        Assert.False(File.Exists(Path.Combine(root, "docs/test.md")));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RequireApproval_is_honored(bool approved)
    {
        var approval = new Approval(approved);
        var files = new FileChangeService(root, [new Policy(PolicyVerdict.RequireApproval)], approval);
        var action = () => files.ApplyAsync([new("docs/test.md", "text")], new(["docs"], [], 100), Context(), "stage", default);
        if (approved) await action(); else Assert.True((await Assert.ThrowsAsync<PolicyViolationException>(action)).RejectedByHuman);
        Assert.Equal(approved, File.Exists(Path.Combine(root, "docs/test.md")));
        Assert.Equal(1, approval.Calls);
    }
    [Fact]
    public async Task Symlinks_cannot_escape_workspace()
    {
        Directory.CreateSymbolicLink(Path.Combine(root, "docs"), Path.GetTempPath());
        var files = new FileChangeService(root, [new AllowAllPolicy()], new Approval());
        await Assert.ThrowsAsync<InvalidDataException>(() => files.ApplyAsync([new("docs/escape.md", "bad")], new(["docs"], [], 100), Context(), "stage", default));
        Directory.Delete(Path.Combine(root, "docs"));
    }
    [Fact]
    public async Task Replay_prefers_variant_then_attempt_then_base_recording()
    {
        await Recording(Document().ToJson());
        await File.WriteAllTextAsync(Path.Combine(root, "demo", "stage.attempt1.json"), (Document() with { Summary = "First attempt" }).ToJson());
        await File.WriteAllTextAsync(Path.Combine(root, "demo", "stage.simple.json"), (Document() with { Summary = "Simplified" }).ToJson());
        using var client = new ReplayChatClient(root, "demo");
        async Task<string> Summary(int attempt, string? variant = null)
        {
            var options = new ChatOptions { AdditionalProperties = new() { ["stageId"] = "stage", ["attempt"] = attempt } };
            if (variant is not null) options.AdditionalProperties["variant"] = variant;
            return AgentDocument.Parse((await client.GetResponseAsync([], options)).Text).Summary;
        }
        Assert.Equal("First attempt", await Summary(1));
        Assert.Equal("Summary", await Summary(2));
        Assert.Equal("Simplified", await Summary(4, "simple"));
        await Assert.ThrowsAsync<ArgumentException>(() => Summary(1, "../x"));
    }
    private sealed class CapturingClient : BufferedChatClient
    {
        public readonly List<string> Inputs = [];
        public readonly List<ChatOptions?> Options = [];
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Inputs.Add(string.Join("\n", messages.Select(m => m.Text)));
            Options.Add(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Document().ToJson())));
        }
    }
    private AgentStage StageFor(IAgent agent, string id = "stage") => new(new() { Id = id, Agent = agent.Role, DependsOn = ["upstream"] }, agent,
        new FileChangeService(root, [new AllowAllPolicy()], new Approval()), Path.Combine(root, "runs"), new NoValidation(), new JsonlAuditWriter(Path.Combine(root, "runs")));
    private sealed class NoValidation : IValidationRunner
    {
        public Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct) => Task.FromResult(GateResult.Pass());
    }
    private static RunContext StageContext(string agent = "Requirements")
    {
        var ctx = Context();
        ctx.Scenario.Stages[0].Agent = agent;
        ctx.Artifacts["upstream"] = new Artifact("upstream.md", "approved");
        ctx.StageStatuses["upstream"] = new() { Status = StageStatus.Completed, Reason = "ok" };
        ctx.StageStatuses["stage"] = new() { Attempts = 2, LastFailure = "dotnet test failed: 1 failed; failing: DomainTests.Generator" };
        return ctx;
    }
    [Fact]
    public async Task Retry_prompt_carries_the_previous_failure_and_attempt_number()
    {
        using var activity = OrchestratorTelemetry.Source.StartActivity("agent-test");
        var client = new CapturingClient();
        var ctx = StageContext();
        await StageFor(new RequirementsAgent(client)).ExecuteAsync(ctx, default);
        Assert.Contains("Fix this failure: dotnet test failed: 1 failed; failing: DomainTests.Generator", client.Inputs.Single());
        Assert.Equal(2, client.Options.Single()!.AdditionalProperties!["attempt"]);

        await StageFor(new RequirementsAgent(client)).ExecuteFallbackAsync(ctx, FallbackLevel.SimplerPrompt, default);
        Assert.Equal("simple", client.Options[^1]!.AdditionalProperties!["variant"]);
        Assert.Contains("smallest correct result", client.Inputs[^1]);
    }
    [Fact]
    public async Task Template_fallback_is_deterministic_and_code_roles_have_none()
    {
        using var activity = OrchestratorTelemetry.Source.StartActivity("agent-test");
        var client = new CapturingClient();
        var docs = await StageFor(new DocsAgent(client)).ExecuteFallbackAsync(StageContext("Docs"), FallbackLevel.Template, default);
        var document = AgentDocument.Parse(docs.Artifact.Content);
        Assert.Equal("docs/generated/stage-template.md", document.Files.Single().Path);
        Assert.Equal("Docs (template)", docs.Decisions.Single().MadeBy);
        Assert.Empty(client.Inputs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StageFor(new CoderAgent(client)).ExecuteFallbackAsync(StageContext("Coder"), FallbackLevel.Template, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => StageFor(new TesterAgent(client)).ExecuteFallbackAsync(StageContext("Tester"), FallbackLevel.Template, default));
    }
    [Fact]
    public async Task Token_budget_exhaustion_is_a_safe_stop_not_an_agent_failure()
    {
        await Recording(Document().ToJson());
        using var client = new ReplayChatClient(root, "demo");
        var ctx = Context();
        ctx.Budget.TokensUsed = ctx.Budget.MaxTokens;
        await Assert.ThrowsAsync<BudgetExceededException>(() => new RequirementsAgent(client).RunAsync(new("stage", "", []), ctx, default));
    }
    [Fact]
    public void Dotnet_output_is_parsed_into_gate_evidence()
    {
        const string output = """
              Failed UrlShortener.UnitTests.DomainTests.GeneratorProducesCodes [3 ms]
              Error Message:
               Assert.Equal() Failure
            Failed!  - Failed:     1, Passed:    42, Skipped:     0, Total:    43, Duration: 107 ms - UrlShortener.UnitTests.dll (net8.0)
            Passed!  - Failed:     0, Passed:    64, Skipped:     1, Total:    65, Duration: 383 ms - Orchestrator.Tests.dll (net8.0)
            """;
        var summary = DotnetOutput.TestSummary(output);
        Assert.Equal((106, 1, 1), (summary.Passed, summary.Failed, summary.Skipped));
        Assert.Equal("106 passed, 1 failed, 1 skipped; failing: UrlShortener.UnitTests.DomainTests.GeneratorProducesCodes", summary.Text);
        Assert.Equal("error CS1002: ; expected", DotnetOutput.BuildErrors("/src/A.cs(3,9): error CS1002: ; expected [/src/A.csproj]"));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
