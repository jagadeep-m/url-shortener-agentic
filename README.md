# Agentic SDLC Orchestrator and URL Shortener (.NET 8)

This repository contains two things:

1. **The orchestrator**, a C# workflow engine that takes a requirement and runs AI agents through
   the software lifecycle: requirements, design, implementation, tests, documentation, validation
   and release readiness. It does this under explicit governance: a dependency graph with gates,
   human approvals, retries, rollback, policies, an audit log and metrics.
2. **The URL shortener**, an ASP.NET Core API that the orchestrator builds and changes.

The guiding principle: agents execute under defined autonomy boundaries, and the human approves
high-impact actions and owns final quality.

## Contents

1. How this project meets the assessment
2. Folder structure
3. Setup from scratch
4. Run the URL shortener API
5. Run the three scenarios (no API key needed)
6. Run with a real AI model (optional)
7. Read the results of a run
8. Other orchestrator commands
9. Troubleshooting
10. What's new: improvements from live runs
11. Tests
12. Documentation index

If you have only 10 minutes: do section 3, run the greenfield scenario in section 5, then open the
`report.md` file the run prints at the end.

## 1. How this project meets the assessment

Each item below is a requirement from the assessment, followed by how the project meets it and
where you can see it.

### Objective: turn a requirement into a reviewable engineering outcome with controlled autonomy

One command takes a requirement through every lifecycle stage and produces a reviewable result:
a git branch with one commit per stage, a report with metrics and decision lineage, an audit log,
and the full diff. The human approves the requirements, the design, high-risk changes and the
release. See section 5 and the sample runs in [docs/SAMPLE_RUNS.md](docs/SAMPLE_RUNS.md).

### Scope

- **Greenfield (new features):** `scenarios/greenfield.yaml`. In replay it builds the short-code
  generator and its tests. In live mode it builds any new feature you describe, for example a URL
  normalizer (section 6).
- **Brownfield (enhancements, refactors, bug fixes):** `scenarios/brownfield.yaml` adds a per-link
  click limit to the existing service, including a database migration, after an impact analysis
  of the existing code.
- **Test and documentation improvements:** every run has a Tester agent that writes unit and
  integration tests and a Docs agent that writes documentation. Both are checked by real gates and
  by the Reviewer.
