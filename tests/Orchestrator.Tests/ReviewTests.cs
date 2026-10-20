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
        public string Id => "validate";
        public IReadOnlyList<string> DependsOn => ["implement"];
        public RiskLevel Risk => RiskLevel.Low;
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(GateResult.Pass());
        public Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(new StageOutput(new Artifact("review", $"review {++Reviews}")));
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) =>
            passes(Reviews) ? Task.FromResult(GateResult.Pass()) : throw new ReworkRequestedException("LinkService never calls UrlNormalizer.");
    }

    private async Task<(RunContext Result, string[] Events)> Run(Work implement, Review review)
    {
        var scenario = new Scenario { Name = "review", Retry = new() { BaseSeconds = 0.001 },
            Stages = [new() { Id = "implement", Agent = "Coder" }, new() { Id = "validate", Agent = "Reviewer", DependsOn = ["implement"] }] };
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
        Assert.Equal(2, implement.ReplanReasons.Count);   // original + one rework
        Assert.Equal(2, review.Reviews);
        Assert.Equal("The review still failed after 1 rework(s): LinkService never calls UrlNormalizer.", result.StageStatuses["validate"].Reason);
        Assert.Equal(1, events.Count(e => e == "rework"));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
