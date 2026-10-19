# ADR 0001: UTC SQLite persistence and atomic click recording

Status: Accepted for Phase 1.

SQLite cannot reliably order EF Core DateTimeOffset values. Store UTC DateTime values
as SQLite TEXT and restore UTC kind on materialization. Domain constructors reject
non-UTC values, keeping time-zone ambiguity out of normal writes.

Use a unique Code index as the collision arbiter. Repository collision results allow
the application service to retry generated codes without treating every database error
as a collision. Preserve analytics on soft deletion and restrict physical deletion.

Increment ClickCount with ExecuteUpdateAsync and insert the click event in one
transaction. An insertion failure must not leave an inflated total. This avoids lost
updates and avoids depending on tracked entity counters. Callers re-query to observe
fresh values and use one scoped context per unit of work.

Consequences: SQLite serializes writes; this is appropriate for the prototype. Repositories
must not share contexts across concurrent workers. Click-history reads are currently
unpaged; later analytics services should aggregate in SQL for larger datasets. Persistence
expects valid domain objects; direct SQL writes can bypass constructor validation.
