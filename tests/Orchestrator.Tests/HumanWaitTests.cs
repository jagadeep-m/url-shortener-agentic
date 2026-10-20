using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

// The time budget limits machine time: time spent waiting for people is measured and excluded.
public sealed class HumanWaitTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wait-{Guid.NewGuid():N}");

    private sealed class ManualTime : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
        public void Advance(double seconds) => Now += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Overlapping_waits_are_counted_once_and_an_open_wait_is_included()
    {
        var time = new ManualTime();
        var clock = new HumanWaitClock(time);
        var first = clock.Measure();
        time.Advance(2);
        var second = clock.Measure();   // a second prompt while the first is still open
        time.Advance(3);
        first.Dispose();
        time.Advance(1);
        Assert.Equal(TimeSpan.FromSeconds(6), clock.Total);   // still open: counted up to now
        second.Dispose();
        second.Dispose();                                      // disposing twice changes nothing
        time.Advance(10);                                      // machine time, not counted
        Assert.Equal(TimeSpan.FromSeconds(6), clock.Total);
    }

    private sealed class SlowApprovals(TimeSpan delay) : IApprovalService
    {
        public async Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new(true, "test-reviewer", "ok");
        }
    }

    private sealed class Quick(string id) : IStage
    {
        public string Id => id;
        public IReadOnlyList<string> DependsOn => [];
        public RiskLevel Risk => RiskLevel.High;   // asks for approval before running
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(GateResult.Pass());
        public Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(new StageOutput(new Artifact("a", "a")));
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) => Task.FromResult(GateResult.Pass());
    }

    private async Task<RunContext> RunWithSlowApprovalAsync(bool measureWaits)
    {
        var scenario = new Scenario { Name = "wait", Budget = new() { MaxMinutes = 0.01 }, Stages = [new() { Id = "review", Agent = "Test", Risk = RiskLevel.High }] }; // 0.6 s
        IApprovalService approvals = new SlowApprovals(TimeSpan.FromSeconds(1.2));  // the human takes twice the whole budget
        var clock = measureWaits ? new HumanWaitClock() : null;
        if (clock is not null) approvals = new HumanWaitApprovalService(approvals, clock);
        using var audit = new JsonlAuditWriter(directory);
        return await new RunEngine(new(directory), audit, approvals, humanWait: clock).StartAsync(scenario, "Build", [new Quick("review")]);
    }

    [Fact]
    public async Task Approval_waits_do_not_use_up_the_time_budget()
    {
        var result = await RunWithSlowApprovalAsync(measureWaits: true);
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Without_the_wait_clock_the_same_wait_still_exhausts_the_budget()
    {
        var result = await RunWithSlowApprovalAsync(measureWaits: false);
        Assert.Equal(RunStatus.Paused, result.Status);
        Assert.Contains("minutes of machine time exhausted", result.StopReason);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
