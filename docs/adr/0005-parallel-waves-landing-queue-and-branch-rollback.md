# ADR 0005: Parallel waves, a landing queue and branch-per-run rollback

Status: Accepted for Phase 5. Supersedes the sequential scheduling part of ADR 0003.

**Waves.** Stages run in dependency waves awaited with `Task.WhenAll`. This is simple to reason
about and to explain, and it makes validate an explicit join. A fully dynamic scheduler would
start dependants sooner but makes failure and cancellation semantics harder to read in an audit.

**Landing queue.** All agents share one working tree, and a real `dotnet build` sees every file
in it. So model calls run in parallel, but exit gates, commits and reverts are serialized, and a
failed gate reverts its own files before the next stage lands. Every gate therefore judges a
tree made only of gate-passing changes plus that stage's proposal. One git worktree per stage
would isolate stages further, but would need merges and conflict handling.

**Rollback scope.** Each run works on `agent/run-<id>`, and every passing stage is a commit linked
to its run and decision. "Reset to the last good commit" applies only to agent-writable paths
(checkout from HEAD, delete new files), not a repository-wide `git reset --hard`, so a person's
uncommitted work elsewhere is never destroyed. The engine refuses to start on dirty agent paths
or staged changes. Merging the run branch stays a human decision after release approval.

**Retries and fallback.** Polly retries only attempt failures, with bounded exponential backoff,
and stores the reason so the next prompt can address it. The fallback order is a simplified
prompt, then a deterministic template, then a failed stage. Code roles have no template, because
a template that changes nothing would pass the build gate without doing the work.
