using System.Text.Json;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public interface IValidationRunner
{
    // logName identifies the stage attempt, e.g. "tests.attempt2", so retries keep separate evidence.
    Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct);
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
        var task = CreateTask(ctx, simplified);
        await audit.WriteAsync(ctx.RunId, "agent_requested", definition, message: agent.Role + (ctx.Scenario.Model is { } model ? $" via {model}" : "") + (simplified ? " (simplified prompt)" : ""), ct: ct,
            promptHash: Artifact.Hash(JsonSerializer.Serialize(task, AgentDocument.Json)));
        return await SaveDraftAsync(ctx, await agent.RunAsync(task, ctx, ct), ct);
    }

    private AgentTask CreateTask(RunContext ctx, bool simplified)
    {
        var state = ctx.StageStatuses[Id];
        var prompt = $"Execute stage '{Id}' as {agent.Role}. Host gate evidence: " + string.Join("; ", DependsOn.Select(id => id + ": " + ctx.StageStatuses[id].Reason));
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
    public async Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct)
    {
        var document = AgentDocument.Parse(output.Artifact.Content);
        await files.ApplyAsync(document.Files, agent.Boundary, ctx, Id, ct);
        var reason = "Agent JSON and file boundaries validated.";
        var changesCode = document.Files.Any(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (changesCode || agent.Role == "Reviewer")
        {
            var result = await validation.ValidateAsync(DirectoryFor(ctx), $"{Id}.attempt{ctx.StageStatuses[Id].Attempts}", ct);
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
        return GateResult.Pass(reason);
    }
}
