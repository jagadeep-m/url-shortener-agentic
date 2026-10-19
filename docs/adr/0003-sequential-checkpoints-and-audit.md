# ADR 0003: Sequential execution with SQLite checkpoints and JSONL audit

Status: Accepted for Phase 3. The sequential-scheduling part is superseded by ADR 0005; checkpoints and the audit trail still apply.

Keep scheduling sequential while establishing gate and persistence semantics. Validate
and topologically sort YAML before execution. Entry failures block a run, exit failures
fail it, and no downstream stage runs after either. Publish artifacts and decisions only
after a passing exit gate. Require explicit approval for high-risk stages.

Persist the complete context as a JSON snapshot in a dedicated SQLite RunSnapshots table.
This preserves nested artifacts/lineage without prematurely normalizing an evolving
orchestration model. Use an optimistic revision check and a local per-run file lease.
Resume uses the saved scenario, skips completed work, and checks supplied implementation
metadata. Unfinished work is retried only on an explicit resume call.

Write audit events separately as append-only JSONL for human inspection and streaming.
Use ActivitySource with a minimal in-process listener so offline runs still have real
W3C correlation IDs. Resume links to the prior saved span. Artifact bodies/prompts are
not copied into audit lines; output hashes identify accepted results.

Consequences: checkpoints and audit appends are not one atomic transaction; power loss
can leave a missing event or a partial final line. External stage effects are not exactly
once. Stages must be idempotent or reconciled before recovery. Snapshot schema evolution,
transactional audit delivery, automatic re-planning, budget enforcement, policy execution,
parallelism and retry/rollback mechanisms are deferred to their respective phases.
