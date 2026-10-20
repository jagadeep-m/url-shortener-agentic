# Testing

```bash
dotnet build
dotnet test
dotnet format UrlShortenerAgentic.sln --verify-no-changes
```

251 tests, about 6 seconds, no network, no external database. Each test uses its own temporary
SQLite file, runs directory and (where needed) git repository, and deletes them afterwards.
Requires the .NET 8 runtimes (see the README).

## Test pyramid

| Level | Where | Count | What it proves |
| --- | --- | --- | --- |
| Unit | UrlShortener.UnitTests; most of Orchestrator.Tests | ~150 | Domain rules, validators, services with fakes; policies, diffing, parsing, metrics maths, DAG validation, engine mechanisms with stage doubles |
| Integration | UrlShortener.IntegrationTests; engine tests on real SQLite and git | ~55 | Real EF Core migrations, SQL behaviour, HTTP pipeline via `WebApplicationFactory`; orchestrator state, audit and rollback on real files and repositories |
| Scenario (replay) | WorkflowTests, ScenarioReplayTests, GuardrailTests | ~20 | Whole runs through the real engine, agents, policies and stages, using recorded model output |
| End-to-end (manual / CI) | `dotnet run --project src/Orchestrator.Cli -- run scenarios/<name>.yaml --replay` | 3 scenarios | The CLI with real git branches and the real `dotnet build` / `dotnet test` gates. Committed results are in `runs/` |
| Live (manual) | `run scenarios/greenfield.yaml` or `brownfield.yaml` with `--requirement "..."` and a real model | 18 runs | The whole system against a real model's unpredictable output; each failure became a fix with tests. Results and the fix each led to are in [SAMPLE_RUNS.md](SAMPLE_RUNS.md) |

## What each project covers

**UrlShortener.UnitTests (43)**
- DomainTests: generator length, Base62 alphabet and a 10,000-code uniqueness sample; code and
  URL rules; UTC enforcement; expiry boundary; idempotent disable; IP-hash-only clicks
- LinkServiceTests: collision retry bound (one try plus three retries), custom alias conflicts,
  reserved generated codes, own-host rejection, idempotency key rules, expiry validation

**UrlShortener.IntegrationTests (41)**
- ApiTests (`WebApplicationFactory`, a temporary SQLite file per class):
  - create and details; a duplicate alias returns 409
  - invalid URLs return 400 (`javascript:`, `data:`, own host, over 2,048 characters, malformed JSON)
  - a redirect returns 302, an unknown code 404, an expired link 410, and a deleted link 410
  - stats after 3 clicks (per day, top referrers, hashed IP)
  - idempotent POST, including across concurrency and restarts
  - Swagger, health checks, and no automatic migration in Production
- PersistenceTests:
  - migrations applied via `MigrateAsync`, matching the model, reversible
  - unique and composite indexes; foreign keys; UTC round-trips
  - 20 concurrent writers producing atomic click totals; transaction rollback when the event
    insert fails
- RateLimitTests: per-IP 429 with `Retry-After`
- ClickRecorderTests: queue capacity, bounded storage retries, graceful drain

**Orchestrator.Tests (167)**

| Class | Covers |
| --- | --- |
| ScenarioTests | strict YAML, topological order, cycle and unknown-dependency errors, budget/retry validation, artifact hashing |
| RunEngineTests | a token-budget pause continues only after an audited `--max-tokens` raise above the tokens used; entry gate blocks, exit failure publishes nothing, approvals, checkpoints and resume across new instances, cancellation, lineage validation, run leases, stale-write protection, one audit line per event |
| ResilienceTests | barrier-proven parallel waves with overlapping timestamps and a join; bounded retries carrying the failure reason; a write in another role's folders retried once with the denial as feedback, while a second one or any other denial stops the stage; fallback order; token- and time-budget safe-stop; rollback on a real git repository |
| GitWorkspaceTests | branch per run, commits scoped to agent paths, revert of modified and new files, refusal of dirty, staged or empty repositories |
| AgentTests | every role's structured output and parent decisions; replay selection (variant, attempt, base); retry feedback in prompts; templates; boundary and symlink escapes; build/test output parsing |
| PolicyTests | Allow, RequireApproval and Deny for each of the six policies; unified diff format, hunks and line numbers |
| GuardrailTests | an agent cannot write `.github/` (not correctable) or commit a fake API key; a file in another role's folders is a correctable denial naming the allowed folders and nothing is written; design and Coder prompts assign each folder to one role (and the key never reaches the audit log); approval shows reasons and the diff; new package detection |
| WorkflowTests | `--auto-approve` refused outside CI and recorded as `ci-auto-approve` inside it; greenfield replay end to end with real policies; Tester retry; editing design re-runs only downstream stages; resume after an edit; console approval paths |
| MetricsTests | success rate, retry rate, MTTR, human wait, machine time and per-stage latency from a hand-written audit log; aggregates; live Meter instruments; report and lineage rendering |
| CodebaseIndexTests | Roslyn endpoints with group prefixes, table mappings, affected files/endpoints/tables/tests, unknown seeds; indexes the real solution |
| OpenQuestionsTests | answer normalisation, canonical round-trip, the 2 to 3 interpretation rule |
| ReviewTests | verdict and recommendation parsing (including markdown bold); missing lines rejected; the `verdict` field completes a review without the line (the line wins when both exist); a schema error names the missing fields; a re-review receives its earlier findings; `[NOT READY]` leads the release summary; a FAIL review reworks the implementation with the findings and reviews again; rework is bounded and a review that keeps failing fails the run; reworked stages run one at a time in scenario order while the first pass stays parallel |
| HumanWaitTests | overlapping approval waits counted once; a long approval no longer exhausts the time budget, while the same wait without the clock still does |
| LiveModeTests | files found by type name when no path is given; a code stage returning no files fails with the model's explanation; the Tester sees the API contract; `--requirement` parsing and its refusal with `--replay`; `feature.yaml` graph; code context (only named, existing product files; no traversal or build output; size and budget caps; absolute paths redacted; outline content); what each role is shown; the provider client against a fake server (reply, usage, served model, request shape, 401/404/429 explanations with the provider's message, cut-off answers); fenced JSON; the model named in the report |
| RedTestsTests | code roles see the whole product source after the named files, without tests or migrations; failure details carry test names, arguments and assertion messages; only failures confined to new test classes may land red; the Tester gate tolerates only classes in new files; a failing full run at validation requests rework with the details; other code stages are still retried; the Coder and a reworked Tester tolerate only test classes this run created; failure details name the line that threw; files a stage lands are recorded for the run (not failed attempts); the Reviewer sees every file the run changed first, including earlier-round tests and docs, while writing roles do not; Docs sees the run's changes and is told to update its own document; a failing run before the review reworks without a model call; the review prompt carries the current run over older gate notes |
| ScenarioReplayTests | brownfield on real git (impact report, migration approval twice, rollback, `diff.patch`); ambiguous (answers as decisions, mid-run answer change re-plans, unanswered questions block) |

