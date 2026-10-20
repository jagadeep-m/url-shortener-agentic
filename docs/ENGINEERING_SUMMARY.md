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
| Requirement understanding | Requirements and Clarifier agents; `open-questions.md` with 2 to 3 interpretations per ambiguity, answered by a human |
| Task decomposition | Scenario DAGs; design artifacts list tasks with dependencies; `plan.json` per run |
| Codebase reasoning | ImpactAnalyst + Roslyn `CodebaseIndex`: affected files, endpoints, tables, tests; `CodeContext` gives agents a read-only outline and the current source |
| Workflow orchestration | `RunEngine` / `StageExecutor`: DAG, entry/exit gates, parallel waves with a join, approvals, retries, fallback, rollback, safe-stop, policies, audit, metrics, re-planning, binding review with bounded rework |
| Engineering output | Clean Architecture API with migrations and 250 tests; the brownfield change verified by the real gates |
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
11. Live mode with a real model: plain-English requirements (`--requirement`), read-only code
    context, and hardening from 18 live runs (below).

The shortener was built first so the orchestrator had a real codebase to reason about and real
tests to gate on.

Key choices (details in the ADRs):
- **A custom engine over a framework** (ADR 0009): every governance mechanism is explicit,
  testable C#, which is what the brief evaluates.
- **The host verifies; the agent proposes.** Gates run real builds and tests; impact analysis is
  computed from syntax trees; policies judge the real diff; metrics come from the audit log. Model
  output is never taken on trust.
- **One write path and one control point.** Agents have no tools. Every change goes through
  `FileChangeService` (boundary check, then policies, then approval, then an atomic write) and
  lands through the workspace lock (the gate, then a commit or a rollback).
- **Replay first** (ADR 0004): demos and tests are deterministic, free and offline. Scripted
  failures show retry and rollback on demand, and prompt-matched variants follow human answers.
- **SQLite only** (ADR 0010): nothing to install; real SQL in tests.

## Artifacts

- Source: `src/Orchestrator.*` (engine, agents, policies, CLI) and `src/UrlShortener.*` (product).
- Tests: 250 across three projects; 91.1% line and 83.6% branch coverage of the solution.
- Scenarios: `scenarios/greenfield.yaml`, `brownfield.yaml` and `ambiguous.yaml`, with replay
  recordings.
- Per run: `input.md`, `plan.json`, drafts, `audit.jsonl`, per-attempt build/test logs,
  `report.md` (metrics and lineage), `diff.patch`, and a branch `agent/run-<id>` with one commit
  per stage.
- Committed sample runs: [SAMPLE_RUNS.md](SAMPLE_RUNS.md).

| Run | Highlights |
| --- | --- |
| Greenfield | parallel implement/tests/docs; the Tester fails the real test gate once, is rolled back and retried; retry rate 12.5%, MTTR 8.1 s |
| Brownfield | impact report (12 files, 3 endpoints, `ShortLinks`, tests); migration approval; a NOT NULL migration fails 15 tests, 8 files are rolled back, and the nullable retry lands |
| Ambiguous | 3 open questions; answers recorded as decisions; changing Q1 at the design prompt re-plans requirements and design only |
| Ambiguous, **live** (`gpt-6-luna`) | The model wrote 6 questions of its own; a changed answer re-planned only the dependent stages; two real cut-off answers recovered by retry; a time-budget pause resumed cleanly; build and 84 tests green; 56,754 tokens (a few cents) |
| Brownfield, **live** (`gpt-6-luna`, 2 runs) | The click limit built by the model on the existing code: impact analysis (17 files, 2 endpoints, the `ShortLinks` table), a real EF Core migration approved at the policy prompt, 14 files changed, 106 tests green, completed in 9 min 22 s with 269,124 tokens. The first attempt paused at the old 400,000-token budget and completed after `resume --max-tokens 800000` |
| New feature, **live** (`gpt-6-luna`, 15 runs) | A URL normalizer (and a QR-code endpoint) built from one plain-English sentence. The final run on the committed code: the review found a real bug, one rework fixed it, 106 tests green, review PASS, released in 6 min 40 s with 203,131 tokens. Every earlier failure is kept with the fix it led to |

## Live-run hardening

Replays prove the mechanisms; a real model proves whether they hold. After the replay scenarios
were finished, the orchestrator built the same feature live ("normalize URLs before saving") until
it completed reliably. Every failure stopped safely (a gate, a policy or the review caught it, and
nothing broken landed), and each one exposed a specific gap. Each gap was fixed with tests, and
each run is kept in [SAMPLE_RUNS.md](SAMPLE_RUNS.md) with the fix it led to.

