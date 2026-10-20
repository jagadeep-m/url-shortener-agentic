# Agentic SDLC Orchestrator + URL Shortener (.NET 8)

A C# workflow engine that runs AI agents through the software lifecycle (requirements → design →
implement / tests / docs in parallel → validate → release readiness) under explicit governance,
and the URL shortener it builds and changes.

**Principle:** agents execute under defined autonomy boundaries; the human approves high-impact
actions and owns final quality.

| Part | What it is |
| --- | --- |
| Orchestrator (`src/Orchestrator.*`) | Scenario YAML → dependency graph with entry/exit gates, parallel waves, human approvals, bounded retries, fallback, git rollback, safe-stop, policy guardrails, audit log, decision lineage, metrics and re-planning. |
| URL Shortener (`src/UrlShortener.*`) | ASP.NET Core Minimal API + EF Core/SQLite: short links, redirects, click analytics, idempotent creation, rate limiting, health checks. |

Start with [docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md), then
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Sample runs of all three scenarios, plus a live run with a
real model, are in [runs/](runs/README.md).

## Prerequisites

- **.NET SDK 9.0.305**, pinned by `global.json`. It builds the `net8.0` projects.
- **.NET 8 runtimes** (Microsoft.NETCore.App 8.x and Microsoft.AspNetCore.App 8.x) to run the API and tests.
  Check with `dotnet --list-sdks` and `dotnet --list-runtimes`.
- **git**. The orchestrator works on a git branch per run.
- Nothing else: no database server, Docker, or API key (replay mode is offline).

## Setup

```bash
git clone <repository-url> url-shortener-agentic
cd url-shortener-agentic
dotnet build
dotnet test
```

`dotnet build` restores the NuGet packages, so the first build needs network access; after that,
everything including replay mode runs offline.

## Run the URL shortener API

```bash
dotnet run --project src/UrlShortener.Api
```

The Development profile listens on <http://localhost:5080> and creates or migrates `urlshortener.db`
in `src/UrlShortener.Api`. Open <http://localhost:5080/swagger>, or:

```bash
curl -i -X POST http://localhost:5080/api/v1/links -H "Content-Type: application/json" -d '{"longUrl":"https://example.com/docs","customAlias":"demo-link"}'
curl -i http://localhost:5080/demo-link
curl -s http://localhost:5080/api/v1/links/demo-link/stats
```

| Method | Route | Result |
| --- | --- | --- |
| POST | `/api/v1/links` (optional `customAlias`, `expiresAt`, header `Idempotency-Key`) | 201; 400 invalid, 409 alias taken, 429 rate limited |
| GET | `/{code}` | 302 redirect (click recorded in the background); 404, 410 expired/disabled |
| GET | `/api/v1/links/{code}` | 200 details; 404 |
| GET | `/api/v1/links/{code}/stats` | 200 total clicks, clicks per day, top referrers; 404 |
| DELETE | `/api/v1/links/{code}` | 204 soft delete; 404 |
| GET | `/health/live`, `/health/ready` | 200; 503 when the database is unreachable |

Errors are RFC 7807 ProblemDetails. Settings are in `src/UrlShortener.Api/appsettings.json`
(`ConnectionStrings:Shortener`, `PublicBaseUrl`, `RateLimiting:*`).

## Run the orchestrator (replay mode, no API key)

Run every command from the repository root. The orchestrator needs a git repository with at least
one commit (a clone has one) and no uncommitted changes under `src/UrlShortener.*`,
`tests/UrlShortener.*` or `docs/`. Each run creates and checks out a branch `agent/run-<runId>`,
commits every stage there, and leaves you on that branch. Switch back before the next scenario,
for example `git switch main` (or whatever your default branch is called).

### Greenfield

```bash
dotnet run --project src/Orchestrator.Cli -- run scenarios/greenfield.yaml --replay
```

At each prompt, type `approve` (or `reject`, or `edit` to open the artifact in `$EDITOR`).
Requirements, Design and Release ask for approval. Implement, tests and docs then run in
parallel. The Tester's first recorded attempt fails the real `dotnet test` gate; it is rolled back
and retried with the failure reason. This takes about 30 seconds, mostly real builds and tests.

### Brownfield (change existing code)

```bash
git switch main
dotnet run --project src/Orchestrator.Cli -- run scenarios/brownfield.yaml --replay
```

Adds a per-link click limit. Approve requirements, then the impact analysis (affected files,
endpoints, tables and tests, computed from Roslyn syntax trees), then design. The EF Core
migration asks for approval. The first attempt's migration fails 15 integration tests, is rolled
back to the last good commit and retried, and asks for approval again. Finish with release.
`runs/<runId>/diff.patch` holds the complete change.

### Ambiguous requirement

```bash
git switch main
dotnet run --project src/Orchestrator.Cli -- run scenarios/ambiguous.yaml --replay
```

