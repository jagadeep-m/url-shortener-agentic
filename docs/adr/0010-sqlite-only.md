# ADR 0010: SQLite only, for the product and the orchestrator

Status: Accepted.

**Decision.** The URL shortener stores links, click events and idempotency records in one SQLite
file through EF Core 8. The orchestrator checkpoints run state in a separate SQLite file
(`runs/orchestrator.db`, Microsoft.Data.Sqlite). There is no PostgreSQL, MongoDB, Redis or Docker.

**Why.**
- Reviewers can clone, build and run with nothing to install. Tests use real SQLite files,
  including real migrations, foreign keys, indexes and transactions, instead of an in-memory
  provider that hides SQL behaviour.
- EF Core migrations give an approvable schema change (a gated stage in brownfield).
- One process with an in-memory cache matches the scale of the prototype.

**Consequences and mitigations.**
- SQLite cannot order or compare `DateTimeOffset` in EF Core, so timestamps are UTC `DateTime`
  with converters that restore `DateTimeKind.Utc` (ADR 0001).
- One writer at a time: click ingestion is a single background consumer, and counters use
  atomic `ExecuteUpdateAsync`.
- Orchestrator state is one JSON snapshot per run with an optimistic revision and a file lease.
  It is simple and resumable, but not shared between machines.

**Production path.** PostgreSQL (or Azure SQL) for the product, a distributed cache (Redis) for
redirects with explicit invalidation, a durable queue for clicks, and a server database or
workflow service for orchestrator state.
