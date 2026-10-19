# ADR 0009: A custom DAG engine rather than a workflow framework

Status: Accepted.

**Context.** The orchestrator needs a dependency graph with entry and exit gates, parallel
branches with a join, human approvals in the middle of a stage, bounded retries with feedback,
fallback, git rollback, budget-driven safe-stop, resumable state, an audit trail and hash-based
re-planning. Candidates were Microsoft Semantic Kernel's Process Framework, Durable Functions /
Durable Task, a general workflow engine (Elsa, Temporal), or a small custom engine.

**Decision.** Write a small engine in C#: `RunEngine` for waves, budget and re-planning,
`StageExecutor` for one stage's lifecycle, and `RunSession` for serialized state. Polly provides
retries and LibGit2Sharp provides versioning.

**Why.**
- Every governance mechanism is explicit code that can be read, tested and explained. The
  interview brief grades exactly these mechanisms.
- Approvals that pause inside a stage, a landing queue for shared-workspace gates, rollback
  scoped to agent paths, and re-planning on artifact hashes are awkward to express in frameworks
  built for durable function replay or agent chat loops.
- It fits the stack constraints: no extra infrastructure (Temporal needs a server; Durable
  Functions needs Azure Storage or an emulator) and no extra packages.
- Tests run in-process in milliseconds with doubles for agents, approvals and gates.

**Costs.** More code to own. Durability is a single-process SQLite checkpoint rather than an
event-sourced history, and there is no distributed worker pool. The engine is wave-based rather
than fully dynamic.

**Scale-up path.** Keep the contracts (`IStage`, `IAgent`, `IApprovalService`, `IPolicy`) and move
execution onto Durable Task or Temporal for distributed, durable workers. Keep the audit log as
the metrics source.
