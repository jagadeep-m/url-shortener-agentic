using System.Diagnostics.Metrics;
using Orchestrator.Cli;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

public sealed class MetricsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"metrics-{Guid.NewGuid():N}");
    private static readonly DateTime T0 = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Run = Guid.NewGuid();

    private static AuditEvent E(double seconds, string name, string? stage = null, Guid? run = null) =>
        new(Guid.NewGuid(), run ?? Run, T0.AddSeconds(seconds), new string('a', 32), stage, null, name, null, null, null, null, null);

    // design: 30 s human review inside its attempt. implement: fails at 10 s, retried, completes at 40 s.
    private static readonly AuditEvent[] KnownLog =
    [
        E(0, "run_started"),
        E(1, "stage_started", "design"), E(2, "approval_requested", "design"), E(32, "approval_decided", "design"), E(33, "stage_completed", "design"),
        E(34, "stage_started", "implement"), E(44, "attempt_failed", "implement"), E(44, "retry_scheduled", "implement"),
        E(46, "stage_started", "implement"), E(74, "stage_completed", "implement"),
        E(75, "stage_started", "tests"), E(80, "stage_completed", "tests"),
        E(80, "run_completed"),
    ];

    [Fact]
    public void Metrics_are_computed_correctly_from_a_known_audit_log()
    {
        var m = RunMetrics.FromAudit(KnownLog);
        Assert.Equal(4, m.StageAttempts);
        Assert.Equal(1, m.Retries);
        Assert.Equal(0.25, m.RetryRate);
        Assert.Equal(TimeSpan.FromSeconds(30), m.Mttr);          // failure at 44 s -> completion at 74 s
        Assert.Equal((1, 0), (m.Recoveries, m.UnrecoveredFailures));
        Assert.Equal(1.0, m.SuccessRate);
        Assert.Equal(TimeSpan.FromSeconds(80), m.EndToEnd);
        Assert.Equal(TimeSpan.FromSeconds(30), m.HumanWait);
        Assert.Equal(TimeSpan.FromSeconds(50), m.MachineTime);
        var stages = m.Stages.ToDictionary(s => s.StageId);
        Assert.Equal(TimeSpan.FromSeconds(2), stages["design"].Active);     // 32 s attempt minus 30 s review
        Assert.Equal(TimeSpan.FromSeconds(38), stages["implement"].Active); // 10 s + 28 s
        Assert.Equal((2, 1, "completed"), (stages["implement"].Attempts, stages["implement"].Retries, stages["implement"].Outcome));
    }

    [Fact]
    public void Aggregates_count_success_rate_rollbacks_and_unrecovered_failures_per_run()
    {
        var failed = Guid.NewGuid();
        AuditEvent[] second =
        [
            E(0, "run_started", run: failed), E(1, "stage_started", "implement", failed), E(5, "attempt_failed", "implement", failed),
            E(6, "rollback", "implement", failed), E(6, "stage_failed", "implement", failed), E(7, "run_failed", run: failed),
        ];
        var m = RunMetrics.FromAudit([.. KnownLog, .. second]);
        Assert.Equal((2, 1), (m.RunsStarted, m.RunsCompleted));
        Assert.Equal(0.5, m.SuccessRate);
        Assert.Equal(0.5, m.RollbackFrequency);
        Assert.Equal(1, m.UnrecoveredFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), m.Mttr); // only recovered failures count towards MTTR
        Assert.Equal(TimeSpan.FromSeconds(87), m.EndToEnd);
    }

    [Fact]
    public void Paused_runs_close_open_attempts_and_do_not_count_as_completed()
    {
        var m = RunMetrics.FromAudit([E(0, "run_started"), E(1, "stage_started", "slow"), E(4, "run_paused")]);
        Assert.Equal(0.0, m.SuccessRate);
        Assert.Equal(("interrupted", TimeSpan.FromSeconds(3)), (m.Stages[0].Outcome, m.Stages[0].Active));
    }

    [Fact]
    public async Task Meter_reports_attempts_retries_and_stage_durations_during_a_run()
    {
        var scenarioName = "meter-" + Guid.NewGuid().ToString("N");
        var measurements = new List<(string Instrument, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == OrchestratorTelemetry.SourceName) l.EnableMeasurementEvents(instrument); };
        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
                if (tag.Key == "scenario" && Equals(tag.Value, scenarioName)) lock (measurements) measurements.Add((instrument.Name, value));
        }
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.Start();

        var calls = 0;
        var stage = new FlakyStage(() => ++calls == 1);
        var scenario = new Scenario { Name = scenarioName, Retry = new() { BaseSeconds = 0.001 }, Stages = [new() { Id = "flaky", Agent = "Test" }] };
        using var audit = new JsonlAuditWriter(directory);
        var result = await new RunEngine(new(directory), audit, new NoApprovals()).StartAsync(scenario, "Build", [stage]);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, measurements.Count(m => m.Instrument == "orchestrator.stage.attempts"));
        Assert.Equal(1, measurements.Count(m => m.Instrument == "orchestrator.stage.retries"));
        Assert.Equal(2, measurements.Count(m => m.Instrument == "orchestrator.stage.duration"));
        Assert.Single(measurements, m => m.Instrument == "orchestrator.runs.started");
        Assert.Single(measurements, m => m.Instrument == "orchestrator.runs.finished");
    }

    [Fact]
    public async Task Report_contains_metrics_and_lineage_down_to_commits()
    {
        var context = new RunContext { Requirement = "Shorten links", Scenario = new() { Name = "demo", Stages = [new() { Id = "requirements" }, new() { Id = "design", DependsOn = ["requirements"] }, new() { Id = "implement", DependsOn = ["design"] }] } };
        foreach (var id in new[] { "requirements", "design", "implement" }) context.StageStatuses[id] = new() { Status = StageStatus.Completed };
        Decision D(string id, string stage, params string[] parents) => new(id, stage, $"{stage} decision", "why", parents, stage + "Agent", T0);
        context.Decisions = [D("req00001", "requirements"), D("des00001", "design", "req00001"), D("imp00001", "implement", "des00001"), D("imp00002", "implement", "des00001")];
        context.SupersededDecisions = ["imp00001"];
        context.DecisionCommits = context.DecisionCommits.Add("imp00002", "0123456789abcdef0123456789abcdef01234567");

        var tree = LineageReport.Render(context);
        Assert.Equal("""
            Requirement: Shorten links
            └─ [requirements] requirements decision · requirementsAgent · req00001
               └─ [design] design decision · designAgent · des00001
                  ├─ [implement] implement decision · implementAgent · imp00001 · superseded by re-plan
                  └─ [implement] implement decision · implementAgent · imp00002 · commit 0123456

            """.Replace("\r", ""), tree);

        var runDirectory = Path.Combine(directory, context.RunId.ToString("D"));
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllLinesAsync(Path.Combine(runDirectory, "audit.jsonl"), KnownLog.Select(e =>
            System.Text.Json.JsonSerializer.Serialize(e with { RunId = context.RunId }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        var report = await File.ReadAllTextAsync(await RunReport.WriteAsync(context, directory));
        Assert.Contains("| Retry rate | 25% (1 retries / 4 stage attempts) |", report);
        Assert.Contains("| MTTR | 30.0 s (1 recovered, 0 unrecovered) |", report);
        Assert.Contains("| Human wait time | 30.0 s |", report);
        Assert.Contains("commit 0123456", report);
    }

    private sealed class NoApprovals : IApprovalService
    {
        public Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct) => Task.FromResult(new ApprovalResult(true, "t", "ok"));
    }

    private sealed class FlakyStage(Func<bool> fail) : IStage
    {
        public string Id => "flaky";
        public IReadOnlyList<string> DependsOn => [];
        public RiskLevel Risk => RiskLevel.Low;
        public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(GateResult.Pass());
        public Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct) => Task.FromResult(new StageOutput(new Artifact("f", "f")));
        public Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct) =>
            Task.FromResult(fail() ? GateResult.Block("flaky") : GateResult.Pass());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
