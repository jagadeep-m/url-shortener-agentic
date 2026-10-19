using System.Diagnostics;
using Orchestrator.Core;
using Spectre.Console;

namespace Orchestrator.Cli;

public sealed class ConsoleApprovalService(bool autoApprove, TextReader? input = null, Action<string>? openArtifact = null) : IApprovalService
{
    private static readonly SemaphoreSlim console = new(1, 1);
    public async Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (autoApprove)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("--auto-approve is restricted to tests/CI (CI=true).");
            return new(true, "ci-auto-approve", "Explicit CI approval.");
        }
        // Parallel stages and file policies share one console, so prompts are asked one at a time.
        await console.WaitAsync(ct);
        try { return await PromptAsync(r, ct); }
        finally { console.Release(); }
    }
    private async Task<ApprovalResult> PromptAsync(ApprovalRequest r, CancellationToken ct)
    {
        AnsiConsole.Write(new Rule($"Approval: {r.StageId}"));
        AnsiConsole.WriteLine(r.Reason);
        if (r.ArtifactPath is not null)
        {
            AnsiConsole.WriteLine("Artifact: " + r.ArtifactPath);
            AnsiConsole.WriteLine(await File.ReadAllTextAsync(r.ArtifactPath, ct));
        }
        while (true)
        {
            AnsiConsole.Write("approve / reject / edit > ");
            var answer = await Task.Run(() => (input ?? Console.In).ReadLine(), CancellationToken.None).WaitAsync(ct);
            switch (answer?.Trim().ToLowerInvariant())
            {
                case "approve": return new(true, Environment.UserName, "Approved from console.");
                case "reject": return new(false, Environment.UserName, "Rejected from console; edit draft and resume.");
                case null: return new(false, Environment.UserName, "Input closed; explicit approval is required.");
                case "edit":
                    if (r.ArtifactPath is null) { AnsiConsole.WriteLine("No artifact is available to edit."); break; }
                    (openArtifact ?? OpenArtifact)(r.ArtifactPath);
                    AnsiConsole.WriteLine("Save the artifact, then choose approve or reject. JSON is validated again after approval.");
                    break;
                default: AnsiConsole.WriteLine("Enter approve, reject or edit."); break;
            }
        }
    }
    private static void OpenArtifact(string path)
    {
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(editor))
        {
            // EDITOR is an executable path; arguments/shell expansions are deliberately not interpreted.
            var start = new ProcessStartInfo(editor) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            process?.WaitForExit();
        }
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
