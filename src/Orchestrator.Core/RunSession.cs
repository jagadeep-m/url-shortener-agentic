namespace Orchestrator.Core;

// Shared state for one engine execution: context writes and checkpoints are serialized,
// and workspace changes (exit gates, commits, reverts) land one stage at a time.
internal sealed class RunSession(RunContext context, SqliteRunStateStore store, JsonlAuditWriter audit,
    TimeProvider clock, IWorkspaceVersioning? versioning) : IDisposable
{
    private readonly SemaphoreSlim state = new(1, 1);
    private readonly DateTime started = clock.GetUtcNow().UtcDateTime;
    public TimeSpan Elapsed => clock.GetUtcNow().UtcDateTime - started;
    public SemaphoreSlim Workspace { get; } = new(1, 1);
    public RunContext Context => context;
    public JsonlAuditWriter Audit => audit;
    public IWorkspaceVersioning? Versioning => versioning;
    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task UpdateAsync(Action<RunContext> change, bool save = true)
    {
        await state.WaitAsync(CancellationToken.None);
        try
        {
            change(context);
            if (save) await store.SaveAsync(context, CancellationToken.None);
        }
        finally { state.Release(); }
    }

    public void Dispose()
    {
        state.Dispose();
        Workspace.Dispose();
    }
}
