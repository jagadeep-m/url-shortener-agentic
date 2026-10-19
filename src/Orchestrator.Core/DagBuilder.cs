using System.Text.RegularExpressions;

namespace Orchestrator.Core;

public static class DagBuilder
{
    public static IReadOnlyList<StageDefinition> Build(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Stages is null || scenario.Stages.Count == 0) throw new ScenarioException("Scenario must define at least one stage.");
        ValidateBudget(scenario.Budget, "scenario");
        ValidateRetry(scenario.Retry, "scenario");
        var definitions = new Dictionary<string, StageDefinition>(StringComparer.Ordinal);
        foreach (var stage in scenario.Stages)
        {
            if (stage is null || string.IsNullOrWhiteSpace(stage.Id) || !Regex.IsMatch(stage.Id, @"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z"))
                throw new ScenarioException("Stage IDs must start with an ASCII letter and contain up to 64 letters, digits, underscores or hyphens.");
            if (!definitions.TryAdd(stage.Id, stage)) throw new ScenarioException($"Duplicate stage ID '{stage.Id}'.");
            if (string.IsNullOrWhiteSpace(stage.Agent)) throw new ScenarioException($"Stage '{stage.Id}' must name an agent.");
            if (!Enum.IsDefined(stage.Risk)) throw new ScenarioException($"Stage '{stage.Id}' has an unknown risk level.");
            if (stage.DependsOn is null || stage.DependsOn.Any(string.IsNullOrWhiteSpace))
                throw new ScenarioException($"Stage '{stage.Id}' has invalid dependencies.");
            if (stage.DependsOn.Distinct(StringComparer.Ordinal).Count() != stage.DependsOn.Count)
                throw new ScenarioException($"Stage '{stage.Id}' repeats a dependency.");
            if (stage.Budget is not null) ValidateBudget(stage.Budget, stage.Id);
            if (stage.Retry is not null) ValidateRetry(stage.Retry, stage.Id);
        }
        foreach (var stage in scenario.Stages)
            foreach (var dependency in stage.DependsOn)
                if (!definitions.ContainsKey(dependency))
                    throw new ScenarioException($"Stage '{stage.Id}' references unknown dependency '{dependency}'.");

        var indexes = scenario.Stages.Select((stage, index) => (stage.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var incoming = scenario.Stages.ToDictionary(x => x.Id, x => x.DependsOn.Count);
        var successors = scenario.Stages.ToDictionary(x => x.Id, _ => new List<string>());
        foreach (var stage in scenario.Stages)
            foreach (var dependency in stage.DependsOn) successors[dependency].Add(stage.Id);
        var ready = new PriorityQueue<string, int>();
        foreach (var stage in scenario.Stages.Where(stage => incoming[stage.Id] == 0)) ready.Enqueue(stage.Id, indexes[stage.Id]);
        var ordered = new List<StageDefinition>();
        while (ready.TryDequeue(out var id, out _))
        {
            ordered.Add(definitions[id]);
            foreach (var next in successors[id])
                if (--incoming[next] == 0) ready.Enqueue(next, indexes[next]);
        }
        if (ordered.Count != scenario.Stages.Count)
            throw new ScenarioException($"Dependency cycle detected; unresolved stages: {string.Join(", ", scenario.Stages.Where(x => incoming[x.Id] > 0).Select(x => x.Id))}.");
        return ordered;
    }

    private static void ValidateBudget(RunBudget? budget, string owner)
    {
        if (budget is null || !double.IsFinite(budget.MaxMinutes) || budget.MaxMinutes <= 0 || budget.MaxTokens <= 0 || budget.TokensUsed < 0)
            throw new ScenarioException($"Invalid budget for '{owner}': minutes and tokens must be positive.");
    }

    private static void ValidateRetry(RetryOptions? retry, string owner)
    {
        if (retry is null || retry.MaxAttempts is < 1 or > 3 || retry.Backoff != "exponential"
            || !double.IsFinite(retry.BaseSeconds) || retry.BaseSeconds <= 0)
            throw new ScenarioException($"Invalid retry settings for '{owner}': maxAttempts 1–3, exponential backoff, positive baseSeconds required.");
    }
}
