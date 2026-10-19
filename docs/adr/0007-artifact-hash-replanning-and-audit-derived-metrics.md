# ADR 0007: Hash-based re-planning and audit-derived metrics

Status: Accepted for Phase 7.

**Re-plan trigger.** The draft file each stage presents for review is the artifact a human edits.
The engine re-reads it between waves and on resume, and compares normalised content hashes with
the accepted artifact. Hashes make the trigger exact and cheap. Normalising first avoids
re-running a pipeline for whitespace. Checking between waves keeps re-planning away from
running stages.

**What re-runs.** The edited stage re-lands its edited draft rather than accepting the file
directly. A human edit therefore passes the same schema, policy, build/test gates and (when
high-risk) approval as agent output, and is committed like any other change. Downstream stages
are regenerated with the reason in the prompt. Upstream and unrelated stages are kept. Old
downstream decisions stay in the lineage, marked superseded, so the history of the first pass
is never lost.

**Metrics source.** Metrics are computed from audit.jsonl rather than kept in counters inside
the run state. The audit log is append-only, spans resumes and pauses, can be re-analysed with
new definitions, and lets the "across all runs" view use the same code. A Meter publishes the
same signals live for dashboards. Its tags avoid run IDs to keep cardinality low.
