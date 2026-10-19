using System.Text.Json;
using Orchestrator.Core;

namespace Orchestrator.Agents;

// Ambiguous requirements: the Clarifier writes runs/<runId>/open-questions.md with 2-3 interpretations per
// ambiguity and the run pauses for review. Each answer the human gives becomes a Decision; a changed answer
// later is an artifact edit, so the engine re-plans everything downstream.
public sealed class ClarificationStage(StageDefinition definition, IAgent agent, string runsDirectory, JsonlAuditWriter audit,
    Action<Guid>? onRun = null) : IArtifactReviewStage
{
    public string Id => definition.Id;
    public IReadOnlyList<string> DependsOn => definition.DependsOn;
    public RiskLevel Risk => definition.Risk;
    private string DirectoryFor(RunContext ctx) => Path.Combine(runsDirectory, ctx.RunId.ToString("D"));
    public string GetArtifactPath(RunContext context) => Path.Combine(DirectoryFor(context), "open-questions.md");
    private string ResultPath(RunContext ctx) => Path.Combine(DirectoryFor(ctx), Id + ".result.json");

    public Task<GateResult> CheckEntryAsync(RunContext ctx, CancellationToken ct)
    {
        onRun?.Invoke(ctx.RunId);
        Directory.CreateDirectory(DirectoryFor(ctx));
        return Task.FromResult(GateResult.Pass());
    }

    public async Task<StageOutput> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.StageStatuses[Id];
        if (state.LastFailure is null && state.ReplanReason is null && File.Exists(GetArtifactPath(ctx)) && File.Exists(ResultPath(ctx)))
        {
            // Same questionnaire, new or changed answers: answers are read after the review.
            var previous = JsonSerializer.Deserialize<AgentResult>(await File.ReadAllTextAsync(ResultPath(ctx), ct), AgentDocument.Json)?.Output;
            if (previous is not null)
            {
                var known = ctx.Decisions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                return previous with { Decisions = previous.Decisions.Where(d => !known.Contains(d.Id)).ToArray() };
            }
        }
        var task = new AgentTask(Id, $"List the ambiguities in the requirement as open questions ({agent.Role}).", DependsOn.Select(id => ctx.Artifacts[id]).ToArray(), Math.Max(1, state.Attempts));
        await audit.WriteAsync(ctx.RunId, "agent_requested", definition, message: agent.Role, ct: ct, promptHash: Artifact.Hash(JsonSerializer.Serialize(task, AgentDocument.Json)));
        var result = await agent.RunAsync(task, ctx, ct);
        if (!result.Success || result.Output is null) throw new InvalidOperationException(result.Error ?? "Clarifier failed without output.");
        var questions = OpenQuestions.Parse(AgentDocument.Parse(result.Output.Artifact.Content).Content).Select(q => q with { Answer = null }).ToArray();
        var output = new StageOutput(new Artifact("open-questions.md", OpenQuestions.Render(ctx.Requirement, questions)), result.Output.Decisions);
        Directory.CreateDirectory(DirectoryFor(ctx));
        await File.WriteAllTextAsync(ResultPath(ctx), JsonSerializer.Serialize(new AgentResult(true, output), AgentDocument.Json), ct);
        await File.WriteAllTextAsync(GetArtifactPath(ctx), output.Artifact.Content, ct);
        return output;
    }

    public async Task<StageOutput> ReadReviewedOutputAsync(RunContext ctx, StageOutput output, CancellationToken ct)
    {
        IReadOnlyList<OpenQuestion> answered;
        try { answered = OpenQuestions.Parse(await File.ReadAllTextAsync(GetArtifactPath(ctx), ct)); }
        catch (InvalidDataException exception) { throw new HumanInputRequiredException($"open-questions.md is not valid: {exception.Message}"); }
        var asked = OpenQuestions.Parse(output.Artifact.Content);
        if (!asked.Select(Shape).SequenceEqual(answered.Select(Shape)))
            throw new HumanInputRequiredException("Only fill in the Answer lines; the questions and options in open-questions.md must stay as asked.");
        var missing = answered.Where(q => q.Answer is null).Select(q => q.Id).ToArray();
        if (missing.Length > 0) throw new HumanInputRequiredException($"Answer {string.Join(", ", missing)} in open-questions.md, then approve (or resume).");

        var artifact = new Artifact("open-questions.md", OpenQuestions.Render(ctx.Requirement, answered));
        if (artifact.ContentHash == output.Artifact.ContentHash) return output;
        var accepted = ctx.Artifacts.TryGetValue(Id, out var current) ? OpenQuestions.Parse(current.Content).ToDictionary(q => q.Id, q => q.Answer) : [];
        var decisions = output.Decisions.ToList();
        var asker = decisions.Concat(ctx.Decisions).FirstOrDefault(d => d.StageId == Id && d.MadeBy == agent.Role);
        var who = ctx.StageStatuses[Id].Approver ?? $"human ({Environment.UserName})";
        foreach (var question in answered.Where(q => accepted.GetValueOrDefault(q.Id) != q.Answer))
        {
            var previous = ctx.Decisions.LastOrDefault(d => d.StageId == Id && d.Summary.StartsWith(question.Id + " = ", StringComparison.Ordinal));
            var parents = new[] { asker?.Id, previous?.Id }.OfType<string>().ToArray();
            var changed = accepted.GetValueOrDefault(question.Id) is { } before ? $" Changed from {before}." : "";
            decisions.Add(new(Guid.NewGuid().ToString("N"), Id, $"{question.Id} = {question.Answer}",
                $"Human answer to \"{question.Text}\": {OpenQuestions.Describe(question)}.{changed}", parents, who, DateTime.UtcNow));
        }
        return new(artifact, decisions);
    }

    public async Task<GateResult> CheckExitAsync(RunContext ctx, StageOutput output, CancellationToken ct)
    {
        // Rewrite the sheet in canonical form (answers summary included); re-plan detection ignores formatting.
        await File.WriteAllTextAsync(GetArtifactPath(ctx), output.Artifact.Content, ct);
        return GateResult.Pass($"{OpenQuestions.Parse(output.Artifact.Content).Count} open questions answered by a human.");
    }

    private static string Shape(OpenQuestion q) => q.Id + "|" + q.Text + "|" + string.Join("|", q.Options.Select(o => o.Letter + o.Text));
}
