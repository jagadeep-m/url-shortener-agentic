using System.Globalization;
using System.Text;
using Orchestrator.Core;

namespace Orchestrator.Cli;

public static class RunReport
{
    public static async Task<string> WriteAsync(RunContext context, string runsDirectory, CancellationToken ct = default)
    {
        var runDirectory = Path.Combine(runsDirectory, context.RunId.ToString("D"));
        var text = new StringBuilder($"# Run {context.RunId}\n\nStatus: {context.Status}\n\nScenario: {context.Scenario.Name}\n\nMode: {(context.Scenario.Replay ? "Replay (offline)" : "Live")}\n\nTrace: {context.TraceId}\n\n");
        if (context.StopReason is not null) text.AppendLine($"Stop reason: {Cell(context.StopReason)}\n");
        text.AppendLine($"Branch: {Cell(context.GitBranch)} (last good commit {Cell(context.LastGoodCommit)})\n");

        var run = RunMetrics.FromAuditFiles([Path.Combine(runDirectory, "audit.jsonl")]);
        var perStage = run.Stages.ToDictionary(s => s.StageId, StringComparer.Ordinal);
        text.AppendLine("| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |").AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var id in context.Scenario.Stages.Select(s => s.Id))
        {
            var state = context.StageStatuses[id];
            var m = perStage.GetValueOrDefault(id);
            text.AppendLine($"| {id} | {state.Status} | {state.Attempts} | {Cell(state.FallbackLevel?.ToString())} | {Cell(state.CommitSha?[..10])} | {Duration(m?.Active)} | {Duration(m?.HumanWait)} | {Cell(state.Reason)} |");
        }

        text.AppendLine("\n## Reliability metrics (this run)\n");
        AppendMetrics(text, run);
        var all = RunMetrics.FromAuditFiles(Directory.EnumerateDirectories(runsDirectory).Select(d => Path.Combine(d, "audit.jsonl")));
        text.AppendLine($"\n## Across all runs in {Path.GetFileName(runsDirectory)}/ ({all.RunsStarted} runs)\n");
        AppendMetrics(text, all);

        text.AppendLine("\n## Decision lineage\n\n```text").Append(LineageReport.Render(context)).AppendLine("```");
        text.AppendLine("\n## Artifacts\n");
        foreach (var id in context.Scenario.Stages.Select(s => s.Id).Where(context.Artifacts.ContainsKey))
            text.AppendLine($"- {id}: SHA-256 `{context.Artifacts[id].ContentHash}`");
        text.AppendLine($"\nReported model tokens: {context.Budget.TokensUsed} (replay has no model usage).\n\nThis report does not claim deployment; merging the run branch is a human decision.");
        var path = Path.Combine(runDirectory, "report.md");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(path, text.ToString(), ct);
        return path;
    }

    private static void AppendMetrics(StringBuilder text, RunMetrics m)
    {
        text.AppendLine("| Metric | Value |").AppendLine("| --- | --- |");
        text.AppendLine($"| Success rate | {Percent(m.SuccessRate)} ({m.RunsCompleted}/{m.RunsStarted} runs completed) |");
        text.AppendLine($"| Retry rate | {Percent(m.RetryRate)} ({m.Retries} retries / {m.StageAttempts} stage attempts) |");
        text.AppendLine($"| Rollback frequency | {m.RollbackFrequency.ToString("0.##", CultureInfo.InvariantCulture)} per run ({m.Rollbacks} rollbacks) |");
        text.AppendLine($"| MTTR | {(m.Mttr is { } mttr ? Duration(mttr) : "—")} ({m.Recoveries} recovered, {m.UnrecoveredFailures} unrecovered) |");
        text.AppendLine($"| Re-plans | {m.Replans} |");
        text.AppendLine($"| End-to-end latency | {Duration(m.EndToEnd)} |");
        text.AppendLine($"| Machine time | {Duration(m.MachineTime)} |");
        text.AppendLine($"| Human wait time | {Duration(m.HumanWait)} |");
    }

    private static string Percent(double value) => value.ToString("0.#%", CultureInfo.InvariantCulture);
    private static string Duration(TimeSpan? value) => value is not { } v ? "—"
        : v.TotalSeconds < 60 ? v.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : $"{(int)v.TotalMinutes} min {v.Seconds} s";
    private static string Cell(string? text) => (text ?? "—").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
