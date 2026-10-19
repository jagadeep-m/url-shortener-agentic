using System.Diagnostics;
using System.Text.RegularExpressions;
using Orchestrator.Core;

namespace Orchestrator.Agents;

// Runs dotnet build for the whole solution, then dotnet test for each test project, as child processes.
// Logs go to <logName>.build.log and <logName>.test.log. With no test projects, the solution's tests run.
public sealed class DotnetValidationRunner(string workspace, IReadOnlyList<string>? testProjects = null) : IValidationRunner
{
    public async Task<GateResult> ValidateAsync(string runDirectory, string logName, CancellationToken ct)
    {
        var build = await RunAsync("build", null, ct);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, $"{logName}.build.log"), build.Output, ct);
        if (build.ExitCode != 0)
            return GateResult.Block($"dotnet build failed: {DotnetOutput.BuildErrors(build.Output)} (see {logName}.build.log).");
        var output = new System.Text.StringBuilder();
        var failed = false;
        foreach (var project in testProjects is { Count: > 0 } ? testProjects.Select(p => (string?)p) : [null])
        {
            var test = await RunAsync("test", project, ct);
            output.AppendLine(test.Output);
            failed |= test.ExitCode != 0;
        }
        await File.WriteAllTextAsync(Path.Combine(runDirectory, $"{logName}.test.log"), output.ToString(), ct);
        var summary = DotnetOutput.TestSummary(output.ToString());
        if (failed) return GateResult.Block($"dotnet test failed: {summary.Text} (see {logName}.test.log).");
        if (summary.Total == 0) return GateResult.Block($"dotnet test reported no results (see {logName}.test.log).");
        return GateResult.Pass($"dotnet build succeeded; dotnet test: {summary.Text}.");
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string command, string? project, CancellationToken ct)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = workspace, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.ArgumentList.Add(command);
        if (project is not null) start.ArgumentList.Add(project);
        start.ArgumentList.Add("--no-restore");
        if (command == "test") start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--verbosity");
        start.ArgumentList.Add("minimal");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await stdout + "\n" + await stderr);
    }
}

public sealed record TestSummary(int Passed, int Failed, int Skipped, IReadOnlyList<string> FailingTests)
{
    public int Total => Passed + Failed + Skipped;
    public string Text => $"{Passed} passed, {Failed} failed, {Skipped} skipped"
        + (FailingTests.Count > 0 ? "; failing: " + string.Join(", ", FailingTests) : "");
}

// Parses dotnet CLI output so gate reasons (and retry prompts) carry concrete evidence.
public static class DotnetOutput
{
    public static TestSummary TestSummary(string output)
    {
        int passed = 0, failed = 0, skipped = 0;
        foreach (Match m in Regex.Matches(output, @"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)"))
        {
            failed += int.Parse(m.Groups[1].Value);
            passed += int.Parse(m.Groups[2].Value);
            skipped += int.Parse(m.Groups[3].Value);
        }
        var failing = Regex.Matches(output, @"^\s*Failed (\S+) \[", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().Take(5).ToArray();
        return new(passed, failed, skipped, failing);
    }

    public static string BuildErrors(string output)
    {
        var errors = Regex.Matches(output, @"error [A-Z]+\d+: [^\r\n\[]+").Select(m => m.Value.Trim()).Distinct().Take(3).ToArray();
        return errors.Length > 0 ? string.Join("; ", errors) : "see log";
    }
}
