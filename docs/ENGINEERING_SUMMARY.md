# Engineering summary

## What was built

A governed, agentic SDLC orchestrator in C#, and the URL shortener it builds and changes. One
command turns a requirement into a reviewed, tested change on its own git branch, with every
step audited. The human approves high-impact actions and owns final quality.

```bash
dotnet run --project src/Orchestrator.Cli -- run scenarios/greenfield.yaml --replay
```

The brief's core requirements and where each one lives:

| Requirement | Where |
| --- | --- |
| Requirement understanding | Requirements and Clarifier agents; `open-questions.md` with 2–3 interpretations per ambiguity, answered by a human |
| Task decomposition | Scenario DAGs; design artifacts list tasks with dependencies; `plan.json` per run |
| Codebase reasoning | ImpactAnalyst + Roslyn `CodebaseIndex`: affected files, endpoints, tables, tests |
| Workflow orchestration | `RunEngine` / `StageExecutor`: DAG, entry/exit gates, parallel waves with a join, approvals, retries, fallback, rollback, safe-stop, policies, audit, metrics, re-planning |
| Engineering output | Clean Architecture API with migrations and 238 tests; the brownfield change verified by the real gates |
| Validation and risk control | Real `dotnet build` / `dotnet test` gates, six policies, budgets, human checkpoints |
| Controlled autonomy | Role autonomy boundaries, a single write path, approvals on high-risk actions, `--auto-approve` only in CI |
| Final summary | This document, [ARCHITECTURE.md](ARCHITECTURE.md), [TESTING.md](TESTING.md), [assumptions.md](assumptions.md), [adr/](adr/) |

## Plan and rationale

Built in small, verified phases, each ending with `dotnet build` and `dotnet test` green:

1. Scaffold.
2. Shortener domain and persistence.
3. Shortener API.
4. Sequential orchestrator core with checkpoints and audit.
5. Agents, replay mode and the CLI.
6. Parallel waves, retries, fallback, git rollback and safe-stop.
7. Policies.
8. Re-planning, lineage and metrics.
9. Brownfield and ambiguous scenarios.
10. Documentation and a fresh-clone check.

The shortener was built first so the orchestrator had a real codebase to reason about and real
tests to gate on.

Key choices (details in the ADRs):
- **A custom engine over a framework** (ADR 0009): every governance mechanism is explicit,
  testable C#, which is what the brief evaluates.
- **The host verifies; the agent proposes.** Gates run real builds and tests; impact analysis is
  computed from syntax trees; policies judge the real diff; metrics come from the audit log. Model
  output is never taken on trust.
- **One write path and one control point.** Agents have no tools. Every change goes through
  `FileChangeService` (boundary → policies → approval → atomic write) and lands through the
  workspace lock (gate → commit, or rollback).
- **Replay first** (ADR 0004): demos and tests are deterministic, free and offline. Scripted
  failures show retry and rollback on demand, and prompt-matched variants follow human answers.
- **SQLite only** (ADR 0010): nothing to install; real SQL in tests.

## Artifacts

- Source: `src/Orchestrator.*` (engine, agents, policies, CLI) and `src/UrlShortener.*` (product).
- Tests: 238 across three projects; 90.4% line and 82.7% branch coverage of the solution.
- Scenarios: `scenarios/greenfield.yaml`, `brownfield.yaml` and `ambiguous.yaml`, with replay
  recordings.
- Per run: `input.md`, `plan.json`, drafts, `audit.jsonl`, per-attempt build/test logs,
  `report.md` (metrics and lineage), `diff.patch`, and a branch `agent/run-<id>` with one commit
  per stage.
- Committed sample runs: [runs/](../runs/README.md).

| Run | Highlights |
| --- | --- |
| Greenfield | parallel implement/tests/docs; the Tester fails the real test gate once → rollback → retry; retry rate 12.5%, MTTR 8.1 s |
| Brownfield | impact report (12 files, 3 endpoints, `ShortLinks`, tests); migration approval; a NOT NULL migration fails 15 tests → 8 files rolled back → nullable retry lands |
| Ambiguous | 3 open questions; answers recorded as decisions; changing Q1 at the design prompt re-plans requirements and design only |
| Ambiguous, **live** (`gpt-6-luna`) | The model wrote 6 questions of its own; a changed answer re-planned only the dependent stages; two real cut-off answers recovered by retry; a time-budget pause resumed cleanly; build and 84 tests green; 56,754 tokens (a few cents) |

