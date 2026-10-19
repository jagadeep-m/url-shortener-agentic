using System.Text.Json;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

public sealed class ScenarioTests
{
    internal const string Sample = """
        stages:
          - id: validate
            agent: ReviewerAgent
            dependsOn: [implement, tests, docs]
            risk: Low
          - id: requirements
            agent: RequirementsAgent
            dependsOn: []
            risk: High
          - id: implement
            agent: CoderAgent
            dependsOn: [requirements]
            risk: Medium
            retry: { maxAttempts: 2, backoff: exponential, baseSeconds: 1 }
          - id: tests
            agent: TesterAgent
            dependsOn: [requirements]
            risk: Low
          - id: docs
            agent: DocsAgent
            dependsOn: [requirements]
            risk: Low
        retry: { maxAttempts: 3, backoff: exponential, baseSeconds: 2 }
        budget: { maxMinutes: 20, maxTokens: 400000 }
        """;

    [Fact]
    public void TopologicalSortHandlesUnorderedNodesAndStableIndependentBranches()
    {
        var scenario = new ScenarioLoader().Parse(Sample);
        Assert.Equal(new[] { "requirements", "implement", "tests", "docs", "validate" }, DagBuilder.Build(scenario).Select(x => x.Id));
        Assert.Equal(RiskLevel.High, scenario.Stages.Single(x => x.Id == "requirements").Risk);
        Assert.Equal(2, scenario.Stages.Single(x => x.Id == "implement").Retry!.MaxAttempts);
        Assert.Equal(400_000, scenario.Budget.MaxTokens);
    }

    [Fact]
    public async Task LoadsYamlFromFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Sample);
            Assert.Equal(5, (await new ScenarioLoader().LoadAsync(file)).Stages.Count);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RejectsUnknownDependenciesWithStageAndDependencyNames()
    {
        var exception = Assert.Throws<ScenarioException>(() => new ScenarioLoader().Parse(Sample.Replace("dependsOn: [requirements]", "dependsOn: [missing]")));
        Assert.Contains("implement", exception.Message);
        Assert.Contains("unknown dependency 'missing'", exception.Message);
    }

    [Fact]
    public void RejectsCycle()
    {
        var exception = Assert.Throws<ScenarioException>(() => new ScenarioLoader().Parse(Sample.Replace("dependsOn: []", "dependsOn: [validate]")));
        Assert.Contains("cycle", exception.Message);
        Assert.Contains("requirements", exception.Message);
    }

    [Fact]
    public void RejectsSelfCycle()
    {
        var exception = Assert.Throws<ScenarioException>(() => new ScenarioLoader().Parse("stages: [{id: a, agent: Agent, dependsOn: [a]}]"));
        Assert.Contains("cycle", exception.Message);
    }

    [Theory]
    [InlineData("stages: [{id: a, agent: Agent}, {id: a, agent: Agent}]")]
    [InlineData("stages: [{id: a, agent: Agent, dependsOn: [b, b]}, {id: b, agent: Agent}]")]
    [InlineData("stages: [{id: a}]")]
    [InlineData("stages: []")]
    [InlineData("stages: null")]
    [InlineData("stages: [{id: a, agent: Agent, risk: Unknown}]")]
    [InlineData("stages: [{id: a, agent: Agent}]\nbudget: {maxTokens: 0}")]
    [InlineData("stages: [{id: a, agent: Agent}]\nretry: {maxAttempts: 4}")]
    [InlineData("stages: [{id: a, agent: Agent, typo: true}]")]
    [InlineData("stages: [{id: a, id: b, agent: Agent}]")]
    [InlineData("stages: [{id: ../escape, agent: Agent}]")]
    public void RejectsInvalidScenario(string yaml) => Assert.Throws<ScenarioException>(() => new ScenarioLoader().Parse(yaml));

    [Fact]
    public void ArtifactHashUsesUtf8Sha256AndSurvivesJsonRoundTrip()
    {
        var artifact = new Artifact("design.md", "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", artifact.ContentHash);
        var restored = JsonSerializer.Deserialize<Artifact>(JsonSerializer.Serialize(artifact))!;
        Assert.Equal(artifact.ContentHash, restored.ContentHash);
        Assert.Equal("abc", restored.Content);
        Assert.NotEqual(artifact.ContentHash, new Artifact("design.md", "abc\n").ContentHash);
    }

    [Fact]
    public void ArtifactRejectsMismatchedStoredHash() =>
        Assert.Throws<ArgumentException>(() => new Artifact("design.md", "changed", Artifact.Hash("original")));
}
