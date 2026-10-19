using System.Xml;
using System.Xml.Linq;
using Orchestrator.Core;

namespace Orchestrator.Agents;

// The only way agents write files. A change set is checked against the role's autonomy boundary,
// diffed against the workspace, and evaluated by every policy before any file is touched.
public sealed class FileChangeService(string workspace, IEnumerable<IPolicy> policies, IApprovalService approvals, JsonlAuditWriter? audit = null)
{
    private const int MaxDiffInPrompt = 6000;
    private readonly string root = Path.GetFullPath(workspace);
    private readonly IPolicy[] rules = policies.ToArray();

    public async Task ApplyAsync(IReadOnlyList<FileChange> changes, AutonomyBoundary boundary, RunContext context, string stageId, CancellationToken ct)
    {
        var paths = changes.Select(change => Resolve(change.Path)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new InvalidDataException("Duplicate file paths in change set.");
        if (changes.Count == 0) return;
        if (rules.Length == 0) throw new InvalidOperationException("At least one file policy must be registered.");
        var outside = changes.Where(c => !PathPatterns.IsUnderAny(c.Path, boundary.AllowedPaths)).Select(c => c.Path).ToArray();
        if (outside.Length > 0)
            throw new PolicyViolationException($"Outside this agent's autonomy boundary ({string.Join(", ", boundary.AllowedPaths)}): {string.Join(", ", outside)}");

        var proposed = await ProposeAsync(changes, paths, ct);
        var results = rules.Select(p => (p.Name, Result: p is IExplainablePolicy e ? e.Check(proposed, context) : new PolicyResult(p.Evaluate(proposed, context), "no detail"))).ToArray();
        var worst = results.Max(r => r.Result.Verdict);
        var summary = string.Join("; ", results.Select(r => $"{r.Name}={r.Result.Verdict} ({r.Result.Reason})"));
        await WriteAuditAsync(context, stageId, "policy_check", worst.ToString().ToLowerInvariant(), summary, null, Artifact.Hash(proposed.Diff));
        if (worst == PolicyVerdict.Deny)
            throw new PolicyViolationException("Denied by policy: " + string.Join("; ", results.Where(r => r.Result.Verdict == PolicyVerdict.Deny).Select(r => $"{r.Name}: {r.Result.Reason}")));
        if (worst == PolicyVerdict.RequireApproval)
        {
            var reasons = string.Join("\n", results.Where(r => r.Result.Verdict == PolicyVerdict.RequireApproval).Select(r => $"- {r.Name}: {r.Result.Reason}"));
            var diff = proposed.Diff.Length > MaxDiffInPrompt ? proposed.Diff[..MaxDiffInPrompt] + "\n... (diff truncated)" : proposed.Diff;
            await WriteAuditAsync(context, stageId, "policy_approval_requested", "require_approval", reasons, null, null);
            var approval = await approvals.RequestAsync(new(context.RunId, stageId, RiskLevel.High, $"File policy requires approval:\n{reasons}\n\n{diff}"), ct);
            await WriteAuditAsync(context, stageId, "policy_approval", approval.Approved ? "allow" : "deny", approval.Reason, approval.Approver, null);
            if (!approval.Approved) throw new PolicyViolationException($"File changes rejected by {approval.Approver}: {approval.Reason}", rejectedByHuman: true);
        }
        for (var i = 0; i < paths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            Resolve(changes[i].Path); // Recheck links immediately before writing.
            Directory.CreateDirectory(Path.GetDirectoryName(paths[i])!);
            var temporary = paths[i] + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, changes[i].Content, ct);
                File.Move(temporary, paths[i], overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static async Task<ProposedChange> ProposeAsync(IReadOnlyList<FileChange> changes, string[] paths, CancellationToken ct)
    {
        var diff = new System.Text.StringBuilder();
        var packages = new List<string>();
        for (var i = 0; i < changes.Count; i++)
        {
            // Decode raw bytes so a UTF-8 BOM is compared like any other character (EF migrations carry one).
            var old = File.Exists(paths[i]) ? System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(paths[i], ct)) : null;
            diff.Append(UnifiedDiff.Create(changes[i].Path, old, changes[i].Content).Text);
            if (changes[i].Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                packages.AddRange(PackageReferences(changes[i].Content).Except(old is null ? [] : PackageReferences(old), StringComparer.OrdinalIgnoreCase));
        }
        var migration = changes.Any(c => c.Path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) && c.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        return new ProposedChange(changes.Select(c => c.Path).ToArray(), diff.ToString(), packages, migration);
    }

    // "Id@Version" for each PackageReference; a version change counts as a new reference.
    private static IEnumerable<string> PackageReferences(string project)
    {
        try
        {
            return XDocument.Parse(project).Descendants().Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => $"{(string?)e.Attribute("Include") ?? (string?)e.Attribute("Update")}@{(string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? "*"}")
                .ToArray();
        }
        catch (XmlException exception) { throw new InvalidDataException("Proposed project file is not valid XML: " + exception.Message); }
    }

    private async Task WriteAuditAsync(RunContext context, string stageId, string eventName, string verdict, string message, string? approver, string? hash)
    {
        if (audit is null) return;
        var stage = context.Scenario.Stages.FirstOrDefault(s => s.Id == stageId) ?? new StageDefinition { Id = stageId };
        await audit.WriteAsync(context.RunId, eventName, stage, outputHash: hash, verdict: verdict, approver: approver, message: message);
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("File path must be a normalized relative path.");
        var full = Path.GetFullPath(Path.Combine(root, path));
        for (var current = full; current != root; current = Path.GetDirectoryName(current)!)
        {
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new InvalidDataException("Agent writes cannot traverse symbolic links.");
        }
        return full;
    }
}