- **Well-defined and ambiguous requirements:** greenfield and brownfield are well defined.
  `scenarios/ambiguous.yaml` starts from a vague request ("make the links more secure and give
  marketing better analytics") and asks the human clarifying questions first.

### Core requirement 1: Requirement understanding

The Requirements agent turns the request into numbered, testable requirements with acceptance
criteria, assumptions and out-of-scope items. For an ambiguous request, the Clarifier agent first
writes `open-questions.md` with two or three interpretations per question. The human answers them,
each answer is recorded as a decision, and the run cannot continue until every question is
answered. Code: `src/Orchestrator.Agents/SdlcAgents.cs` and `ClarificationStage.cs`.

### Core requirement 2: Task decomposition

Each scenario file lists stages with their dependencies and risk level. The engine sorts them into
a dependency graph and writes it to `runs/<runId>/plan.json`, showing which stages run in which
wave. The Architect's design lists every file to change, grouped by the agent that writes it, and
an ordered task list. Code: `src/Orchestrator.Core/DagBuilder.cs`.

### Core requirement 3: Codebase reasoning (brownfield)

The ImpactAnalyst stage reads the real source code with Roslyn (the C# compiler platform) and
reports the affected files, API endpoints, database tables and tests in `impact.json`. Agents that
design, write or review code also receive a read-only outline of the codebase and the current
source files. Code: `src/Orchestrator.Agents/CodebaseIndex.cs` and `CodeContext.cs`.

### Core requirement 4: Workflow orchestration

This is the core of the project. Each part of the requirement and how it is met:

- **Explicit dependency graph:** stages and their dependencies come from the scenario file; cycles
  and unknown dependencies are rejected before the run starts.
- **Entry and exit gates:** the entry gate checks that every upstream stage is complete and
  approved. The exit gate runs real checks: `dotnet build`, `dotnet test` and a schema check of
  the agent's answer.
- **Sequential and parallel paths with synchronization:** stages whose dependencies are complete
  run together (implementation, tests and docs run in parallel), and validation waits for all of
  them before it starts.
- **Cross-stage context:** every artifact is stored with a SHA-256 hash in the run context, which is
  saved to SQLite (`runs/orchestrator.db`) after every stage, so a run can be resumed.
- **Decision lineage:** every agent decision records the decisions it was based on.
  `report --lineage` prints the tree down to the git commit of each stage.
- **Human approval checkpoints:** high-risk stages (requirements, design, release), database
  migrations, new NuGet packages, large diffs and any policy that asks for approval stop and wait
  for a person.
- **Bounded retries:** at most 3 attempts per stage with exponential backoff. The reason for the
  failure is added to the next attempt's prompt.
- **Fallback:** after the retries, one attempt with a simpler prompt, then a deterministic template
  where a safe one exists; otherwise the stage fails.
- **Rollback:** every run works on its own git branch, `agent/run-<runId>`. A failed exit gate
  resets the changed files to the last good commit.
- **Safe stop:** a token budget and a time budget, the `stop` command and Ctrl+C all stop the run at
  a safe point with its state saved, and `resume` continues it.
- **Policy guardrails (security, compliance, change control):** agents may write only inside their
  own folders; a secret scan checks every added line; NuGet packages must be on an allowlist and
  free of known vulnerabilities; migrations and diffs over 400 lines need approval.
- **Audit-grade observability and traceability:** `audit.jsonl` is an append-only record of every
  gate, approval, policy verdict, retry, rollback and rework, with a trace id, prompt and output
  hashes and the approver. The engine also emits OpenTelemetry traces and metrics.
- **Reliability metrics:** `report.md` shows success rate, retry rate, rollback frequency, MTTR
  (mean time to recover), end-to-end and per-stage latency, and human waiting time.
- **Dynamic re-planning:** if an artifact changes after it was approved (for example you edit the
  design, or change an answer), only the stages that depend on it are marked stale and run again.
- **Governance and controlled autonomy:** described under core requirement 7.

Code: `src/Orchestrator.Core` (`RunEngine.cs`, `StageExecutor.cs`, `GitWorkspace.cs`,
`Replanner.cs`, `RunMetrics.cs`) and `src/Orchestrator.Policies`. Design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Core requirement 5: Engineering output generation

The URL shortener is production-style code: Clean Architecture in four projects, a documented
OpenAPI (Swagger) contract, an EF Core schema with migrations, RFC 7807 error responses, rate
limiting, idempotent creation, background click recording and health checks. It has 250 automated
tests (unit and integration) with 91% line coverage. In live mode, the agents produced working
features with their own tests and documentation (see [docs/SAMPLE_RUNS.md](docs/SAMPLE_RUNS.md)).

### Core requirement 6: Validation and risk control

Nothing an agent produces is trusted on its word. The host runs the real build and tests at every
gate, the policies inspect the real diff, and the Reviewer agent must give a PASS or FAIL verdict
that the engine enforces. Risks, trade-offs and failure scenarios are listed with their
mitigations in [docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md), and key decisions are
recorded as ADRs in [docs/adr/](docs/adr/).

### Core requirement 7: Controlled autonomy

Each agent has an autonomy boundary: the folders it may write to (Coder: `src/UrlShortener.*`,
Tester: `tests/UrlShortener.*`, Docs: `docs/`) and an output limit. Planning agents cannot write
files at all. Every file change goes through one service that checks the boundary and the policies
before writing. Agents have no shell or tools. The human approves the important steps, and merging
a run's branch into `main` is always a human decision. The `--auto-approve` flag works only in CI.

### Core requirement 8: Final engineering summary

[docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md) covers the plan and rationale, the
artifacts, risks and trade-offs, validation, assumptions, limitations, and the improvements made
after live runs.

### Deliverables

- **Working prototype, runnable end to end:** sections 3 to 8 of this README.
- **Architecture overview (components, orchestration model, control flow, key decisions):**
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and the decision records in [docs/adr/](docs/adr/).
- **Three scenarios: greenfield, brownfield, ambiguous, each showing decomposition, orchestration
  and validation:** section 5, the files in `scenarios/`, and the committed runs described in
  [docs/SAMPLE_RUNS.md](docs/SAMPLE_RUNS.md).
- **Setup instructions:** section 3.
- **Testing approach, limitations and trade-offs:** [docs/TESTING.md](docs/TESTING.md) and the
  limitations and trade-offs sections of [docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md).

### Evaluation criteria

- **Effectiveness of agentic orchestration:** the full lifecycle runs from one command, with
  parallel stages, gates, approvals, retries, rollback, rework after a failed review and
  re-planning (core requirement 4).
- **Architecture and system design quality:** a small engine with explicit contracts (`IStage`,
  `IAgent`, `IPolicy`, `IApprovalService`), Clean Architecture for the product, and ADRs for every
  significant choice.
- **Depth of decomposition and execution quality:** dependency graphs per scenario, designs with
  ordered tasks per agent, impact analysis from real code, and real build and test gates.
- **Realism and quality of outputs:** live runs with a real model built working, tested features
  (docs/SAMPLE_RUNS.md).
- **Validation and risk management rigor:** real gates, six policies, budgets, approvals, and a
  binding review.
- **Clarity and defensibility of decisions:** decision lineage per run, ADRs, and
  [docs/assumptions.md](docs/assumptions.md) recording every interpretation.
- **Modular, testable, reliable, secure, scalable code with safe change management:** small
  projects with dependency injection, 250 tests, a branch per run with rollback, secret scanning,
  and no keys in code. Scale limits are stated honestly in the summary.
- **Engineering judgment:** the trade-offs in the summary and the record of how each live-run
  failure was diagnosed and fixed (section 10).

## 2. Folder structure

The repository follows the standard layout for a .NET solution: the solution file and shared build
settings at the root, production code in `src/`, test projects in `tests/`, and documentation in
`docs/`. Two folders are specific to this project: `scenarios/` holds the scenario definitions and
recorded model answers, and `runs/` holds the output of orchestrator runs.

```
UrlShortenerAgentic.sln          the solution
global.json                      pins the .NET SDK version
Directory.Build.props            shared settings (net8.0, nullable, warnings as errors)
src/
  Orchestrator.Core/             dependency graph engine, gates, retries, rollback, state, audit, metrics
  Orchestrator.Agents/           the agents, code context, replay and live model clients
  Orchestrator.Policies/         path, secret, package, diff size, migration and vulnerability policies
  Orchestrator.Cli/              the command line: run, resume, report, stop
  UrlShortener.Domain/           entities and rules, no dependencies
  UrlShortener.Application/      services, validators, request and response types
  UrlShortener.Infrastructure/   EF Core with SQLite, repositories, migrations
  UrlShortener.Api/              the HTTP API
tests/
  Orchestrator.Tests/
  UrlShortener.UnitTests/
  UrlShortener.IntegrationTests/
scenarios/                       greenfield.yaml, brownfield.yaml, ambiguous.yaml, feature.yaml, recordings/
runs/                            one folder per committed sample run
docs/                            engineering summary, architecture, testing, assumptions, sample runs, adr/
```

## 3. Setup from scratch

### Step 3.1: Install the tools

You need three things:

1. **The .NET SDK, version 9.0.305 exactly.** The file `global.json` pins this version and does not
   accept another one. Download it from https://dotnet.microsoft.com/download/dotnet/9.0 (choose
   SDK 9.0.305 for your operating system), or install it from a terminal:

   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 9.0.305
   ```

2. **The .NET 8 runtimes.** The projects target .NET 8, so the API and the tests need the .NET 8
   runtime and the ASP.NET Core 8 runtime. Download "ASP.NET Core Runtime 8.0" from
   https://dotnet.microsoft.com/download/dotnet/8.0 (it includes the .NET runtime).
3. **git**, any recent version.

You do not need a database server, Docker or an API key.

### Step 3.2: Check the tools

Run these three commands:

```bash
dotnet --list-sdks
```

The output must include a line starting with `9.0.305`. Other versions may also be listed; that is
fine.

```bash
dotnet --list-runtimes
```

The output must include a line starting with `Microsoft.AspNetCore.App 8.` and a line starting with
`Microsoft.NETCore.App 8.`.

```bash
git --version
```

Any version number is fine.

### Step 3.3: Download the code

```bash
git clone https://github.com/jagadeep-m/url-shortener-agentic.git
```

```bash
cd url-shortener-agentic
```

Run every command in this README from this folder. You do not need to set a git user name: the
orchestrator commits under its own name, `orchestrator-agent`.

### Step 3.4: Build

```bash
dotnet build
```

The first build downloads the NuGet packages, so it needs an internet connection once. It should
end with `Build succeeded`, `0 Warning(s)` and `0 Error(s)`.

### Step 3.5: Run the tests

```bash
dotnet test
```

This takes about 10 seconds and should print three lines starting with `Passed!`: 43 tests in
UrlShortener.UnitTests, 41 in UrlShortener.IntegrationTests and 166 in Orchestrator.Tests, 250 in
total. If you see these, the setup is complete.

## 4. Run the URL shortener API

This section is optional. It shows the product the orchestrator works on.

1. Start the API:

   ```bash
   dotnet run --project src/UrlShortener.Api
   ```

   The terminal prints `Using launch settings from ...` and then stays open: the API is running on
   http://localhost:5080 and prints one log line for each request it receives. It creates its
   database file, `urlshortener.db`, on first start.
2. Open http://localhost:5080/swagger in a browser. Every endpoint is listed there with its request
   and response format, and you can try each one with the "Try it out" button.
3. Or open a second terminal in the same folder and try these commands. Create a short link:

   ```bash
   curl -i -X POST http://localhost:5080/api/v1/links -H "Content-Type: application/json" -d '{"longUrl":"https://example.com/docs","customAlias":"demo-link"}'
   ```

   The answer starts with `HTTP/1.1 201 Created`. Follow the short link:

   ```bash
   curl -i http://localhost:5080/demo-link
   ```

   The answer is `HTTP/1.1 302 Found` with `Location: https://example.com/docs`. See the click
   statistics:

   ```bash
   curl -s http://localhost:5080/api/v1/links/demo-link/stats
   ```

   If you create the same alias twice, the second attempt returns 409 (alias taken), as designed.
4. Stop the API with Ctrl+C in its terminal.

The endpoints:

- `POST /api/v1/links` creates a short link (optional `customAlias`, `expiresAt` and an
  `Idempotency-Key` header). Returns 201, or 400 for invalid input, 409 if the alias is taken and
  429 when rate limited.
- `GET /{code}` redirects with 302 and records the click. Returns 404 if unknown and 410 if expired
  or disabled.
- `GET /api/v1/links/{code}` returns the link details, or 404.
- `GET /api/v1/links/{code}/stats` returns total clicks, clicks per day and top referrers, or 404.
- `DELETE /api/v1/links/{code}` disables the link (soft delete) and returns 204, or 404.
- `GET /health/live` and `GET /health/ready` return 200, or 503 when the database is unreachable.

Errors use the RFC 7807 ProblemDetails format. Settings are in
`src/UrlShortener.Api/appsettings.json`.

## 5. Run the three scenarios (no API key needed)

These runs use **replay mode**: instead of calling an AI model, the agents play back recorded
answers stored in `scenarios/recordings/`. Everything else is real: the git branches, the
policies, your approvals, and the `dotnet build` and `dotnet test` checks. Replay runs are free,
work offline and behave the same way every time.

### Before each scenario

1. Make sure you are on the `main` branch:

   ```bash
   git switch main
   ```

   Every run creates a new branch named `agent/run-<runId>`, commits each stage on it and leaves
   you on that branch when it finishes. Switching back to `main` before the next run is important.
2. Make sure there are no uncommitted changes in `src/`, `tests/` or `docs/`. In a fresh download
   there are none. You can check with `git status`.

### How approval prompts work

When the orchestrator needs your decision, it prints a heading such as `Approval: design`, the
path of the file to review, its content, and then this prompt:

```
approve / reject / edit >
```

Type one word and press Enter:

- `approve` accepts what is shown and the run continues.
- `reject` stops the run. Its status becomes Blocked, and you can continue later with `resume`.
- `edit` opens the file in your text editor. Make your changes, save the file, close the editor,
  then come back to the terminal and type `approve`.

Instead of typing `edit`, you can also open the file yourself in any editor (the path is printed
just above the prompt), change it, save it, and then type `approve` in the terminal. The
orchestrator reads the file again when you approve, and checks that it is still valid.

### Scenario 1: Greenfield (a new feature)

1. Start the run:

   ```bash
   dotnet run --project src/Orchestrator.Cli -- run scenarios/greenfield.yaml --replay
   ```

   The first line shows the run id, for example `Run: 7a00de95-... (replay)`.
2. **Prompt `Approval: requirements`.** Read the requirements, then type `approve`.
3. **Prompt `Approval: design`.** Read the design, then type `approve`.
4. Now the implementation, tests and documentation stages run at the same time. You do not need to
   do anything. The recorded Tester answer contains a deliberate mistake, so its first attempt
   fails the real `dotnet test` check. The orchestrator rolls the files back and retries with the
   failure message, and the second attempt passes.
5. The validation stage runs the full build and test suite, then the Reviewer checks the work.
6. **Prompt `Approval: release`.** Type `approve`.
7. The run ends with three lines: `Run <runId>: Completed`, the branch name, and the path of the
   report. The whole run takes about one minute.

### Scenario 2: Brownfield (change existing code)

1. Go back to `main`:

   ```bash
   git switch main
   ```

2. Start the run:

   ```bash
   dotnet run --project src/Orchestrator.Cli -- run scenarios/brownfield.yaml --replay
   ```

   This adds a per-link click limit to the existing API.
3. **Prompt `Approval: requirements`.** Type `approve`.
4. **Prompt `Approval: impact`.** This shows the impact analysis computed from the real code: the
   affected files, endpoints, database tables and tests. Type `approve`.
5. **Prompt `Approval: design`.** Type `approve`.
6. **Prompt `Approval: implement` with `File policy requires approval: MigrationApproval`.** The
   change includes a database migration, so the policy asks you to review the diff shown. Type
   `approve`.
7. The first recorded migration is deliberately wrong and makes 15 integration tests fail. The
   orchestrator rolls back to the last good commit and retries.
8. **Prompt `Approval: implement` again**, for the corrected migration. Type `approve`.
9. **Prompt `Approval: release`.** Type `approve`. The run ends with `Completed`.

### Scenario 3: Ambiguous requirement

1. Go back to `main`:

   ```bash
   git switch main
   ```

2. Start the run:

   ```bash
   dotnet run --project src/Orchestrator.Cli -- run scenarios/ambiguous.yaml --replay
   ```

3. **Prompt `Approval: clarify`.** The request is vague, so the Clarifier agent has written three
   questions to a file. The line after `Artifact:` shows its path, which ends in
   `runs/<runId>/open-questions.md`. Do not approve yet.
4. Open that file in a text editor (type `edit`, or open the path yourself). Each question looks
   like this:

   ```
   ## Q1. What should "more secure" mean for links?
   Why it matters: each option changes a different part of the system ...
   - A. Block malicious or phishing target URLs when a link is created
   - B. Make short codes harder to guess (longer codes, more entropy)
   - C. Require an API key to create links
   Answer:
   ```

5. Type your answer after `Answer:` under **each** of the three questions. Use one letter (for
   example `Answer: A`), several letters separated by commas (`Answer: A, C`), or your own words
   (`Answer: Other: block known phishing domains only`). Leave everything else in the file as it
   is, including the generated list at the bottom.
6. **Save the file** (Cmd+S on macOS, Ctrl+S on Windows and Linux) after you have answered all
   three questions, and close the editor.
7. Go back to the terminal and type `approve`. Your answers are recorded as decisions.
   If you approve before answering every question, the run stops with status Blocked and the
   message `Answer Q1, Q2, Q3 ...`. In that case, answer the questions in the same file, save it,
   and continue with `resume` (section 8).
8. **Prompt `Approval: requirements`.** The requirements now follow your answers. Type `approve`.
9. **Prompt `Approval: design`.** To see re-planning, change one answer now: open
   `open-questions.md` again, change for example Q1 from `A` to `C`, save the file, then type
   `approve`. The orchestrator notices the change. It shows the answer sheet again
   (`Approval: clarify`) so you can confirm it, then re-runs only the stages that depend on the
   answers: requirements and design. Type `approve` at each of these three prompts. If you do not
   want to try this, just type `approve` at the design prompt.
10. **Prompt `Approval: release`.** Type `approve`. The run ends with `Completed`.

## 6. Run with a real AI model (optional)

In live mode the agents call a real model through an OpenAI-compatible API. Everything else stays
the same: approvals, policies, real build and test checks, rollback, budgets and the audit log.

### Step 6.1: Store your API key and model name

The key is never written into the code or into the repository. Store it with .NET user secrets,
which keeps it in your user profile:

```bash
dotnet user-secrets set "LLM:ApiKey" "<your key>" --project src/Orchestrator.Cli
```

```bash
dotnet user-secrets set "LLM:Model" "<model name>" --project src/Orchestrator.Cli
```

The sample live runs used the model `gpt-6-luna`. Instead of user secrets you can set the
environment variables `LLM__APIKEY` and `LLM__MODEL`, and optionally `LLM__ENDPOINT` (the default is
https://api.openai.com/v1/chat/completions).

### Step 6.2: Greenfield with a real model (a new feature from one sentence)

1. Go back to `main`:

   ```bash
   git switch main
   ```

2. Start the run with your requirement in quotes:

   ```bash
   dotnet run --project src/Orchestrator.Cli -- run scenarios/greenfield.yaml --requirement "Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters before a link is saved"
   ```

   Always include `--requirement` when you run greenfield or brownfield with a real model. Without
   it, the model receives the scenario's built-in text, which describes the replay demonstration
   instead of a feature.
3. **Prompt `Approval: requirements`.** Read the model's requirements. Type `approve`, or `edit`
   to correct them first.
4. **Prompt `Approval: design`.** Read the design. It lists the files each agent will change: the
   Coder in `src/`, the Tester in `tests/` and the Docs agent in `docs/`. Type `approve`.
5. The implementation, tests and documentation run in parallel. Some retries are normal with a
   real model: for example, a compile error is fixed by retrying with the compiler's message.
6. The validation stage runs the full build and test suite and then the Reviewer. If either finds a
   problem, the orchestrator sends the work back to the Coder, then the Tester, then the Docs agent,
   with the findings, and reviews it again. This happens at most twice.
7. **Prompt `Approval: release`.** The summary starts with `[READY]` or `[NOT READY]`. Type
   `approve`.

A run like this takes 5 to 10 minutes and uses roughly 150,000 to 300,000 tokens, which costs a few
cents with a small model. Each run is limited to 600,000 tokens and 20 minutes of machine time
(time spent reading and approving does not count).

### Step 6.3: Brownfield with a real model (change existing code)

This asks the model to change the existing API, including its database, after analysing the
current code.

1. Go back to `main`:

   ```bash
   git switch main
   ```

2. Start the run:

   ```bash
   dotnet run --project src/Orchestrator.Cli -- run scenarios/brownfield.yaml --requirement "Add an optional per-link click limit: POST /api/v1/links accepts maxClicks (1 to 1,000,000); once a link has recorded that many clicks, its redirect returns 410 Gone. Links without a limit must behave exactly as they do today."
   ```

3. **Prompt `Approval: requirements`.** Read the requirements. Type `approve`.
4. **Prompt `Approval: impact`.** This is the impact analysis of the current code: the files,
   endpoints, database tables and tests the change will touch. Check that it names the link
   creation and redirect code and the `ShortLinks` table. Type `approve`.
5. **Prompt `Approval: design`.** Read the design, including the database change. Type `approve`.
6. The documentation and the implementation now run. The implementation adds a database
   migration, so you get **prompt `Approval: implement` with `File policy requires approval:
   MigrationApproval`**, followed by the full diff. Read the migration (it should add a nullable
   `MaxClicks` column) and type `approve`.
7. The Tester writes new tests, then validation runs the full build and test suite and the
   Reviewer. If something is missing, the work goes back to the Coder, the Docs agent and the
   Tester, one after another, and is reviewed again (at most twice). You do not need to do anything
   during this part.
8. **Prompt `Approval: release`.** Type `approve`. The run ends with `Completed`.

The sample run of exactly this command ([docs/SAMPLE_RUNS.md](docs/SAMPLE_RUNS.md)) took 9 minutes,
used 269,124 tokens and changed 14 files, including the migration, with 106 tests passing.

### Step 6.4: Other live runs

For any other feature, `scenarios/feature.yaml` runs the same stages as brownfield (with impact
analysis) for whatever you describe:

```bash
dotnet run --project src/Orchestrator.Cli -- run scenarios/feature.yaml --requirement "Add an endpoint that returns a QR code for a short link"
```

The ambiguous scenario also works with a real model, without `--requirement`. Answer the model's
questions exactly as in scenario 3 of section 5:

```bash
dotnet run --project src/Orchestrator.Cli -- run scenarios/ambiguous.yaml
```

If a live run ever stops with `Paused (Run token budget exceeded ...)`, continue it with a higher
budget as shown in section 8.

## 7. Read the results of a run

At the end of every run, the last line printed is the path of its report. Everything is in the
folder `runs/<runId>/`:

- `report.md` is the best starting point: the status and duration of every stage, the reliability
  metrics (success rate, retry rate, rollback frequency, MTTR, latency, human waiting time) and the
  decision lineage.
- `audit.jsonl` is the audit log, one line per event.
- `plan.json` is the dependency graph and the order in which stages ran.
- `diff.patch` is every change the run made.
- `requirements.json`, `design.json`, `tests.json`, `validate.json` and the other `.json` files are
  the agents' answers.
- The `.build.log` and `.test.log` files are the real build and test output of each check.

To see the commits of a run, one per stage:

```bash
git log --oneline agent/run-<runId>
```

The changes stay on that branch. To keep them, merge the branch yourself with
`git merge agent/run-<runId>`. Merging is deliberately left to a person.

## 8. Other orchestrator commands

Replace `<runId>` with the id printed at the start of the run.

Rewrite the report and print the decision tree down to each commit:

```bash
dotnet run --project src/Orchestrator.Cli -- report <runId> --lineage
```

Pause a running run at a safe point (run this from a second terminal; Ctrl+C in the first terminal
does the same):

```bash
dotnet run --project src/Orchestrator.Cli -- stop <runId>
```

Continue a run that was paused, blocked or failed, from where it stopped:

```bash
dotnet run --project src/Orchestrator.Cli -- resume <runId>
```

`resume` also re-runs the affected stages of a completed run if you edited one of its files.

If a live run stopped because it used its whole token budget (the message says `Run token budget
exceeded`), a plain `resume` would stop again, because tokens count for the whole run. Give it a
higher budget when you resume:

```bash
dotnet run --project src/Orchestrator.Cli -- resume <runId> --max-tokens 800000
```

The new budget must be above the tokens already used. The change is recorded in the audit log as
`token_budget_changed`, with your user name.

The `--auto-approve` option exists only for automated tests and CI. It is refused unless the
environment variable `CI` is set to `true`.

## 9. Troubleshooting

- **"A compatible .NET SDK was not found"**: install SDK 9.0.305 exactly (step 3.1).
- **Tests fail with "You must install or update .NET to run this application"**: install the .NET 8
  runtimes (step 3.1).
- **"Commit or stash these changes before an agent run"**: there are uncommitted changes in `src/`,
  `tests/` or `docs/`. Commit them or undo them, then start again.
- **A new run starts from an `agent/run-...` branch, or you see "HEAD is on 'agent/run-...'"**:
  run `git switch main` first.
- **"--requirement needs a real model"**: remove `--replay` (and configure section 6), or remove
  `--requirement` to run a recorded scenario.
- **"The API key was rejected", "Model ... was not found" or a 429 error**: check the key and model
  name from step 6.1, and your account's billing and rate limits.
- **"Run <runId>: Blocked (Answer Q1 ...)"**: answer every question in
  `runs/<runId>/open-questions.md`, save the file, then run `resume <runId>`.
- **"Run <runId>: Paused (Time budget ...)"** or a run you stopped: run `resume <runId>`.
- **"Run <runId>: Paused (Run token budget exceeded ...)"**: the run used its whole token budget.
  Resume it with a higher budget, for example `resume <runId> --max-tokens 800000` (section 8).
- **"Address already in use" when starting the API**: another program uses port 5080. Stop it, or
  start the API on another port with
  `dotnet run --project src/UrlShortener.Api --urls http://localhost:5090`.
- **"--auto-approve is restricted to tests/CI"**: this is expected outside CI. Approve at the
  prompts instead.

## 10. What's new: improvements from live runs

After the three scenarios worked in replay mode, the orchestrator was run 18 times with a real
model (once on the ambiguous scenario, fifteen times on new features and twice on the brownfield
change). Every run that went wrong
stopped safely: a gate, a policy or the review caught the problem and nothing broken was merged.
Each problem was then fixed, with tests. The final runs completed from start to finish. Each run is
kept as evidence, together with the fix it led to, in [docs/SAMPLE_RUNS.md](docs/SAMPLE_RUNS.md).
The reasoning behind each change is in the "Live-run hardening" section of
[docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md).

### New features

- Live mode with a plain-English requirement: `--requirement "..."` on any scenario, and a new
  `scenarios/feature.yaml` that adds an impact analysis stage.
- Agents that design, write or review code receive a read-only view of the code: an outline built
  with Roslyn, the files named in earlier stages, and the rest of the product source. The local
  folder path is hidden before anything is sent to the model.
- A binding review. The Reviewer must answer PASS or FAIL. A FAIL, or a failing full test run,
  sends the work back to the Coder, the Tester and the Docs agent with the findings, at most twice.
- A release recommendation: the release prompt starts with `[READY]` or `[NOT READY]`.
- Time spent waiting for a human no longer counts against the time budget.
- `resume <runId> --max-tokens <n>` continues a run that used its whole token budget, and records
  who raised the budget in the audit log. The default budget is now 600,000 tokens per run.

### Enhancements

- New tests may fail at first ("red tests") while the code is unfinished, as long as every existing
  test passes. The full test run at validation makes the final decision, so the Coder is not blamed
  for a wrong test and the Tester is not blamed for unfinished code.
- The Reviewer works from a fresh build and test run made just before the review, and sees every
  file the run changed, including tests and documentation from earlier rounds.
- During a rework, the Coder, the Tester and the Docs agent run one after another, so each sees the
  previous fix.
- Each folder belongs to one agent (Coder `src/`, Tester `tests/`, Docs `docs/`). If an agent writes
  into another agent's folder, it gets one retry with an explanation before the run stops. This is
  recorded in [docs/adr/0011-one-correction-for-role-boundary-denials.md](docs/adr/0011-one-correction-for-role-boundary-denials.md).
- Clearer feedback for retries: failing test names with their error messages and the line that
  failed, schema errors that name the missing field, and a separate verdict field for the review.
- Higher output limits where real answers were cut off: 12,288 tokens for the Architect and 8,192
  for the Reviewer.

### Fixes

- A code stage that returns no files now fails instead of passing.
- The Tester always sees the API contract and the test setup, so it uses the correct request
  fields.
- The Docs agent updates its own document instead of adding a second one, and never writes test
  results into it.
- Swagger now documents the API contract accurately (required fields and the redirect header).
- The brownfield replay recordings match the current API files.
- Reports of live runs state the number of tokens used correctly.

## 11. Tests

```bash
dotnet test
```

250 tests in three projects. They create temporary SQLite databases and git repositories and need
no network. To measure code coverage:

```bash
dotnet test --collect "Code Coverage;Format=cobertura" --results-directory ./TestResults
```

[docs/TESTING.md](docs/TESTING.md) explains the test levels, what each project covers, how the
gates use the tests, and the coverage numbers.

## 12. Documentation index

- [docs/ENGINEERING_SUMMARY.md](docs/ENGINEERING_SUMMARY.md): plan and rationale, artifacts, risks
  and trade-offs, validation, assumptions, limitations, and the improvements from live runs.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): components, orchestration model, control flow and key
  decisions.
- [docs/adr/](docs/adr/): one record per significant decision.
- [docs/TESTING.md](docs/TESTING.md): testing approach and coverage.
- [docs/SAMPLE_RUNS.md](docs/SAMPLE_RUNS.md): every committed run and what it shows.
- [docs/assumptions.md](docs/assumptions.md): every interpretation of the specification, and what
  each live run taught.
