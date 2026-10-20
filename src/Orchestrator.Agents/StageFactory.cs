using Microsoft.Extensions.AI;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public static class StageFactory
{
    // Every agent gets the same read-only CodeContext; each role decides what it needs from it (SdlcAgent.Needs).
    public static IAgent[] Agents(IChatClient client, string workspace)
    {
        var code = new CodeContext(workspace);
        return
        [
            new RequirementsAgent(client) { Code = code }, new ClarifierAgent(client) { Code = code }, new ImpactAnalystAgent(client, workspace) { Code = code },
            new ArchitectAgent(client) { Code = code }, new CoderAgent(client) { Code = code }, new TesterAgent(client) { Code = code },
            new DocsAgent(client) { Code = code }, new ReviewerAgent(client) { Code = code }, new ReleaseAgent(client) { Code = code }
        ];
    }

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