Every orchestrator mechanism (gates, approvals, retries, fallback, rollback, safe-stop,
policies, audit, lineage, metrics, re-planning) has at least one test that fails if the mechanism
is removed.

## How the gates use the tests

The orchestrator does not trust an agent's claim that code works. Its exit gate for any stage that
changes `.cs` or `.csproj` files, and always for the Reviewer, runs:

1. `dotnet build` of the whole solution (warnings are errors in `src/`)
2. `dotnet test` of `tests/UrlShortener.UnitTests` and `tests/UrlShortener.IntegrationTests`

The orchestrator's own tests are excluded because agents cannot change the orchestrator, and its
timing-sensitive tests should not decide product gates. Pass/fail/skip counts, the first failing
test names and compiler errors become the gate reason, which is fed into the retry prompt. Logs are
kept as `runs/<runId>/<stage>.attempt<N>.build.log` and `.test.log`. A gate with zero test results
fails.

In the scenarios, the Tester's first greenfield attempt (a wrong assertion) and the Coder's first
brownfield attempt (a NOT NULL migration, which fails 15 integration tests) are caught this way,
rolled back and retried.

## Coverage

Collected with the coverage collector that ships with `Microsoft.NET.Test.Sdk` (no extra package):

```bash
dotnet test --collect "Code Coverage;Format=cobertura" --results-directory ./TestResults
```

Line and branch coverage of the solution's assemblies, excluding EF-generated migration code
(measured on 2026-10-20):

| Assembly | Line | Branch |
| --- | --- | --- |
| UrlShortener.Domain | 100.0% (75/75) | 100.0% |
| UrlShortener.Application | 100.0% (71/71) | 100.0% |
| UrlShortener.Infrastructure | 96.9% (154/159) | 85.3% |
| UrlShortener.Api | 97.4% (189/194) | 84.4% |
| Orchestrator.Core | 98.0% (1,041/1,062) | 89.9% |
| Orchestrator.Agents | 93.4% (841/900) | 84.2% |
| Orchestrator.Policies | 75.5% (111/147) | 81.9% |
| Orchestrator.Cli | 50.4% (133/264) | 48.5% |
| **Total** | **91.1% (2,615/2,872)** | **83.6%** |

Known gaps:
- `Orchestrator.Cli` `Main` (argument parsing, wiring, stop polling) is exercised by the
  end-to-end runs, not unit tests.
- The vulnerability scanner's process path (it restores a probe project and needs network) is
  covered only through its JSON parser and a fake.
- The live `OpenAiCompatibleChatClient` is tested against a fake HTTP server; the real provider is
  exercised only by the live runs in `runs/`, because that needs a key and costs money.

## Flakiness controls

- Retry delays in tests use a 1 ms base.
- Parallelism is proven with a barrier that only opens if three stages run at once, not with
  sleeps.
- The time-budget test uses a 1.2 s budget and raises it before resume.
- Click-ingestion tests poll for up to 10 s instead of sleeping.
- New tests were run at least three times in a row before being accepted.
- **Known intermittent failure:** `RateLimitTests.IdempotencySurvivesApplicationRestart` (a product
  integration test that restarts the API on the same SQLite file) failed once in about 30 full-suite
  runs on 2026-10-20, with no error captured. It was not reproduced in 20 further runs (8 full,
  12 isolated). It stays enabled; the suspected cause is file timing between the two app instances
  under parallel load, to be investigated.

## Migration tooling (developers)

```bash
dotnet tool install --global dotnet-ef --version 8.0.32
```

```bash
dotnet ef migrations add <Name> --project src/UrlShortener.Infrastructure --startup-project src/UrlShortener.Infrastructure --output-dir Migrations
```

The design-time factory accepts a connection string as its first argument. Development startup
applies migrations; other environments must apply them explicitly before readiness succeeds.
