using Microsoft.Extensions.AI;
using Orchestrator.Agents;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

// Found by a live greenfield run: the design named no files, so the Coder never saw LinkService and wrote an
// unconnected UrlNormalizer; the Tester's correct tests failed and were blamed (rolled back five times).
// Now code roles see the whole product source, new tests may land red, and validation sends the code back.
// A later run reworked correctly, but the second review called a test missing that an earlier round had written:
// the Reviewer saw only the files the latest artifacts named. It now sees every file the run changed.
public sealed class RedTestsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "red-" + Guid.NewGuid().ToString("N"));

    public RedTestsTests()
    {
        Write("src/UrlShortener.Application/Links/LinkService.cs", "class LinkService { public void CreateAsync() { } }");
        Write("src/UrlShortener.Domain/ShortLink.cs", "class ShortLink { }");
        Write("src/UrlShortener.Infrastructure/Migrations/20261017_InitialCreate.cs", "class InitialCreate { }");
        Write("tests/UrlShortener.UnitTests/ExistingTests.cs", "class ExistingTests { }");
        Write("docs/features/url-normalization.md", "# URL normalization");
    }

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
        File.WriteAllText(Path.Combine(root, path), content);
    }

    // ---- Fix A: whole product source

    [Fact]
    public void Whole_product_source_follows_the_named_files_and_skips_tests_and_migrations()
    {
        var code = new CodeContext(root);
        var named = code.RelevantFiles(["Add a URL normalizer before a link is saved."]); // names no existing type or path
        Assert.Empty(named);

        var whole = code.RelevantFiles(["Keep ShortLink as is."], includeProductSource: true).Select(f => f.Path).ToArray();
        Assert.Equal(new[] { "src/UrlShortener.Domain/ShortLink.cs", "src/UrlShortener.Application/Links/LinkService.cs" }, whole);
    }

    // ---- Fix B: judging test results

    private const string Output = """
          Failed UrlShortener.IntegrationTests.NormalizationTests.Create_normalizes(url: "HTTPS://A.COM") [12 ms]
          Error Message:
           Assert.Equal() Failure: Strings differ
        Expected: "https://a.com/"
        Actual:   "HTTPS://A.COM"
          Stack Trace:
             at Foo()
          Failed UrlShortener.IntegrationTests.NormalizationTests.Strips_utm [3 ms]
          Error Message:
           Assert.Equal() Failure
          Stack Trace:
             at Bar()
        Failed!  - Failed:     2, Passed:    84, Skipped:     0, Total:    86, Duration: 1 s - UrlShortener.IntegrationTests.dll (net8.0)
        """;

    [Fact]
    public void Failure_details_carry_test_names_with_arguments_and_their_assertion_messages()
    {
        var summary = DotnetOutput.TestSummary(Output);
        Assert.Equal(2, summary.FailingTests.Count);
        Assert.Equal("UrlShortener.IntegrationTests.NormalizationTests.Create_normalizes(url: \"HTTPS://A.COM\")", summary.FailingTests[0]);
        var details = DotnetOutput.FailureDetails(Output);
        Assert.Contains("Create_normalizes(url: \"HTTPS://A.COM\"): Assert.Equal() Failure: Strings differ Expected: \"https://a.com/\" Actual: \"HTTPS://A.COM\"", details);
        Assert.Contains("Strips_utm: Assert.Equal() Failure", details);
    }

    [Fact]
    public void Only_failures_confined_to_new_test_classes_may_land_red()
    {
        var red = DotnetValidationRunner.Judge(failed: true, Output, ["FixedClock", "NormalizationTests"], "tests.attempt1"); // helpers that pass are not named
        Assert.True(red.Passed);
        Assert.Contains("2 new test(s) in NormalizationTests fail against the current code (red), so validation will decide", red.Reason);
        Assert.Contains("Expected: \"https://a.com/\"", red.Reason);

        Assert.False(DotnetValidationRunner.Judge(true, Output, [], "x").Passed);                       // strict without a tolerance list
        Assert.False(DotnetValidationRunner.Judge(true, Output, ["OtherTests"], "x").Passed);           // an existing test is failing
        var mixed = Output.Replace("Failed!  - Failed:     2", "  Failed UrlShortener.UnitTests.DomainTests.Codes [1 ms]\nFailed!  - Failed:     3");
        Assert.False(DotnetValidationRunner.Judge(true, mixed, ["NormalizationTests"], "x").Passed);   // new and existing both failing
        Assert.False(DotnetValidationRunner.Judge(false, "no results", ["NormalizationTests"], "x").Passed);
        Assert.StartsWith("dotnet test failed: 84 passed, 2 failed", DotnetValidationRunner.Judge(true, Output, [], "x").Reason);
    }

    // ---- Fix B: the stage gates

    private sealed class Capture(GateResult result) : IValidationRunner
    {
        public IReadOnlyCollection<string>? Tolerated;
        public Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct) => Task.FromResult(result);
        public Task<GateResult> ValidateAsync(string runDirectory, string logName, IReadOnlyCollection<string> tolerateFailuresIn, CancellationToken ct)
        {
            Tolerated = tolerateFailuresIn;
            return Task.FromResult(result);
        }
    }

    private sealed class Approvals : IApprovalService
    {
        public Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct) => Task.FromResult(new ApprovalResult(true, "t", "ok"));
    }

    private sealed class NoModel : BufferedChatClient
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not used");
    }

    private RunContext? lastContext;
    private Action<RunContext>? setUp;

    private async Task<GateResult> ExitAsync(IAgent agent, string stage, Capture validation, params FileChange[] files)
    {
        using var activity = OrchestratorTelemetry.Source.StartActivity("red-tests");
        var runs = Path.Combine(root, "runs");
        using var audit = new JsonlAuditWriter(runs);
        var definition = new StageDefinition { Id = stage, Agent = agent.Role };
        var ctx = new RunContext { Scenario = new() { Stages = [definition] } };
        ctx.StageStatuses[stage] = new() { Attempts = 1 };
        lastContext = ctx;
        setUp?.Invoke(ctx);
        Directory.CreateDirectory(Path.Combine(runs, ctx.RunId.ToString("D")));
        var content = agent.Role == "Reviewer" ? "# Review\n\nVerdict: PASS" : "# Tests";
        var output = new StageOutput(new Artifact(stage + ".json", new AgentDocument("s", "r", content, files).ToJson()));
        var gate = new AgentStage(definition, agent, new FileChangeService(root, [new AllowAllPolicy()], new Approvals()), runs, validation, audit);
        return await gate.CheckExitAsync(ctx, output, default);
    }

    [Fact]
    public async Task Tester_gate_tolerates_only_classes_in_new_test_files()
    {
        var validation = new Capture(GateResult.Pass("ok"));
        await ExitAsync(new TesterAgent(new NoModel()), "tests", validation,
            new FileChange("tests/UrlShortener.IntegrationTests/NormalizationTests.cs", "public sealed class NormalizationTests { }"),
            new FileChange("tests/UrlShortener.UnitTests/ExistingTests.cs", "class ExistingTests { }"));   // edited, not new
        Assert.Equal(new[] { "NormalizationTests" }, validation.Tolerated);
    }

    [Fact]
    public async Task A_failing_full_run_at_validation_requests_rework_with_the_failure_details()
    {
        var validation = new Capture(GateResult.Block("dotnet test failed: 84 passed, 2 failed. First failures: Create_normalizes: Expected \"https://a.com/\""));
        var rework = await Assert.ThrowsAsync<ReworkRequestedException>(() => ExitAsync(new ReviewerAgent(new NoModel()), "validate", validation));
        Assert.Equal("The full build and test run failed at validation: dotnet test failed: 84 passed, 2 failed. First failures: Create_normalizes: Expected \"https://a.com/\"", rework.Findings);
    }

    [Fact]
    public async Task Other_code_stages_are_still_retried_on_a_failing_run()
    {
        var blocked = GateResult.Block("dotnet build failed: error CS0246");
        var result = await ExitAsync(new CoderAgent(new NoModel()), "implement", new Capture(blocked), new FileChange("src/UrlShortener.Domain/Url.cs", "class Url { }"));
        Assert.False(result.Passed);
        Assert.Equal(blocked.Reason, result.Reason);
    }

    // ---- The Reviewer sees the whole run

    [Fact]
    public async Task Files_are_recorded_for_the_run_when_a_stage_lands_them_and_not_when_it_fails()
    {
        await ExitAsync(new TesterAgent(new NoModel()), "tests", new Capture(GateResult.Pass("ok")),
            new FileChange("tests/UrlShortener.UnitTests/LinkNormalizationTests.cs", "class LinkNormalizationTests { }"));
        Assert.Equal("tests", lastContext!.ChangedFiles["tests/UrlShortener.UnitTests/LinkNormalizationTests.cs"]);

        await ExitAsync(new CoderAgent(new NoModel()), "implement", new Capture(GateResult.Block("dotnet build failed")),
            new FileChange("src/UrlShortener.Domain/Url.cs", "class Url { }"));
        Assert.Empty(lastContext!.ChangedFiles);
    }

    private sealed class Reply(string text) : BufferedChatClient
    {
        public string Instructions = "";
        public string[] Shown = [];
        public string Prompt = "";
        public int Calls;
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls++;
            Instructions = list[0].Text;
            using var user = System.Text.Json.JsonDocument.Parse(list[1].Text);
            Prompt = user.RootElement.GetProperty("prompt").GetString()!;
            Shown = user.RootElement.GetProperty("currentFiles").EnumerateArray().Select(f => f.GetProperty("path").GetString()!).ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }
    }

    [Fact]
    public async Task Reviewer_sees_every_file_the_run_changed_first_including_tests_from_earlier_rounds()
    {
        var ctx = new RunContext { Scenario = new() { Stages = [new() { Id = "validate", Agent = "Reviewer" }, new() { Id = "implement", Agent = "Coder" }] } };
        ctx.StageStatuses["validate"] = new();
        ctx.ChangedFiles["tests/UrlShortener.UnitTests/ExistingTests.cs"] = "tests";            // written in the first round
        ctx.ChangedFiles["src/UrlShortener.Application/Links/LinkService.cs"] = "implement";
        ctx.ChangedFiles["tests/UrlShortener.UnitTests/RolledBackTests.cs"] = "tests";         // no longer on disk: skipped
        ctx.ChangedFiles["docs/features/url-normalization.md"] = "docs";                       // a live review missed this file

        var reviewer = new Reply("""{"summary":"s","rationale":"r","content":"Verdict: PASS","files":[]}""");
        Assert.True((await new ReviewerAgent(reviewer) { Code = new CodeContext(root) }.RunAsync(new("validate", "", []), ctx, default)).Success);
        Assert.Equal(new[] { "docs/features/url-normalization.md", "src/UrlShortener.Application/Links/LinkService.cs", "tests/UrlShortener.UnitTests/ExistingTests.cs",
            "src/UrlShortener.Domain/ShortLink.cs" }, reviewer.Shown);
        Assert.Contains("including tests and docs from earlier rounds", reviewer.Instructions);

        // Writing roles still see the product source only; the run's test files are the Reviewer's view.
        var coder = new Reply("""{"summary":"s","rationale":"r","content":"c","files":[{"path":"src/UrlShortener.Domain/X.cs","content":"class X { }"}]}""");
        await new CoderAgent(coder) { Code = new CodeContext(root) }.RunAsync(new("implement", "", []), ctx, default);
        Assert.DoesNotContain("tests/UrlShortener.UnitTests/ExistingTests.cs", coder.Shown);
        Assert.DoesNotContain("docs/features/url-normalization.md", coder.Shown);

        // Docs sees the run's changes too, so a rework updates its own document (a live rework added a second, disagreeing one).
        var docs = new Reply("""{"summary":"s","rationale":"r","content":"c","files":[{"path":"docs/features/url-normalization.md","content":"# v2"}]}""");
        ctx.Scenario.Stages.Add(new() { Id = "docs", Agent = "Docs" });
        await new DocsAgent(docs) { Code = new CodeContext(root) }.RunAsync(new("docs", "", []), ctx, default);
        Assert.Equal("docs/features/url-normalization.md", docs.Shown[0]);
        Assert.Contains("update that file instead of adding another", docs.Instructions);
        Assert.Contains("never mention test results, failing tests, review findings", docs.Instructions); // a live rework wrote "this test fails" into the doc
    }

    // ---- The Reviewer works from a fresh build and test run

    private AgentStage ReviewStage(IAgent reviewer, Capture validation, out RunContext ctx)
    {
        activity = OrchestratorTelemetry.Source.StartActivity("red-tests-review");
        var runs = Path.Combine(root, "runs");
        var definition = new StageDefinition { Id = "validate", Agent = "Reviewer" };
        ctx = new RunContext { Scenario = new() { Stages = [definition] } };
        ctx.StageStatuses["validate"] = new() { Attempts = 1 };
        return new AgentStage(definition, reviewer, new FileChangeService(root, [new AllowAllPolicy()], new Approvals()), runs, validation, new JsonlAuditWriter(runs));
    }

    [Fact]
    public async Task A_failing_run_before_the_review_requests_rework_without_asking_the_model()
    {
        var stage = ReviewStage(new ReviewerAgent(new NoModel()), new Capture(GateResult.Block("dotnet test failed: 90 passed, 3 failed")), out var ctx);
        var rework = await Assert.ThrowsAsync<ReworkRequestedException>(() => stage.ExecuteAsync(ctx, default)); // NoModel throws if it is called
        Assert.Equal("The full build and test run failed at validation: dotnet test failed: 90 passed, 3 failed", rework.Findings);
    }

    [Fact]
    public async Task The_review_prompt_carries_the_current_run_over_older_gate_notes()
    {
        // A live review read the tests gate's "5 new tests fail (red)" note after the Coder had fixed them and asked for that fix again.
        var reviewer = new Reply("""{"summary":"s","rationale":"r","content":"Verdict: PASS","files":[]}""");
        var stage = ReviewStage(new ReviewerAgent(reviewer) { Code = new CodeContext(root) }, new Capture(GateResult.Pass("dotnet build succeeded; dotnet test: 93 passed, 0 failed, 0 skipped.")), out var ctx);
        await stage.ExecuteAsync(ctx, default);
        Assert.Equal(1, reviewer.Calls);
        Assert.Contains("Full build and test run of the current code, just before this review: dotnet build succeeded; dotnet test: 93 passed, 0 failed, 0 skipped.", reviewer.Prompt);
        Assert.Contains("where they differ, this run is current", reviewer.Prompt);
    }

    private System.Diagnostics.Activity? activity;

    // ---- Neither code stage is blamed for the other's new tests

    [Fact]
    public async Task Coder_and_reworked_Tester_tolerate_only_test_classes_this_run_created()
    {
        // A live Coder was retried three times against a red test that read its HTTP response twice.
        Write("tests/UrlShortener.IntegrationTests/UrlNormalizationTests.cs", "public sealed class UrlNormalizationTests { }"); // landed earlier in this run
        setUp = ctx => ctx.CreatedFiles["tests/UrlShortener.IntegrationTests/UrlNormalizationTests.cs"] = "tests";

        var coder = new Capture(GateResult.Pass("ok"));
        await ExitAsync(new CoderAgent(new NoModel()), "implement", coder, new FileChange("src/UrlShortener.Domain/Url.cs", "class Url { }"));
        Assert.Equal(new[] { "UrlNormalizationTests" }, coder.Tolerated); // ExistingTests predates the run: never tolerated
        Assert.Equal("implement", lastContext!.CreatedFiles["src/UrlShortener.Domain/Url.cs"]);

        var tester = new Capture(GateResult.Pass("ok"));                       // the rework edits its own earlier file
        await ExitAsync(new TesterAgent(new NoModel()), "tests", tester,
            new FileChange("tests/UrlShortener.IntegrationTests/UrlNormalizationTests.cs", "public sealed class UrlNormalizationTests { int fixedTest; }"));
        Assert.Equal(new[] { "UrlNormalizationTests" }, tester.Tolerated);
        Assert.False(lastContext!.CreatedFiles.ContainsKey("tests/UrlShortener.UnitTests/ExistingTests.cs"));
    }

    [Fact]
    public void Failure_details_say_where_the_failure_was_thrown()
    {
        const string output = """
              Failed UrlShortener.IntegrationTests.UrlNormalizationTests.Create(inputUrl: "HTTP://A.COM") [540 ms]
              Error Message:
               System.ObjectDisposedException : Cannot access a closed Stream.
              Stack Trace:
                 at System.IO.MemoryStream.Read(Byte[] buffer, Int32 offset, Int32 count)
               at UrlShortener.IntegrationTests.UrlNormalizationTests.Create(String inputUrl) in /work/tests/UrlShortener.IntegrationTests/UrlNormalizationTests.cs:line 43
            Failed!  - Failed:     1, Passed:    84, Skipped:     0, Total:    85, Duration: 1 s - UrlShortener.IntegrationTests.dll (net8.0)
            """;
        Assert.Equal("UrlShortener.IntegrationTests.UrlNormalizationTests.Create(inputUrl: \"HTTP://A.COM\"): System.ObjectDisposedException : Cannot access a closed Stream. "
            + "(thrown at UrlNormalizationTests.cs:line 43)", DotnetOutput.FailureDetails(output));
    }

    public void Dispose() { activity?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
