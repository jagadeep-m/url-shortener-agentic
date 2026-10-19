using LibGit2Sharp;

namespace Orchestrator.Core;

// Branch-per-run versioning with LibGit2Sharp. Each run works on agent/run-<id>; every successful
// stage is committed, and a failed gate resets the agent-writable paths to the last good commit.
// Files outside those paths are never staged, committed or reset.
public sealed class GitWorkspace : IWorkspaceVersioning
{
    private const FileStatus Staged = FileStatus.NewInIndex | FileStatus.ModifiedInIndex | FileStatus.DeletedFromIndex
        | FileStatus.RenamedInIndex | FileStatus.TypeChangeInIndex;
    private readonly string root;
    private readonly IReadOnlyList<string> agentRoots;
    private readonly TimeProvider clock;

    public GitWorkspace(string workspace, IReadOnlyList<string> agentRoots, TimeProvider? clock = null)
    {
        if (agentRoots.Count == 0) throw new ArgumentException("At least one agent path root is required.", nameof(agentRoots));
        root = Path.GetFullPath(workspace);
        this.agentRoots = agentRoots;
        this.clock = clock ?? TimeProvider.System;
    }

    public static string BranchName(Guid runId) => $"agent/run-{runId:D}";

    public Task BeginAsync(RunContext ctx, CancellationToken ct)
    {
        if (!Repository.IsValid(root)) throw new InvalidOperationException($"'{root}' is not a git repository; branch-per-run rollback needs one.");
        using var repo = new Repository(root);
        if (repo.Head.Tip is null) throw new InvalidOperationException("The git repository needs at least one commit before an agent run.");
        EnsureClean(repo);
        ctx.BaseCommit ??= repo.Head.Tip.Sha;
        var name = BranchName(ctx.RunId);
        if (repo.Head.FriendlyName != name) Commands.Checkout(repo, repo.Branches[name] ?? repo.CreateBranch(name));
        ctx.GitBranch = name;
        ctx.LastGoodCommit = repo.Head.Tip.Sha;
        return Task.CompletedTask;
    }

    public Task<string> CommitStageAsync(RunContext ctx, string stageId, string? decisionId, CancellationToken ct)
    {
        using var repo = Open(ctx);
        var changed = AgentChanges(repo).Select(e => e.FilePath).ToArray();
        if (changed.Length > 0) Commands.Stage(repo, changed);
        var agent = new Signature("orchestrator-agent", "agent@orchestrator.local", clock.GetUtcNow());
        var message = $"agent({stageId}): run {ctx.RunId:D}, decision {decisionId ?? "none"}\n\n{changed.Length} file(s) changed by stage '{stageId}'.";
        var commit = repo.Commit(message, agent, agent, new CommitOptions { AllowEmptyCommit = true });
        return Task.FromResult(commit.Sha);
    }

    public Task<RevertResult> RevertToLastGoodAsync(RunContext ctx, string stageId, CancellationToken ct)
    {
        using var repo = Open(ctx);
        var head = repo.Head.Tip;
        var changes = AgentChanges(repo).ToArray();
        foreach (var entry in changes)
        {
            if (head[entry.FilePath] is not null)
            {
                repo.CheckoutPaths(head.Sha, [entry.FilePath], new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
                continue;
            }
            if (entry.State.HasFlag(FileStatus.NewInIndex)) Commands.Unstage(repo, entry.FilePath);
            File.Delete(Path.Combine(root, entry.FilePath));
        }
        return Task.FromResult(new RevertResult(head.Sha, changes.Select(e => e.FilePath).ToArray()));
    }

    public Task<string> DiffSinceStartAsync(RunContext ctx, CancellationToken ct)
    {
        using var repo = new Repository(root);
        var start = ctx.BaseCommit is null ? null : repo.Lookup<Commit>(ctx.BaseCommit);
        var end = ctx.GitBranch is null ? null : repo.Branches[ctx.GitBranch]?.Tip;
        if (start is null || end is null) return Task.FromResult("");
        return Task.FromResult(repo.Diff.Compare<Patch>(start.Tree, end.Tree).Content);
    }

    private Repository Open(RunContext ctx)
    {
        var repo = new Repository(root);
        if (ctx.GitBranch is null || repo.Head.FriendlyName != ctx.GitBranch)
        {
            var actual = repo.Head.FriendlyName;
            repo.Dispose();
            throw new InvalidOperationException($"HEAD is on '{actual}', not the run branch '{ctx.GitBranch}'; refusing to commit or reset.");
        }
        return repo;
    }

    private void EnsureClean(Repository repo)
    {
        var dirty = Status(repo).Where(e => (e.State & Staged) != 0 || PathPatterns.IsUnderAny(e.FilePath, agentRoots))
            .Select(e => e.FilePath).Take(5).ToArray();
        if (dirty.Length > 0)
            throw new InvalidOperationException("Commit or stash these changes before an agent run (agent paths must be clean and nothing staged): "
                + string.Join(", ", dirty));
    }

    private IEnumerable<StatusEntry> AgentChanges(Repository repo) => Status(repo).Where(e => PathPatterns.IsUnderAny(e.FilePath, agentRoots));

    private static IEnumerable<StatusEntry> Status(Repository repo) =>
        repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true, IncludeIgnored = false })
            .Where(e => e.State is not (FileStatus.Unaltered or FileStatus.Ignored));
}
