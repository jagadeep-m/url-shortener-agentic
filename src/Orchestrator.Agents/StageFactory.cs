using Microsoft.Extensions.AI;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public static class StageFactory
{
    public static IAgent[] Agents(IChatClient client, string workspace) =>
    [
        new RequirementsAgent(client), new ClarifierAgent(client), new ImpactAnalystAgent(client, workspace), new ArchitectAgent(client),
        new CoderAgent(client), new TesterAgent(client), new DocsAgent(client), new ReviewerAgent(client), new ReleaseAgent(client)
    ];

    // The scenario names the agent; the Clarifier gets the open-questions stage, every other role an AgentStage.
    public static IStage Create(StageDefinition definition, IReadOnlyList<IAgent> agents, FileChangeService files, string runsDirectory,
        IValidationRunner validation, JsonlAuditWriter audit, Action<Guid>? onRun = null)
    {
        var agent = agents.SingleOrDefault(a => a.Role.Equals(definition.Agent, StringComparison.OrdinalIgnoreCase))
            ?? throw new ScenarioException($"Unknown agent '{definition.Agent}' for stage '{definition.Id}'.");
        return agent is ClarifierAgent
            ? new ClarificationStage(definition, agent, runsDirectory, audit, onRun)
            : new AgentStage(definition, agent, files, runsDirectory, validation, audit, onRun);
    }
}