| What a live run exposed | What changed | Kind |
| --- | --- | --- |
| The design named types but no paths, so the Coder saw no code and answered with no files, which passed | Files found by type name; a Roslyn outline for planners and coders; code stages must return files | Feature + fix |
| The Tester guessed the request field name | The Tester always sees the API contract and the test fixture | Fix |
| Correct code that nothing called passed, although the Reviewer said so | Binding review: `Verdict: PASS/FAIL`; a FAIL reworks implement, tests and docs with the findings; release states READY / NOT READY | Feature |
| A design that named no files left the Coder unable to wire its code in | Roles that write or review code see the whole product source (within a 120,000-character budget) | Enhancement |
| Correct new tests were blamed for code that was not finished, and wrong new tests were blamed on the Coder | Tests this run created may be "red" at the tests and implement gates if every existing test passes; the strict full run at validation decides and reworks with the failure details | Enhancement |
| The second review called a test missing that the first round had written, and could not read `docs/` | The Reviewer sees every file the run changed, tests and docs included | Fix |
| A review relied on a stale "tests are red" note | The full build and test run happens before the review and is given to the Reviewer as current; a failing run reworks without a model call | Enhancement |
| The design gave test files to the Coder; its boundary stopped the run | Folders are assigned to roles in the prompts; a write in another role's folder gets one corrected retry; secrets, packages and forbidden paths still stop at once (ADR 0011) | Enhancement |
| Docs, reworked beside the Coder, described the code before the fix and added a second document | Reworked stages run one at a time in order; Docs updates its own document and documents the feature, never the run | Enhancement + fix |
| Reviews reported gaps a few at a time, so one rework cycle was not enough | The Reviewer lists every gap at once; the re-review checks its earlier findings; rework is bounded at two cycles | Enhancement |
| Retries spent on vague feedback (missing verdict line, generic schema error, missing `files` key, cut-off answers) | A structured `verdict` field; schema errors name the missing field; no `files` key means no changes; failure details name the line that threw; output limits raised (Architect 12,288, Reviewer 8,192 tokens) | Fix |
| The time budget counted time spent waiting for a human | Human waiting time is excluded (`HumanWaitClock`) | Fix |
| A live brownfield run with two real rework cycles paused at the 400,000-token budget, and a plain resume could not continue it | The default budget is 600,000 tokens; `resume <runId> --max-tokens <n>` raises it, audited as `token_budget_changed` with the person's name | Feature + fix |

Engineering judgment behind these changes:
- **The host still verifies; agents still only propose.** Each fix gave agents better information
  (code, contracts, current test results, precise errors), not more authority. The one loosened
  guardrail (a corrected retry for a misfiled write) is bounded, audited and recorded in ADR 0011.
- **Decide with evidence, not with a single gate's view.** A gate can't tell whether a failing new
  test or the code is wrong, so red tests are tolerated until the strict full run, and the review
  reads the result of that run.
- **Bounded everywhere.** Retries (3 attempts), fallback, rework (2 cycles), a boundary correction
  (1), the token budget (600,000, raisable on resume) and the time budget (20 minutes of machine time) all have limits,
  so a run ends as Completed, Failed or Paused, never in a loop.

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

- Every phase ended green. The final state is 250 tests passing, repeated at least three times
  with no flakes.
- All three scenarios ran end to end through the CLI with real git and real gates; results are
  committed.
- The brownfield product change was verified by hand before recording: the good version passed
  all product tests three times, and the deliberately broken migration failed 15 integration
  tests.
- A fresh-clone check followed the README exactly (see TESTING.md and the Phase 9 notes in
  assumptions.md).
- Live mode: 18 live runs with `gpt-6-luna` (one ambiguous, fifteen new-feature runs, two brownfield runs). The last
  three new-feature runs completed, and the final one ran on the committed code from a fresh clone.
  Every failure along the way stopped safely and is kept as evidence.
- A second fresh-clone check (2026-10-20) followed the README step by step: prerequisites, build,
  all tests, the API with its curl examples and Swagger, the three replays (the ambiguous run
  blocked on unanswered questions, then resumed to Completed after answering), `report --lineage`,
  and the documented error for `--requirement` with `--replay`.

## Assumptions

All interpretations are in [assumptions.md](assumptions.md). The most important:
- SDK 9.0.305 builds `net8.0`.
- `maxAttempts` counts total attempts.
- The time budget limits machine time per session (approval waits are excluded); the token budget is per run.
- Rollback is scoped to agent-writable paths.
- A policy Deny is not retried, except one correction for a file in another role's folders (ADR 0011).
- Exit gates run the product's tests.
- Brownfield adds a click limit, because both suggested changes already existed.
- The ambiguous scenario ends at an approved scope rather than code.

## Limitations

- Replay recordings demonstrate the mechanisms but are hand-authored. Live mode is proven with a
  real model (`gpt-6-luna`) on the ambiguous scenario and on new features, but model output varies:
  a live run can still fail when the review keeps finding real problems after two rework cycles.
  That is the intended safe outcome, not a crash, and the run can be resumed after a human change.
- A live feature run uses roughly 150,000 to 470,000 tokens of its 600,000-token budget, mainly
  because code roles see the whole product source. A much larger codebase would need retrieval
  instead of whole-source context.
- Live runs 9 to 15 were approved by a script that typed `approve` at every prompt, so their
  human-wait metrics are near zero; the gates, policies and reviews were unchanged.
- Single process: SQLite checkpoints, an in-memory click queue and cache, and a local file lease.
  There is no distributed execution.
- Console approvals only. There is no web UI, no approver identity beyond the OS user, and no
  authentication on the shortener.
- Impact analysis uses syntax trees (names, not resolved symbols) and does not follow transitive
  call chains.
- Test quality is reviewed by an agent and a human, not measured (no mutation testing).
- Scheduling is wave-based. A stage-level `budget` is parsed but only the run budget is enforced.
- The orchestrator CLI's `Main` is covered by end-to-end runs, not unit tests (50% line coverage).

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
