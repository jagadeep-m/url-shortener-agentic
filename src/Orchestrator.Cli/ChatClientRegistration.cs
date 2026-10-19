using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Agents;
using Orchestrator.Core;

namespace Orchestrator.Cli;

public static class ChatClientRegistration
{
    public const string SecretsId = "url-shortener-orchestrator-phase4";
    public static IServiceCollection AddScenarioChatClient(this IServiceCollection services, Scenario scenario, string workspace, IConfiguration configuration)
    {
        services.AddSingleton<IChatClient>(_ => scenario.Replay
            ? new ReplayChatClient(Path.Combine(workspace, "scenarios", "recordings"), scenario.Name)
            : new OpenAiCompatibleChatClient(configuration["LLM:ApiKey"] ?? throw new InvalidOperationException("Set LLM__APIKEY or user-secret LLM:ApiKey for live mode."),
                configuration["LLM:Model"] ?? throw new InvalidOperationException("Set LLM__MODEL or user-secret LLM:Model to a model your account can access."),
                configuration["LLM:Endpoint"] ?? "https://api.openai.com/v1/chat/completions"));
        return services;
    }
    public static IConfiguration LoadConfiguration()
    {
        // Use the same UserSecretsId and platform path as dotnet user-secrets, without adding a provider package.
        var basePath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");
        var path = Path.Combine(basePath, SecretsId, "secrets.json");
        var values = new Dictionary<string, string?>();
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Flatten(document.RootElement, "", values);
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).AddEnvironmentVariables().Build();
    }
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> values)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object) Flatten(property.Value, key + ":", values);
            else if (property.Value.ValueKind == JsonValueKind.String) values[key] = property.Value.GetString();
        }
    }
}
