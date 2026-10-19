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
        if (args.Length < 2 || args[0] is not ("run" or "resume" or "report" or "stop"))
        {
            AnsiConsole.WriteLine("Usage: run <scenario.yaml> [--replay] [--auto-approve] | resume <runId> | report <runId> [--lineage] | stop <runId>");
            return 1;
        }
        string[] allowed = args[0] switch { "run" => ["--replay", "--auto-approve"], "report" => ["--lineage"], _ => [] };
        if (args.Skip(2).Any(a => !allowed.Contains(a))) throw new ArgumentException("Unknown command option.");
        var workspace = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(workspace, "UrlShortenerAgentic.sln"))) throw new InvalidOperationException("Run the CLI from the solution root.");
        var runs = Path.Combine(workspace, "runs");
        var store = new SqliteRunStateStore(runs);
        RunContext? saved = null;
        if (args[0] != "run") saved = await store.LoadAsync(Guid.Parse(args[1])) ?? throw new KeyNotFoundException("Run not found.");
        if (args[0] == "report")
        {
            if (args.Contains("--lineage")) AnsiConsole.Write(new Text(LineageReport.Render(saved!)));
            AnsiConsole.WriteLine(await RunReport.WriteAsync(saved!, runs));
            return 0;
        }
        if (args[0] == "stop")
        {
            if (saved!.Status == RunStatus.Completed) throw new InvalidOperationException("Run is already completed.");
            await File.WriteAllTextAsync(Path.Combine(runs, saved.RunId.ToString("D"), "stop.request"), DateTime.UtcNow.ToString("O"));
            AnsiConsole.WriteLine("Stop requested. An active CLI observes it and saves a resumable checkpoint.");
            return 0;
        }
        var scenario = saved?.Scenario ?? await new ScenarioLoader().LoadAsync(args[1]);
        if (saved is null)
        {
            scenario.Name = Path.GetFileNameWithoutExtension(args[1]);
            scenario.Replay = args.Contains("--replay");
        }
        var autoApprove = args.Contains("--auto-approve");
        if (autoApprove && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("--auto-approve is restricted to tests/CI (CI=true).");
        // Replay never loads user-secrets or creates an HTTP client.
        var configuration = scenario.Replay ? new ConfigurationBuilder().Build() : ChatClientRegistration.LoadConfiguration();
        using var services = new ServiceCollection().AddScenarioChatClient(scenario, workspace, configuration).BuildServiceProvider();
        var client = services.GetRequiredService<IChatClient>();
        var approvals = new ConsoleApprovalService(autoApprove);
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
                AnsiConsole.WriteLine($"Run: {id} ({(scenario.Replay ? "replay" : "live")})");
                monitor = MonitorStopAsync(runs, id, stop, monitorStop.Token);
            }
            var agents = StageFactory.Agents(client, workspace);
            var validation = new DotnetValidationRunner(workspace, ProductTests);
            var stages = scenario.Stages.Select(definition => StageFactory.Create(definition, agents, files, runs, validation, audit, Started)).ToArray();
            var git = new GitWorkspace(workspace, DefaultPolicies.AgentRoots);
            var engine = new RunEngine(store, audit, approvals, versioning: git);
            var result = saved is null ? await engine.StartAsync(scenario, scenario.Requirement, stages, stop.Token)
                : await engine.ResumeAsync(saved.RunId, stages, stop.Token);
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
