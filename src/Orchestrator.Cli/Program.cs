using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Agents;
using Orchestrator.Core;
using Orchestrator.Policies;
using Spectre.Console;

namespace Orchestrator.Cli;

public static class Program
{
    // Agents change only the product, so gates build the whole solution but run the product's tests.
    private static readonly string[] ProductTests = ["tests/UrlShortener.UnitTests", "tests/UrlShortener.IntegrationTests"];

    public static async Task<int> Main(string[] args)
    {
        try { return await ExecuteAsync(args); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AnsiConsole.WriteLine("Error: " + exception.Message);
            return 1;
        }
    }
    private static async Task<int> ExecuteAsync(string[] args)
    {
        var command = CommandLine.Parse(args);
        var workspace = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(workspace, "UrlShortenerAgentic.sln"))) throw new InvalidOperationException("Run the CLI from the solution root.");
        var runs = Path.Combine(workspace, "runs");
        var store = new SqliteRunStateStore(runs);
        RunContext? saved = null;
        if (command.Command != "run") saved = await store.LoadAsync(Guid.Parse(command.Target)) ?? throw new KeyNotFoundException("Run not found.");
        if (command.Command == "report")
        {
            if (command.Lineage) AnsiConsole.Write(new Text(LineageReport.Render(saved!)));
            AnsiConsole.WriteLine(await RunReport.WriteAsync(saved!, runs));
            return 0;
        }
        if (command.Command == "stop")
        {
            if (saved!.Status == RunStatus.Completed) throw new InvalidOperationException("Run is already completed.");
            await File.WriteAllTextAsync(Path.Combine(runs, saved.RunId.ToString("D"), "stop.request"), DateTime.UtcNow.ToString("O"));
            AnsiConsole.WriteLine("Stop requested. An active CLI observes it and saves a resumable checkpoint.");
            return 0;
        }
        var scenario = saved?.Scenario ?? await new ScenarioLoader().LoadAsync(command.Target);
        if (saved is null)
        {
            scenario.Name = Path.GetFileNameWithoutExtension(command.Target);
            scenario.Replay = command.Replay;
            if (command.Requirement is not null) scenario.Requirement = command.Requirement;
            if (string.IsNullOrWhiteSpace(scenario.Requirement))
                throw new ArgumentException($"Scenario '{scenario.Name}' has no requirement; pass one with --requirement \"...\".");
        }
        var autoApprove = command.AutoApprove;
        if (autoApprove && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("--auto-approve is restricted to tests/CI (CI=true).");
        // Replay never loads user-secrets or creates an HTTP client.
        var configuration = scenario.Replay ? new ConfigurationBuilder().Build() : ChatClientRegistration.LoadConfiguration();
        if (saved is null && !scenario.Replay) scenario.Model = configuration["LLM:Model"];
        using var services = new ServiceCollection().AddScenarioChatClient(scenario, workspace, configuration).BuildServiceProvider();
        var client = services.GetRequiredService<IChatClient>();
        // Every approval (stage reviews and file-policy approvals) is timed, so the time budget counts machine time only.
        var humanWait = new HumanWaitClock();
        var approvals = new HumanWaitApprovalService(new ConsoleApprovalService(autoApprove), humanWait);
        using var audit = new JsonlAuditWriter(runs);
        var files = new FileChangeService(workspace, DefaultPolicies.Create(new DotnetVulnerabilityScanner()), approvals, audit);
        using var stop = new CancellationTokenSource();
        using var monitorStop = new CancellationTokenSource();
        Guid? activeRun = saved?.RunId;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        Task? monitor = null;
        try
        {
            // Only the lease owner may clear a previous stop request (see run_started callback below).
            var initialized = false;
            var startLock = new object();
            void Started(Guid id)
            {
                lock (startLock)
                {
                    activeRun = id;
                    if (initialized) return;
                    initialized = true;
                }
                File.Delete(Path.Combine(runs, id.ToString("D"), "stop.request"));
                AnsiConsole.WriteLine($"Run: {id} ({(scenario.Replay ? "replay" : $"live, model {scenario.Model}")})");
                monitor = MonitorStopAsync(runs, id, stop, monitorStop.Token);
            }
            var agents = StageFactory.Agents(client, workspace);
            var validation = new DotnetValidationRunner(workspace, ProductTests);
            var stages = scenario.Stages.Select(definition => StageFactory.Create(definition, agents, files, runs, validation, audit, Started)).ToArray();
            var git = new GitWorkspace(workspace, DefaultPolicies.AgentRoots);
            var engine = new RunEngine(store, audit, approvals, versioning: git, humanWait: humanWait);
            var result = saved is null ? await engine.StartAsync(scenario, scenario.Requirement, stages, stop.Token)
                : await engine.ResumeAsync(saved.RunId, stages, stop.Token, command.MaxTokens, Environment.UserName);
            AnsiConsole.WriteLine($"Run {result.RunId}: {result.Status}" + (result.StopReason is null ? "" : $" ({result.StopReason})"));
            AnsiConsole.WriteLine($"Branch: {result.GitBranch}");
            AnsiConsole.WriteLine($"Last good commit: {result.LastGoodCommit}");
            AnsiConsole.WriteLine(await RunReport.WriteAsync(result, runs));
            return result.Status == RunStatus.Completed ? 0 : 2;
        }
        finally
        {
            monitorStop.Cancel();
            if (monitor is not null) await monitor;
            Console.CancelKeyPress -= cancel;
        }
    }
    private static async Task MonitorStopAsync(string runs, Guid id, CancellationTokenSource stop, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(ct))
                if (File.Exists(Path.Combine(runs, id.ToString("D"), "stop.request"))) { stop.Cancel(); return; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
