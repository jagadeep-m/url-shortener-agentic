using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public sealed record ImpactProposal(string Summary, string Rationale, IReadOnlyList<string> Seeds, IReadOnlyList<string> Risks);

// Brownfield impact analysis. The model proposes seed types/members; the host grounds them in a Roslyn
// index of the current code and computes affected files, endpoints, tables and tests itself.
public sealed class ImpactAnalystAgent(IChatClient client, string workspace) : SdlcAgent(client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public override string Role => "ImpactAnalyst";
    public override string RolePrompt => "Identify where the approved change lands in the existing codebase. Name the C# types and members (Type or Type.Member) that must change and the main risks. Do not design or implement.";
    public override AutonomyBoundary Boundary => new([], ["CodebaseIndex"], 4096);
    protected override string OutputContract => "Return only JSON: {\"summary\":\"...\",\"rationale\":\"...\",\"seeds\":[\"Type\",\"Type.Member\"],\"risks\":[\"...\"]}. ";

    protected override AgentDocument Interpret(string json, AgentTask task, RunContext ctx)
    {
        var proposal = JsonSerializer.Deserialize<ImpactProposal>(json, Json) ?? throw new InvalidDataException("Impact JSON is null.");
        if (string.IsNullOrWhiteSpace(proposal.Summary) || string.IsNullOrWhiteSpace(proposal.Rationale) || proposal.Seeds is not { Count: > 0 } || proposal.Risks is null)
            throw new InvalidDataException("Impact JSON requires summary, rationale, at least one seed and a risks array.");
        var report = CodebaseIndex.Build(workspace, ["src/UrlShortener.*", "tests/UrlShortener.*"]).Analyze(proposal.Seeds);
        if (report.UnknownSeeds.Count == report.Seeds.Count) throw new InvalidDataException("None of the proposed seeds exist in the codebase: " + string.Join(", ", report.UnknownSeeds));
        var risks = proposal.Risks.Count == 0 ? "- none identified" : string.Join("\n", proposal.Risks.Select(r => "- " + r));
        return new(proposal.Summary, proposal.Rationale, report.ToMarkdown() + "\n## Risks (agent)\n\n" + risks + "\n", []);
    }
}

// Requirements clarification: the Requirements role, asked to list ambiguities instead of resolving them.
public sealed class ClarifierAgent(IChatClient client) : SdlcAgent(client)
{
    public override string Role => "Clarifier";
    public override string RolePrompt => "Act as the Requirements agent for a vague request. Do not resolve ambiguity yourself: list each ambiguity as a question with 2-3 concrete interpretations, using the open-questions format "
        + "(\"## Q<n>. <question>\", optional \"Why it matters: ...\", options \"- A. ...\", \"- B. ...\", then an empty \"Answer:\" line).";
    public override AutonomyBoundary Boundary => new([], [], 4096);
    protected override AgentDocument Interpret(string json, AgentTask task, RunContext ctx)
    {
        var document = AgentDocument.Parse(json);
        OpenQuestions.Parse(document.Content); // the gate: 2-3 interpretations per question, unique ids
        return document;
    }
}
