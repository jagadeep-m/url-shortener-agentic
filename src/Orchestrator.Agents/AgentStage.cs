using System.Text.Json;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public interface IValidationRunner
{
    // logName identifies the stage attempt, e.g. "tests.attempt2", so retries keep separate evidence.
    Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct);

    // Failures confined to these (new) test classes are tolerated, "red", provided the build and every other test pass.
    // Runners that cannot tell tests apart fall back to the strict check.
    Task<GateResult> ValidateAsync(string runDirectory, string logName, IReadOnlyCollection<string> tolerateFailuresIn, CancellationToken ct) =>
        ValidateAsync(runDirectory, logName, ct);
}

public sealed class AgentStage(StageDefinition definition, IAgent agent, FileChangeService files,
    string runsDirectory, IValidationRunner validation, JsonlAuditWriter audit, Action<Guid>? onRun = null) : IArtifactReviewStage, IFallbackStage
{
    public string Id => definition.Id;
    public IReadOnlyList<string> DependsOn => definition.DependsOn;
    public RiskLevel Risk => definition.Risk;
    private string DirectoryFor(RunContext ctx) => Path.Combine(runsDirectory, ctx.RunId.ToString("D"));
    public string GetArtifactPath(RunContext context) => Path.Combine(DirectoryFor(context), Id + ".json");
    private string ResultPath(RunContext ctx) => Path.Combine(DirectoryFor(ctx), Id + ".result.json");

    public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct)
    {
        onRun?.Invoke(ctx.RunId);
        Directory.CreateDirectory(DirectoryFor(ctx));
        return Task.FromResult(GateResult.Pass());
    }

    public async Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        // A stopped, rejected or human-edited draft is reused; a failed attempt or a stale stage is regenerated.
        var state = ctx.StageStatuses[Id];
        if (state.LastFailure is null && state.ReplanReason is null && File.Exists(GetArtifactPath(ctx)) && File.Exists(ResultPath(ctx)))
        {
            var previous = JsonSerializer.Deserialize<AgentResult>(await File.ReadAllTextAsync(ResultPath(ctx), ct), AgentDocument.Json);
            if (previous?.Output is not null)
            {
                // Re-landing an edited, already-accepted draft: only the new (human-edit) decisions are new lineage.
                var reviewed = await ReadReviewedOutputAsync(ctx, previous.Output, ct);
                var known = ctx.Decisions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                return reviewed with { Decisions = reviewed.Decisions.Where(d => !known.Contains(d.Id)).ToArray() };
            }
        }
        return await RunAgentAsync(ctx, simplified: false, ct);
    }

    public async Task<StageOutput> ExecuteFallbackAsync(RunContext ctx, FallbackLevel level, CancellationToken ct)
    {
        if (level == FallbackLevel.SimplerPrompt) return await RunAgentAsync(ctx, simplified: true, ct);
        var result = (agent as ITemplateAgent)?.CreateTemplate(CreateTask(ctx, false), ctx)
            ?? throw new InvalidOperationException($"No deterministic template exists for the {agent.Role} role.");
        return await SaveDraftAsync(ctx, result, ct);
    }

    private async Task<StageOutput> RunAgentAsync(RunContext ctx, bool simplified, CancellationToken ct)
    {
        var task = CreateTask(ctx, simplified, agent.Role == "Reviewer" ? await ValidateBeforeReviewAsync(ctx, ct) : null);
        await audit.WriteAsync(ctx.RunId, "agent_requested", definition, message: agent.Role + (ctx.Scenario.Model is { } model ? $" via {model}" : "") + (simplified ? " (simplified prompt)" : ""), ct: ct,
            promptHash: Artifact.Hash(JsonSerializer.Serialize(task, AgentDocument.Json)));
        return await SaveDraftAsync(ctx, await agent.RunAsync(task, ctx, ct), ct);
    }

    // The Reviewer judges the code as it is now: the full build and test run happens before the model is asked, so
    // the review never relies on earlier gate notes (a live review once asked to fix "red" tests that already passed).
    // A failing run sends the work back at once, without a model call.
    private async Task<string> ValidateBeforeReviewAsync(RunContext ctx, CancellationToken ct)
    {
        var result = await validation.ValidateAsync(DirectoryFor(ctx), $"{Id}.attempt{Math.Max(1, ctx.StageStatuses[Id].Attempts)}.before-review", ct);
        if (!result.Passed) throw new ReworkRequestedException($"The full build and test run failed at validation: {result.Reason}");
        return result.Reason;
    }

    private AgentTask CreateTask(RunContext ctx, bool simplified, string? currentRun = null)
    {
        var state = ctx.StageStatuses[Id];
        var prompt = $"Execute stage '{Id}' as {agent.Role}. Host gate evidence: " + string.Join("; ", DependsOn.Select(id => id + ": " + ctx.StageStatuses[id].Reason));
        if (currentRun is not null)
            prompt += $"\nFull build and test run of the current code, just before this review: {currentRun}. "
                + "The gate evidence above was recorded when each stage landed and may predate later changes; where they differ, this run is current.";
        if (state.LastFailure is { } failure) prompt += $"\nThe previous attempt failed. Fix this failure: {failure}";
        if (state.ReplanReason is { } replan) prompt += $"\nRe-plan: {replan}. Regenerate this stage from the current inputs.";
        return new AgentTask(Id, prompt, DependsOn.Select(id => ctx.Artifacts[id]).ToArray(), Math.Max(1, state.Attempts), simplified);
    }

    private async Task<StageOutput> SaveDraftAsync(RunContext ctx, AgentResult result, CancellationToken ct)
    {
        Directory.CreateDirectory(DirectoryFor(ctx));
        await File.WriteAllTextAsync(ResultPath(ctx), JsonSerializer.Serialize(result, AgentDocument.Json), ct);
        if (!result.Success || result.Output is null) throw new InvalidOperationException(result.Error ?? "Agent failed without output.");
        await File.WriteAllTextAsync(GetArtifactPath(ctx), result.Output.Artifact.Content, ct);
        return result.Output;
    }

    public async Task<StageOutput> ReadReviewedOutputAsync(RunContext ctx, StageOutput output, CancellationToken ct)
    {
        var document = AgentDocument.Parse(await File.ReadAllTextAsync(GetArtifactPath(ctx), ct));
        if (document.Files.Count > 0 && agent.Boundary.AllowedPaths.Count == 0) throw new InvalidDataException("This role cannot write files.");
        var artifact = new Artifact(output.Artifact.Name, document.ToJson());
        if (artifact.ContentHash == output.Artifact.ContentHash) return output;
        var decisions = output.Decisions.ToList();
        decisions.Add(new(Guid.NewGuid().ToString("N"), Id, document.Summary, "Human-edited artifact: " + document.Rationale,
            decisions.Select(d => d.Id).ToArray(), ctx.StageStatuses[Id].Approver ?? $"human-editor ({Environment.UserName})", DateTime.UtcNow));
        return new(artifact, decisions);
    }

    // Stages that change C# sources, and the Reviewer, must pass a real dotnet build and dotnet test.
    // The Tester's brand-new test classes may be red (they can be right and the code wrong); the Reviewer's full run
    // decides, and a failure there sends the work back for rework rather than repeating the same review.
    public async Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct)
    {
        var document = AgentDocument.Parse(output.Artifact.Content);
        var newTestClasses = agent.Role == "Tester" ? NewTestClasses(document) : [];
        await files.ApplyAsync(document.Files, agent.Boundary, ctx, Id, ct);
        var reason = "Agent JSON and file boundaries validated.";
        var changesCode = document.Files.Any(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (changesCode || agent.Role == "Reviewer")
        {
            // For the Reviewer this repeats the run made before the review: the gate checks what is actually landed.
            var result = await validation.ValidateAsync(DirectoryFor(ctx), $"{Id}.attempt{ctx.StageStatuses[Id].Attempts}", newTestClasses, ct);
            if (!result.Passed && agent.Role == "Reviewer")
                throw new ReworkRequestedException($"The full build and test run failed at validation: {result.Reason}");
            if (!result.Passed) return result;
            await audit.WriteAsync(ctx.RunId, "validation_passed", definition, message: result.Reason, ct: ct);
            reason = result.Reason;
        }
        if (agent.Role == "Reviewer")
        {
            // The review is binding: a FAIL sends the reviewed work back for rework, even when build and tests pass.
            var (passed, findings) = Verdicts.Review(document.Content);
            if (!passed) throw new ReworkRequestedException(findings);
            reason += " Review verdict: PASS.";
        }
        await File.WriteAllTextAsync(ResultPath(ctx), JsonSerializer.Serialize(new AgentResult(true, output), AgentDocument.Json), ct);
        foreach (var file in document.Files) ctx.ChangedFiles[file.Path] = Id;
        return GateResult.Pass(reason);
    }

    // Classes declared in test files this answer creates (not ones it edits): only these may land red.
    private string[] NewTestClasses(AgentDocument document) => document.Files
        .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !files.Exists(f.Path))
        .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(f.Content, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)").Select(m => m.Groups[1].Value))
        .Distinct(StringComparer.Ordinal).ToArray();
}