## Risks, trade-offs and how they are handled

| Risk | Handling |
| --- | --- |
| LLM output is unpredictable or wrong | Strict JSON schemas; real build/test gates; bounded retries with failure feedback; fallback to a simplified prompt, then a template; human review of high-risk artifacts |
| An agent writes a test that always passes | The Reviewer maps requirements to tests; humans approve the design and release; coverage is measured. Not fully solved; see limitations |
| An agent escapes its role or leaks secrets | Role boundaries plus a global path allowlist; secret scan on added lines (values never logged); a Deny is final for the attempt |
| Risky changes slip through | Approvals for migrations, new packages, diffs over 400 lines and build/config files; release is High risk; merging stays manual |
| Partial or broken changes in the workspace | Changes land one at a time under a lock; a failed gate restores the agent paths to the last good commit; a run refuses to start on dirty agent paths |
| Runaway cost or time | Token and time budgets cancel the run cleanly with a resumable checkpoint; stop command and Ctrl+C |
| Upstream decisions change mid-run | Hash-based re-planning re-runs only downstream stages; lineage keeps superseded decisions |
| Wave scheduling waits for the slowest sibling | Accepted for readability of failure semantics (ADR 0005) |
| Soft click limit (brownfield) | Documented; a hard limit would need a synchronous conditional update per capped redirect |

## Validation

- Every phase ended green. The final state is 238 tests passing, repeated at least three times
  with no flakes.
- All three scenarios ran end to end through the CLI with real git and real gates; results are
  committed.
- The brownfield product change was verified by hand before recording: the good version passed
  all product tests three times, and the deliberately broken migration failed 15 integration
  tests.
- A fresh-clone check followed the README exactly (see TESTING.md and the Phase 9 notes in
  assumptions.md).

## Assumptions

All interpretations are in [assumptions.md](assumptions.md). The most important:
- SDK 9.0.305 builds `net8.0`.
- `maxAttempts` counts total attempts.
- The time budget limits machine time per session (approval waits are excluded); the token budget is per run.
- Rollback is scoped to agent-writable paths.
- A policy Deny is not retried.
- Exit gates run the product's tests.
- Brownfield adds a click limit, because both suggested changes already existed.
- The ambiguous scenario ends at an approved scope rather than code.

## Limitations

- Replay recordings demonstrate the mechanisms but are hand-authored. Live mode is proven on the
  ambiguous scenario with a real model (`gpt-6-luna`); a live code-writing run is the next step.
- Single process: SQLite checkpoints, an in-memory click queue and cache, and a local file lease.
  There is no distributed execution.
- Console approvals only. There is no web UI, no approver identity beyond the OS user, and no
  authentication on the shortener.
- Impact analysis uses syntax trees (names, not resolved symbols) and does not follow transitive
  call chains.
- Test quality is reviewed by an agent and a human, not measured (no mutation testing).
- Scheduling is wave-based. A stage-level `budget` is parsed but only the run budget is enforced.
- The orchestrator CLI's `Main` is covered by end-to-end runs, not unit tests (46% line coverage).

## What I would do next for production

1. **Durable, distributed execution:** move the engine onto Durable Task or Temporal behind the
   same contracts; PostgreSQL for orchestrator state; a queue-backed worker pool.
2. **Product scale-out:** PostgreSQL, Redis with explicit invalidation, a durable click queue
   (for example Azure Service Bus or Kafka), API authentication, trusted-proxy configuration.
3. **Approvals and identity:** a web approval inbox with SSO identities, four-eyes rules for
   release, and approval SLAs feeding the human-wait metric.
4. **Stronger verification:** mutation testing on agent-written tests; a Roslyn semantic model for
   impact analysis; a secret scanner with entropy checks; `dotnet list package --vulnerable` as a
   scheduled check on the whole solution.
5. **Delivery integration:** open a pull request from `agent/run-<id>` with the report and
   lineage attached; let CI run the same gates; merge only on approval.
6. **Observability:** export the Meter and traces through OpenTelemetry to a dashboard; alert on
   retry-rate and MTTR regressions.
7. **Model operations:** record live runs as new replay fixtures, keep evaluation suites per
   agent role, and track prompt versions in the audit log.
