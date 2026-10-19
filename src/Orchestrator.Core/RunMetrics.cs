using System.Text.Json;

namespace Orchestrator.Core;

public sealed record StageMetrics(string StageId, int Attempts, int Retries, TimeSpan Active, TimeSpan HumanWait, string Outcome);

// Reliability metrics derived from audit.jsonl, the append-only source of truth that survives resumes.
//   Success rate        runs completed / runs started
//   Retry rate          retries scheduled / stage attempts
//   Rollback frequency  rollbacks / runs started
//   MTTR                mean time from a stage's first failed attempt to its next completion
//   End-to-end latency  first event to last run outcome, per run (summed across runs)
//   Human wait          approval requested -> decided, kept apart from machine time
public sealed record RunMetrics(int RunsStarted, int RunsCompleted, int StageAttempts, int Retries, int Rollbacks, int Replans,
    TimeSpan? Mttr, int Recoveries, int UnrecoveredFailures, TimeSpan EndToEnd, TimeSpan HumanWait, IReadOnlyList<StageMetrics> Stages)
{
    private static readonly HashSet<string> RunOutcomes = ["run_completed", "run_failed", "run_blocked", "run_paused"];
    private static readonly HashSet<string> AttemptEnds = ["stage_completed", "attempt_failed", "stage_failed", "stage_blocked", "policy_denied", "policy_rejected"];

    public double SuccessRate => RunsStarted == 0 ? 0 : (double)RunsCompleted / RunsStarted;
    public double RetryRate => StageAttempts == 0 ? 0 : (double)Retries / StageAttempts;
    public double RollbackFrequency => RunsStarted == 0 ? 0 : (double)Rollbacks / RunsStarted;
    public TimeSpan MachineTime => EndToEnd - HumanWait;

    public static RunMetrics FromAuditFiles(IEnumerable<string> paths) =>
        FromAudit(paths.Where(File.Exists).SelectMany(File.ReadLines).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<AuditEvent>(l, RunJson.Options)!));

    public static RunMetrics FromAudit(IEnumerable<AuditEvent> events)
    {
        var runs = events.GroupBy(e => e.RunId).Select(g => Measure(g.ToList())).ToArray();
        var stages = runs.SelectMany(r => r.Stages).GroupBy(s => s.StageId)
            .Select(g => new StageMetrics(g.Key, g.Sum(s => s.Attempts), g.Sum(s => s.Retries), Sum(g.Select(s => s.Active)), Sum(g.Select(s => s.HumanWait)), g.Last().Outcome))
            .ToArray();
        var recoveries = runs.SelectMany(r => r.RecoveryTimes).ToArray();
        return new(runs.Count(r => r.Started), runs.Count(r => r.Completed), runs.Sum(r => r.Attempts), runs.Sum(r => r.Retries),
            runs.Sum(r => r.Rollbacks), runs.Sum(r => r.Replans),
            recoveries.Length == 0 ? null : TimeSpan.FromTicks((long)recoveries.Average(t => t.Ticks)), recoveries.Length,
            runs.Sum(r => r.Unrecovered), Sum(runs.Select(r => r.EndToEnd)), Sum(runs.Select(r => r.HumanWait)), stages);
    }

    private sealed record RunMeasure(bool Started, bool Completed, int Attempts, int Retries, int Rollbacks, int Replans,
        IReadOnlyList<TimeSpan> RecoveryTimes, int Unrecovered, TimeSpan EndToEnd, TimeSpan HumanWait, IReadOnlyList<StageMetrics> Stages);

    private sealed class StageTally
    {
        public int Attempts, Retries;
        public TimeSpan Active, Wait, WaitInAttempt;
        public DateTime? AttemptStart, WaitStart, FailedSince;
        public string Outcome = "pending";
    }

    // Events are processed in file (append) order, which is the order they happened.
    private static RunMeasure Measure(IReadOnlyList<AuditEvent> events)
    {
        var tallies = new Dictionary<string, StageTally>(StringComparer.Ordinal);
        StageTally For(string id) => tallies.TryGetValue(id, out var t) ? t : tallies[id] = new StageTally();
        var recoveries = new List<TimeSpan>();
        int retries = 0, rollbacks = 0, replans = 0;
        var lastOutcome = events.LastOrDefault(e => RunOutcomes.Contains(e.Event));
        foreach (var e in events)
        {
            var stage = e.Stage is null ? null : For(e.Stage);
            switch (e.Event)
            {
                case "stage_started":
                    stage!.Attempts++;
                    stage.AttemptStart = e.TimestampUtc;
                    stage.WaitInAttempt = TimeSpan.Zero;
                    break;
                case "retry_scheduled": retries++; stage!.Retries++; break;
                case "rollback": rollbacks++; break;
                case "replan": replans++; break;
                case "approval_requested" or "policy_approval_requested": stage!.WaitStart = e.TimestampUtc; break;
                case "approval_decided" or "policy_approval" when stage!.WaitStart is { } since:
                    var waited = e.TimestampUtc - since;
                    stage.Wait += waited;
                    if (stage.AttemptStart is not null) stage.WaitInAttempt += waited;
                    stage.WaitStart = null;
                    break;
            }
            if (stage is not null && AttemptEnds.Contains(e.Event))
            {
                CloseAttempt(stage, e.TimestampUtc);
                if (e.Event == "attempt_failed" || e.Event == "policy_denied") stage.FailedSince ??= e.TimestampUtc;
                if (e.Event == "stage_completed" && stage.FailedSince is { } failed)
                {
                    recoveries.Add(e.TimestampUtc - failed);
                    stage.FailedSince = null;
                }
                stage.Outcome = e.Event switch { "stage_completed" => "completed", "stage_failed" or "policy_denied" => "failed", "stage_blocked" or "policy_rejected" => "blocked", _ => stage.Outcome };
            }
            if (RunOutcomes.Contains(e.Event))
                foreach (var open in tallies.Values.Where(t => t.AttemptStart is not null)) { CloseAttempt(open, e.TimestampUtc); open.Outcome = "interrupted"; }
        }
        var end = lastOutcome?.TimestampUtc ?? events[^1].TimestampUtc;
        var stages = tallies.Select(t => new StageMetrics(t.Key, t.Value.Attempts, t.Value.Retries, t.Value.Active, t.Value.Wait, t.Value.Outcome)).ToArray();
        return new(events.Any(e => e.Event == "run_started"), lastOutcome?.Event == "run_completed", stages.Sum(s => s.Attempts), retries,
            rollbacks, replans, recoveries, tallies.Values.Count(t => t.FailedSince is not null), end - events[0].TimestampUtc,
            Sum(stages.Select(s => s.HumanWait)), stages);
    }

    private static void CloseAttempt(StageTally stage, DateTime at)
    {
        if (stage.AttemptStart is not { } start) return;
        stage.Active += at - start - stage.WaitInAttempt;
        stage.AttemptStart = null;
        stage.WaitInAttempt = TimeSpan.Zero;
    }

    private static TimeSpan Sum(IEnumerable<TimeSpan> values) => values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
}
