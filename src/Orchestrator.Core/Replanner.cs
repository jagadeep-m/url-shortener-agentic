namespace Orchestrator.Core;

// Finds accepted artifacts whose reviewed file was edited after the stage completed, and the
// stages downstream of them. Comparison uses the stage's own normalisation (ReadReviewedOutputAsync),
// so formatting-only edits do not trigger a re-plan.
internal static class Replanner
{
    public sealed record Change(string StageId, string OldHash, string NewHash);

    public static async Task<IReadOnlyList<Change>> DetectAsync(RunContext ctx, IReadOnlyList<StageDefinition> ordered,
        IReadOnlyDictionary<string, IStage> stages, CancellationToken ct)
    {
        var changes = new List<Change>();
        foreach (var definition in ordered)
        {
            if (ctx.StageStatuses[definition.Id].Status != StageStatus.Completed || stages[definition.Id] is not IArtifactReviewStage review
                || !ctx.Artifacts.TryGetValue(definition.Id, out var accepted) || !File.Exists(review.GetArtifactPath(ctx))) continue;
            var current = new StageOutput(accepted, ctx.Decisions.Where(d => d.StageId == definition.Id).ToArray());
            StageOutput reviewed;
            try { reviewed = await review.ReadReviewedOutputAsync(ctx, current, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidDataException($"Edited artifact for stage '{definition.Id}' is invalid: {exception.Message}", exception);
            }
            if (reviewed.Artifact.ContentHash != accepted.ContentHash) changes.Add(new(definition.Id, accepted.ContentHash, reviewed.Artifact.ContentHash));
        }
        return changes;
    }

    // Transitive dependants of the changed stages, in topological order.
    public static IReadOnlyList<string> Downstream(IReadOnlyList<StageDefinition> ordered, IEnumerable<string> changed)
    {
        var affected = new HashSet<string>(changed, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var stage in ordered)
            if (!affected.Contains(stage.Id) && stage.DependsOn.Any(affected.Contains))
            {
                affected.Add(stage.Id);
                result.Add(stage.Id);
            }
        return result;
    }
}