The first prompt shows `runs/<runId>/open-questions.md`. Type `edit`, fill in each `Answer:`
line (for example `A`, or `A, C`, or `Other: ...`), save, then type `approve`. Approving with
questions unanswered blocks the run; answer them and `resume <runId>`. To see re-planning, change
an answer in that file at the design prompt (for example Q1 from `A` to `C`) before approving:
the answer sheet is re-reviewed, and only requirements and design re-run, now following the new
answer.

### Other commands

```bash
dotnet run --project src/Orchestrator.Cli -- report <runId> --lineage
```

```bash
dotnet run --project src/Orchestrator.Cli -- stop <runId>
```

```bash
dotnet run --project src/Orchestrator.Cli -- resume <runId>
```

- `report` rewrites `runs/<runId>/report.md` (stage table, reliability metrics, lineage);
  `--lineage` also prints the decision tree down to commits.
- `stop`, from a second terminal, pauses a running run at a safe point; Ctrl+C does the same.
- `resume` continues a paused, blocked or failed run. It also re-plans a completed run whose
  artifact files you edited.

Each run writes to `runs/<runId>/`: `input.md`, `plan.json`, per-stage drafts, `audit.jsonl`,
per-attempt build/test logs, `report.md` and `diff.patch`. Checkpoints are in `runs/orchestrator.db`.

`--auto-approve` exists only for CI. It is refused unless the environment variable `CI=true` is set.

## Run with a real model (live mode)

Live mode calls an OpenAI-compatible chat-completions endpoint through `IChatClient`. The key never
goes in code or config files. Set it with user-secrets:

```bash
dotnet user-secrets set "LLM:ApiKey" "<your key>" --project src/Orchestrator.Cli
```

```bash
dotnet user-secrets set "LLM:Model" "<model name>" --project src/Orchestrator.Cli
```

You can use environment variables instead: `LLM__APIKEY`, `LLM__MODEL` and optionally
`LLM__ENDPOINT` (default `https://api.openai.com/v1/chat/completions`). Then run a scenario
without `--replay`:

```bash
dotnet run --project src/Orchestrator.Cli -- run scenarios/greenfield.yaml
```

The same gates, policies, approvals and budgets (20 minutes of machine time, so time spent reading
and approving doesn't count, and 400,000 tokens per run, by default) apply. Model output varies, so live runs may need more retries than the replays. The run header and
`report.md` name the model that was used.

### Request a new feature in plain English (live mode)

`scenarios/feature.yaml` runs any requirement you type through the full lifecycle: requirements →
impact analysis → design → implement ‖ docs → tests → validate → release.

```bash
dotnet run --project src/Orchestrator.Cli -- run scenarios/feature.yaml --requirement "Add an endpoint that returns a QR code for a short link"
```

- **Needs a real model.** Replay only knows the three recorded demo scenarios, so `--requirement`
  with `--replay` is refused.
- **Agents see the code they change.** Planning agents get an outline of the codebase. The
  Architect, Coder, Tester, Docs and Reviewer agents get the current contents of the files the run
  names (for example in the impact report), read-only and size-capped. Your machine's absolute path
  is replaced with `<workspace>` before anything is sent.
- **Every safeguard still applies.** You approve requirements, impact, design, any migration and
  release. Real `dotnet build` / `dotnet test` gates judge the code, failures are rolled back and
  retried, and everything is audited.
- **The result is on a branch.** The feature lands on `agent/run-<runId>`. Review
  `runs/<runId>/report.md` and `diff.patch`, then merge it into `main` yourself if you want it:
  `git merge agent/run-<runId>`.
- **Cost:** with a small model, a full run is typically a few cents. The budget (`maxTokens` in
  `feature.yaml`) caps it.

## Tests

```bash
dotnet test
```

244 tests across three projects (Orchestrator.Tests, UrlShortener.UnitTests,
UrlShortener.IntegrationTests). They use temporary SQLite files and git repositories and need no
network. For coverage (no extra tools; the collector ships with the test SDK):

```bash
dotnet test --collect "Code Coverage;Format=cobertura" --results-directory ./TestResults
```

See [docs/TESTING.md](docs/TESTING.md) for the test pyramid, what each project covers, how the
exit gates use the tests, and coverage numbers.

## Repository layout

```
src/Orchestrator.Core          DAG engine, stage executor, gates, retries, rollback, state, audit, metrics, re-planning
src/Orchestrator.Agents        Agents, AgentStage/ClarificationStage, FileChangeService, replay/live chat clients, Roslyn index
src/Orchestrator.Policies      Path allowlist, secret scan, NuGet allowlist, diff size, migration approval, vulnerable packages
src/Orchestrator.Cli           run / resume / report / stop, console approvals
src/UrlShortener.*             Domain ← Application ← Infrastructure, Api (Clean Architecture)
tests/                         Orchestrator.Tests, UrlShortener.UnitTests, UrlShortener.IntegrationTests
scenarios/                     greenfield.yaml, brownfield.yaml, ambiguous.yaml, recordings/ (replay fixtures)
docs/                          ARCHITECTURE.md, TESTING.md, ENGINEERING_SUMMARY.md, assumptions.md, adr/
runs/                          committed sample runs (README.md indexes them)
```
