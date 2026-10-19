using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Orchestrator.Core;

public sealed class ScenarioLoader
{
    public async Task<Scenario> LoadAsync(string path, CancellationToken ct = default) =>
        Parse(await File.ReadAllTextAsync(path, ct));

    public Scenario Parse(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        try
        {
            var scenario = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithDuplicateKeyChecking().Build().Deserialize<Scenario>(yaml)
                ?? throw new ScenarioException("Scenario YAML must contain a mapping with stages.");
            DagBuilder.Build(scenario);
            return scenario;
        }
        catch (YamlException exception)
        {
            throw new ScenarioException($"Invalid scenario YAML at line {exception.Start.Line + 1}: {exception.Message}", exception);
        }
    }
}
