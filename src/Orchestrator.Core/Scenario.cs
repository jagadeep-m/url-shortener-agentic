namespace Orchestrator.Core;

public sealed class Scenario
{
    public string Name { get; set; } = "scenario";
    public string Requirement { get; set; } = "Build the URL shortener described in the project specification.";
    [YamlDotNet.Serialization.YamlIgnore]
    public bool Replay { get; set; }
    public List<StageDefinition> Stages { get; set; } = [];
    public RetryOptions Retry { get; set; } = new();
    public RunBudget Budget { get; set; } = new();
}

public sealed class StageDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public List<string> DependsOn { get; set; } = [];
    public RiskLevel Risk { get; set; }
    public RetryOptions? Retry { get; set; }
    public RunBudget? Budget { get; set; }
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 3;
    public string Backoff { get; set; } = "exponential";
    public double BaseSeconds { get; set; } = 2;
}

public sealed class ScenarioException(string message, Exception? inner = null) : Exception(message, inner);
