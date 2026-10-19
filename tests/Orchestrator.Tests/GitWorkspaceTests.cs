using LibGit2Sharp;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string repoPath = Path.Combine(Path.GetTempPath(), $"git-{Guid.NewGuid():N}");
    private RunContext Context() => new() { RunId = Guid.NewGuid() };

    internal static string InitRepo(string path, params (string Path, string Content)[] files)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        foreach (var (file, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(path, file))!);
            File.WriteAllText(Path.Combine(path, file), content);
            Commands.Stage(repo, file);
        }
        var user = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
        return repo.Commit("initial", user, user).Sha;
    }

    // LibGit2Sharp marks object files read-only, which Directory.Delete refuses on some platforms.
    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public async Task Begin_creates_the_run_branch_and_commits_only_agent_paths()
    {
        var initial = InitRepo(repoPath, ("docs/a.md", "a"), ("notes.txt", "user"));
        var git = new GitWorkspace(repoPath, ["docs"]);
        var ctx = Context();
        await git.BeginAsync(ctx, default);
        Assert.Equal(initial, ctx.LastGoodCommit);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "docs/b.md"), "b");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "notes.txt"), "user edit");
        var sha = await git.CommitStageAsync(ctx, "docs", "decision-1", default);

        using var repo = new Repository(repoPath);
        Assert.Equal($"agent/run-{ctx.RunId:D}", repo.Head.FriendlyName);
        Assert.Equal(sha, repo.Head.Tip.Sha);
        Assert.NotNull(repo.Head.Tip["docs/b.md"]);
        Assert.Equal("user", ((Blob)repo.Head.Tip["notes.txt"].Target).GetContentText());
        Assert.Contains("decision decision-1", repo.Head.Tip.Message);
        Assert.Equal(FileStatus.ModifiedInWorkdir, repo.RetrieveStatus("notes.txt"));
    }

    [Fact]
    public async Task Revert_restores_modified_files_and_removes_new_ones_under_agent_paths()
    {
        InitRepo(repoPath, ("docs/a.md", "a"));
        var git = new GitWorkspace(repoPath, ["docs"]);
        var ctx = Context();
        await git.BeginAsync(ctx, default);
        await File.WriteAllTextAsync(Path.Combine(repoPath, "docs/a.md"), "broken");
        Directory.CreateDirectory(Path.Combine(repoPath, "docs/new"));
        await File.WriteAllTextAsync(Path.Combine(repoPath, "docs/new/c.md"), "c");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "outside.txt"), "keep");

        var reverted = await git.RevertToLastGoodAsync(ctx, "docs", default);

        Assert.Equal(ctx.LastGoodCommit, reverted.Commit);
        Assert.Equal(new[] { "docs/a.md", "docs/new/c.md" }, reverted.Files.Order());
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(repoPath, "docs/a.md")));
        Assert.False(File.Exists(Path.Combine(repoPath, "docs/new/c.md")));
        Assert.True(File.Exists(Path.Combine(repoPath, "outside.txt")));
    }

    [Fact]
    public async Task Begin_refuses_dirty_agent_paths_staged_changes_and_empty_repositories()
    {
        Repository.Init(repoPath);
        var git = new GitWorkspace(repoPath, ["docs"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.BeginAsync(Context(), default));
        DeleteDirectory(repoPath);

        InitRepo(repoPath, ("docs/a.md", "a"), ("other.txt", "o"));
        await File.WriteAllTextAsync(Path.Combine(repoPath, "docs/a.md"), "uncommitted");
        var dirty = await Assert.ThrowsAsync<InvalidOperationException>(() => git.BeginAsync(Context(), default));
        Assert.Contains("docs/a.md", dirty.Message);

        await File.WriteAllTextAsync(Path.Combine(repoPath, "docs/a.md"), "a");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "other.txt"), "staged");
        using (var repo = new Repository(repoPath)) Commands.Stage(repo, "other.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.BeginAsync(Context(), default));
    }

    [Fact]
    public async Task Commit_refuses_when_head_left_the_run_branch()
    {
        InitRepo(repoPath, ("docs/a.md", "a"));
        var git = new GitWorkspace(repoPath, ["docs"]);
        var ctx = Context();
        await git.BeginAsync(ctx, default);
        using (var repo = new Repository(repoPath)) Commands.Checkout(repo, repo.Branches["master"] ?? repo.Branches["main"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.CommitStageAsync(ctx, "docs", null, default));
    }

    public void Dispose() => DeleteDirectory(repoPath);
}
