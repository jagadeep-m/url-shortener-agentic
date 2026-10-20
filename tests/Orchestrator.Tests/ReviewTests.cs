using Microsoft.Extensions.AI;
using Orchestrator.Agents;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

// The review is binding: a FAIL verdict sends the reviewed work back with the findings, at most once.
// (Found by a live greenfield run: the Reviewer said "the normalizer is never called", but the run still completed.)
public sealed class ReviewTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("All good.\n\nVerdict: PASS", true, "All good.\n\nVerdict: PASS")]
    [InlineData("Gaps found.\n**Verdict: FAIL**\n- LinkService never calls UrlNormalizer.", false, "- LinkService never calls UrlNormalizer.")]
    [InlineData("verdict: fail", false, "verdict: fail")]
    public void Review_verdicts_are_parsed_with_their_findings(string content, bool passed, string findings) =>
        Assert.Equal((passed, findings), Verdicts.Review(content));

    [Fact]
    public void Missing_verdicts_and_recommendations_are_rejected()
    {
        Assert.Contains("Verdict: PASS", Assert.Throws<InvalidDataException>(() => Verdicts.Review("Looks fine.")).Message);
        Assert.Contains("Recommendation: READY", Assert.Throws<InvalidDataException>(() => Verdicts.Ready("Ship it.")).Message);
        Assert.True(Verdicts.Ready("Recommendation: READY\nall checks green"));
        Assert.False(Verdicts.Ready("**Recommendation: NOT READY**\nthe normalizer is unused"));
    }

    private sealed class Reply(string json) : BufferedChatClient
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    [Fact]
    public async Task Release_recommendation_leads_the_summary_shown_at_the_prompt()
    {
        var ctx = new RunContext { Scenario = new() { Stages = [new() { Id = "release", Agent = "Release" }] } };
        var result = await new ReleaseAgent(new Reply("""{"summary":"Release assessment","rationale":"r","content":"Recommendation: NOT READY\nThe normalizer is unused.","files":[]}"""))
            .RunAsync(new("release", "", []), ctx, default);
        Assert.StartsWith("""{"summary": "[NOT READY] Release assessment""", result.Output!.Artifact.Content.Replace("\n", "").Replace("  ", ""));
    }

    [Fact]
    public async Task The_verdict_field_completes_a_review_whose_content_has_no_verdict_line()
    {
        // Live reviews sometimes put the verdict only in the summary and were retried for a missing line.
        var ctx = new RunContext { Scenario = new() { Stages = [new() { Id = "validate", Agent = "Reviewer" }] } };
        async Task<string> Review(string json) => (await new ReviewerAgent(new Reply(json)).RunAsync(new("validate", "", []), ctx, default)).Output!.Artifact.Content;

        Assert.Contains(@"All requirements are met.\n\nVerdict: PASS", await Review("""{"summary":"The review verdict is PASS.","rationale":"r","content":"All requirements are met.","verdict":"pass","files":[]}"""));
        Assert.Contains(@"Verdict: FAIL Docs claim encoded names are kept.", await Review("""{"summary":"Docs claim encoded names are kept.","rationale":"r","content":"Gaps below.","verdict":"FAIL","files":[]}"""));
        Assert.Contains(@"Verdict: FAIL\n- fix it", await Review("""{"summary":"s","rationale":"r","content":"Verdict: FAIL\n- fix it","verdict":"PASS","files":[]}""")); // the line wins
        await Assert.ThrowsAsync<InvalidDataException>(() => Review("""{"summary":"s","rationale":"r","content":"Looks fine.","verdict":"maybe","files":[]}"""));
    }

    [Fact]
    public void A_schema_error_names_what_is_missing_so_the_retry_can_fix_it()
    {
        // A live Docs stage used two of its three attempts on a generic schema error.
        var error = Assert.Throws<InvalidDataException>(() => AgentDocument.Parse("""{"summary":"s","rationale":"","content":" ","files":[{"path":"docs/a.md"}]}"""));
        Assert.EndsWith("Missing or empty: rationale; content (a non-empty markdown artifact, even when the work is in files); a path and content for every files entry.", error.Message);
        Assert.Empty(AgentDocument.Parse("""{"summary":"s","rationale":"r","content":"c"}""").Files); // no files key: no file changes
    }

    // ---- The engine's rework loop, with stage doubles

    private sealed class Approvals : IApprovalService
    {
        public Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct) => Task.FromResult(new ApprovalResult(true, "test-reviewer", "ok"));
    }

    private sealed class Work(string id) : IStage
    {
        public readonly List<string?> ReplanReasons = [];
        public string Id => id;
        public IReadOnlyList<string> DependsOn => [];
        public RiskLevel Risk => RiskLevel.Low;
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(GateResult.Pass());
        public Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct)
        {
            ReplanReasons.Add(ctx.StageStatuses[Id].ReplanReason);
            return Task.FromResult(new StageOutput(new Artifact(id, $"{id} v{ReplanReasons.Count}")));
        }
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) => Task.FromResult(GateResult.Pass());
    }

    private sealed class Review(Func<int, bool> passes) : IStage
    {
        public int Reviews;
        public readonly List<string?> ReplanReasons = [];
        public string Id => "validate";
        public IReadOnlyList<string> DependsOn => ["implement"];
        public RiskLevel Risk => RiskLevel.Low;
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(GateResult.Pass());
        public Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct)
        {
            ReplanReasons.Add(ctx.StageStatuses[Id].ReplanReason);
            return Task.FromResult(new StageOutput(new Artifact("review", $"review {++Reviews}")));
        }
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) =>
            passes(Reviews) ? Task.FromResult(GateResult.Pass()) : throw new ReworkRequestedException("LinkService never calls UrlNormalizer.");
    }

    private async Task<(RunContext Result, string[] Events)> Run(Work implement, Review review)
    {
        var scenario = new Scenario
        {
            Name = "review",
            Retry = new() { BaseSeconds = 0.001 },
            Stages = [new() { Id = "implement", Agent = "Coder" }, new() { Id = "validate", Agent = "Reviewer", DependsOn = ["implement"] }]
        };
        using var audit = new JsonlAuditWriter(directory);
        var result = await new RunEngine(new(directory), audit, new Approvals()).StartAsync(scenario, "Normalize URLs", [implement, review]);
        var events = (await File.ReadAllLinesAsync(audit.GetPath(result.RunId))).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()!).ToArray();
        return (result, events);
    }

    [Fact]
    public async Task A_failed_review_reworks_the_implementation_with_the_findings_then_reviews_again()
    {
        var implement = new Work("implement");
        var review = new Review(passes: n => n >= 2);
        var (result, events) = await Run(implement, review);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, implement.ReplanReasons.Count);
        Assert.Null(implement.ReplanReasons[0]);
        Assert.Equal("The review failed. Fix these findings: LinkService never calls UrlNormalizer.", implement.ReplanReasons[1]);
        Assert.Equal(2, review.Reviews);
        Assert.Equal("Review the reworked work again. Your earlier findings were: LinkService never calls UrlNormalizer. Check that each one is fixed, "
            + "and review what the rework changed.", review.ReplanReasons[1]); // the re-review checks its own findings
        Assert.Equal("implement v2", result.Artifacts["implement"].Content);
        Assert.Equal(1, result.StageStatuses["validate"].Reworks);
        Assert.Equal(1, events.Count(e => e == "review_failed"));
        Assert.Equal(1, events.Count(e => e == "rework"));
        Assert.DoesNotContain("retry_scheduled", events); // a failed review is not retried, it is reworked
    }

    [Fact]
    public async Task Rework_is_bounded_and_a_review_that_keeps_failing_fails_the_run()
    {
        var implement = new Work("implement");
        var review = new Review(passes: _ => false);
        var (result, events) = await Run(implement, review);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(3, implement.ReplanReasons.Count);   // original + two reworks
        Assert.Equal(3, review.Reviews);
        Assert.Equal("The review still failed after 2 rework(s): LinkService never calls UrlNormalizer.", result.StageStatuses["validate"].Reason);
        Assert.Equal(2, events.Count(e => e == "rework"));
    }

    // A live Docs stage reworked beside the Coder documented the code as it was before the fix, and the review failed again.
    private sealed class Logged(string id, List<string> log, params string[] dependsOn) : IStage
    {
        public string Id => id;
        public IReadOnlyList<string> DependsOn => dependsOn;
        public RiskLevel Risk => RiskLevel.Low;
        public int Reviews;
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(GateResult.Pass());
        public async Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct)
        {
            lock (log) log.Add("start " + id);
            await Task.Delay(50, ct);
            lock (log) log.Add("end " + id);
            return new StageOutput(new Artifact(id, $"{id} {log.Count}"));
        }
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) =>
            id == "validate" && ++Reviews == 1 ? throw new ReworkRequestedException("The doc says encoded names are kept.") : Task.FromResult(GateResult.Pass());
    }

    [Fact]
    public async Task Reworked_stages_run_one_at_a_time_in_scenario_order_while_the_first_pass_stays_parallel()
    {
        var log = new List<string>();
        string[] work = ["implement", "tests", "docs"];
        var scenario = new Scenario
        {
            Name = "review",
            Retry = new() { BaseSeconds = 0.001 },
            Stages = [.. work.Select(id => new StageDefinition { Id = id, Agent = id }), new() { Id = "validate", Agent = "Reviewer", DependsOn = [.. work] }]
        };
        using var audit = new JsonlAuditWriter(directory);
        var result = await new RunEngine(new(directory), audit, new Approvals())
            .StartAsync(scenario, "Normalize URLs", [.. work.Select(id => new Logged(id, log)), new Logged("validate", log, work)]);

        Assert.Equal(RunStatus.Completed, result.Status);
        var first = log.TakeWhile(e => e != "start validate").ToArray();
        Assert.Equal(3, first.TakeWhile(e => e.StartsWith("start ", StringComparison.Ordinal)).Count()); // all three started before any ended
        var rework = log.SkipWhile(e => e != "end validate").Skip(1).TakeWhile(e => e != "start validate").ToArray();
        Assert.Equal(new[] { "start implement", "end implement", "start tests", "end tests", "start docs", "end docs" }, rework);
        Assert.All(result.StageStatuses.Values, s => Assert.False(s.InRework));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
