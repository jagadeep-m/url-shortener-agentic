namespace Orchestrator.Core;

// Measures time spent waiting for people (approvals, reviews, answers), so a run's time budget can limit
// machine time only. Overlapping waits are counted once.
public sealed class HumanWaitClock(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private int open;
    private long since;
    private TimeSpan total;

    public TimeSpan Total
    {
        get { lock (gate) return open > 0 ? total + time.GetElapsedTime(since) : total; }
    }

    public IDisposable Measure()
    {
        lock (gate) if (open++ == 0) since = time.GetTimestamp();
        return new Scope(this);
    }

    private void End()
    {
        lock (gate) if (--open == 0) total += time.GetElapsedTime(since);
    }

    private sealed class Scope(HumanWaitClock owner) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.End(); }
    }
}

// Wraps any approval service so every human wait is measured, whoever asks: the engine or a file policy.
public sealed class HumanWaitApprovalService(IApprovalService inner, HumanWaitClock waits) : IApprovalService
{
    public async Task<ApprovalResult> RequestAsync(ApprovalRequest r, CancellationToken ct)
    {
        using var _ = waits.Measure();
        return await inner.RequestAsync(r, ct);
    }
}
