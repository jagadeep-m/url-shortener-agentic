# Assumptions

- SDK 9.0.305 builds net8.0 projects, per the user's final version choice.
- Expiry, when supplied, must be strictly after creation. At the exact expiry timestamp,
  a link is unavailable. UTC kind is required; local/unspecified timestamps are rejected.
- Codes are case-sensitive, including custom aliases; reserved names are blocked
  case-insensitively to avoid route conflicts.
- IpHash is optional. When provided it must be a 64-character hexadecimal SHA-256
  digest and is normalized to lowercase. No raw-IP property exists. The redirect endpoint hashes the connection IP before enqueueing.
- The domain checks absolute HTTP(S) URLs and length. The application rejects the request
  host, configured PublicBaseUrl host, trailing-dot/case variants and equivalent loopback addresses.
- Missing-link click recording returns false. Existing disabled/expired links may accept
  already-queued click events. The redirect flow will decide whether a click is eligible.
- Repository calls save immediately and use scoped contexts with no unrelated pending
  changes. Reads are untracked; re-query after atomic database updates for fresh counters.
- The 10,000-code uniqueness test is a probabilistic smoke test, not a
  uniqueness guarantee; the unique database index remains authoritative.

## Phase 2

- "Max 3 retries" means one initial generated-code attempt plus up to three retries.
  Custom aliases are never retried. Randomly generated reserved codes consume an attempt
  without writing to the database.
- Idempotency-Key is optional, case-sensitive, global to this unauthenticated prototype,
  and limited to 128 non-control characters. Request fingerprints include LongUrl,
  CustomAlias and ExpiresAt. Changed payloads return 409. Keys currently have no expiry;
  callers should use high-entropy unique values. A future authenticated service should
  scope keys by tenant/user and define retention.
- POST replay returns the original creation fields with 201 and the original Location
  even if the link has since expired or been disabled; it never creates a new link.
- ExpiresAt must carry UTC kind (use a Z suffix). Invalid input, including URL controls
  or a trailing newline in an alias, returns 400.
- The queue is bounded and in memory. Analytics can be lost on process failure or after
  three failed writes. Stats clients may briefly observe fewer clicks and should poll.
- Rate limiting uses the connection IP and does not trust X-Forwarded-For. Trusted-proxy
  forwarding must be configured explicitly before deployment behind a reverse proxy.
- Cache invalidation covers this single API process. External DB writers or multiple
  API instances require shared invalidation or a different caching strategy.
- Configure PublicBaseUrl for a canonical public short URL and AllowedHosts for deployment.
  Without PublicBaseUrl, the request origin is used. No authentication is added in this phase.
- Development startup applies migrations automatically as requested. Other environments
  require explicit migration application before readiness succeeds.

## Phase 3

- IStage implementations are trusted in-process components supplied by the caller; this
  engine does not execute YAML-named classes dynamically. Stage callbacks must not mutate
  engine-owned statuses, accepted artifacts, scenario definitions or decision history.
- The engine runs strictly sequentially. Retry/budget settings are validated and persisted,
  but automatic retries and time/token enforcement are deferred. Caller cancellation is
  supported now and results in a persisted Paused run.
- StageOutput has one primary Artifact per stage and a list of Decision records. Outputs
  become accepted only after a successful exit gate. Concrete stages must implement real
  build/test/schema checks in later phases; the core delegates to their gate methods.
- High-risk stages require an injected approval service; there is no default auto-approval.
  Low/medium stages need no separate human approval. Policy definitions and evaluation
  orchestration remain future work beyond the IPolicy contract.
- Resume is an explicit retry of unfinished work, not automatic re-planning. It checks
  saved ID/dependency/risk metadata but cannot detect changed implementation code. A
  changed graph or risk level requires a new run until re-planning is implemented.
- Stage side effects must be idempotent or reconciled before resuming an interrupted run:
  a process may stop after an external effect but before its checkpoint. The core cannot
  promise exactly-once external execution.
- SQLite checkpoints and the separate JSONL file are not one transaction. A crash can
  leave a checkpoint without its final audit event (or a partial final append). Failures
  of persistence/audit operations propagate; this is not a tamper-proof/compliance ledger.
- Use one shared audit writer per engine host. The engine's per-run file lease prevents
  concurrent executions locally; independent direct writer instances are not coordinated.
- The state-store schema is initialized with CREATE TABLE IF NOT EXISTS in a separate
  SQLite file. Schema evolution for orchestrator state remains future work; the URL
  shortener's migrations and database are unaffected.

## Phase 4

- The live model client is a small OpenAI-compatible HTTPS adapter implementing
  `IChatClient`, rather than an extra provider package, because the stack list names only
  `Microsoft.Extensions.AI`. Any OpenAI-compatible endpoint works via `LLM:Endpoint`.
  `LLM:Model` has no default so the run never guesses a model the account cannot use.
- Recordings are keyed by scenario file name and stage ID. The greenfield recording
  re-emits the already-built Base62 generator, domain tests and a generated doc page, then
  runs the real build/test gate. It demonstrates the workflow; it does not regenerate every
  product file from scratch.
- Requirements, Design and Release are reviewed after execution (the human sees the draft).
  High-risk stages without a reviewable artifact are still approved before execution.
- `--auto-approve` additionally requires `CI=true` so it cannot be the default by accident.
- Agent writes happen in the exit gate, after the human review. Phase 5 commits them on the
  run branch and reverts failed attempts.
- Token usage is charged only when the client reports it; replay reports zero.

## Phase 5

- `maxAttempts` counts total attempts (1–3), so 3 means one try plus two retries. Fallback
  attempts come on top: a Coder stage can make up to five attempts in all.
- Retry and fallback attempts increment `Attempts`, which is cumulative across resumes. Replay
  keys `<stage>.attempt<N>.json` on that number.
- Scheduling is wave-based (`Task.WhenAll` per wave). A failure in one wave lets its siblings
  finish and commit, then stops the run.
- The time budget applies to each engine session and counts machine time only; time spent
  waiting for approvals is excluded (changed after the first live run, where a reviewer's reading
  and a re-plan used up 15 of 20 minutes). Resume starts a new time window. The token budget is cumulative; resuming after it
  is exhausted needs a higher saved budget.
- Rollback is scoped to agent paths and uses the run branch's HEAD as the last good commit.
  The repository needs at least one commit; agent paths must be clean and nothing may be
  staged when a run starts or resumes. Do not edit agent paths while a run is active.
- Exit gates build the whole solution but run only the URL Shortener test projects. Agents
  cannot change the orchestrator, and its timing-sensitive tests should not decide product gates.
- Stage-level `budget` entries are parsed and validated but not enforced separately; the run
  budget applies.

## Phase 6

- A policy Deny is final for that attempt chain: retrying a model that tried to write a
  secret or touch `.github/` is not useful without a human, so the stage fails and the
  reason is kept for an explicit resume.
- The secret scan inspects only lines the agent adds. Existing content is out of scope.
  Generic "secret-looking assignment" matches need approval rather than a denial because
  test fixtures often look like secrets.
- A package version change counts as a new package. Package references are read from
  `.csproj` files only; central package management (`Directory.Packages.props`) is not used.
- The vulnerability check examines proposed packages before they land, not the whole
  solution. If it cannot run, a human decides (RequireApproval); it never silently allows.
- Diff size counts added plus removed lines across the whole change set (approval above 400,
  denial above 2,000). The LCS diff falls back to "replace the changed middle" for very
  large files, which can only overcount.
- Editing an existing migration is denied; the model snapshot may change with a new migration.

## Phase 7

- Re-planning watches the reviewed draft files in `runs/<runId>/`. A human changes an
  artifact by editing that file, during any approval prompt or while the run is paused or
  completed. Edits to workspace source files directly are not artifacts; agents' file changes
  re-land only through their stage.
- An edited stage re-lands its edited draft through its gates and, when high-risk, a fresh
  approval, so a human edit to code still passes policies and build/test gates.
- Detection happens between waves. An edit made while a wave is running is picked up after
  that wave; a stage that already used the old artifact is then re-run.
- A human edit discovered at re-plan time cannot name its author from the file alone, so it
  is attributed to `human-editor (<OS user>)`. An edit made during an approval prompt
  is attributed to the approver.
- MTTR measures from the first failed attempt to the next completion of that stage, including
  any human time spent before an explicit resume. That is the recovery time an operator
  experiences. Per-stage latency excludes approval waits inside the attempt.
- "Success rate" in a single-run report is 0% or 100%; the "across all runs" section gives
  the meaningful rate.

## Phase 8

- `Microsoft.CodeAnalysis.CSharp` 4.11.0 (Roslyn) was added to Orchestrator.Agents with the
  user's approval, for syntax-tree impact analysis. Syntax only: no semantic model, so impact
  follows names, not resolved symbols. Two types with the same name in different namespaces
  would be conflated, and transitive call chains are not followed.
- Both brownfield examples in the brief (expiry enforcement and per-IP rate limiting; lost
  click counts) were already implemented in Phase 2, so, with the user's agreement, brownfield
  adds a per-link click limit, a change that needs a migration.
- The click limit is soft: clicks are recorded asynchronously, so concurrent redirects can pass
  it by the number of queued clicks. Capped links read their live count from the database
  because the redirect cache holds a stale `ClickCount`.
- `CreateLinkRequest.MaxClicks` is omitted from JSON when null, so idempotency fingerprints of
  keys stored before the change still match.
- Before brownfield, `PersistenceTests` asserted exactly two applied migrations. It now
  compares the applied migrations with the assembly's migrations, so a new migration does not
  break an unrelated test. This edit was made to the product tests directly, outside any
  scenario.
- In brownfield, tests depend on implement, because new tests calling a new API cannot pass a
  gate before the API exists. Greenfield keeps tests parallel with implement.
- Every reset that restores files is now audited as `rollback`, including resets after a
  failed attempt that is later retried. Rollback frequency counts those resets.
- The ambiguous scenario ends at an approved scope rather than code. Its replay recordings
  cover the demo answers (Q1 = A or C, Q2 = B, Q3 = A); other combinations fall back to a
  "use live mode" artifact.
- Humans may change only the Answer lines in open-questions.md; changing questions or options
  blocks the stage.

## Phase 9

- The repository has no commits yet, so the fresh-clone check emulated a clone exactly: the
  files git would track (`git ls-files --cached --others --exclude-standard`, 173 files, no
  databases, build output or logs) were copied into an empty folder and committed as the
  initial commit. Every README step was then followed verbatim:
  - prerequisites check, `dotnet build` (0 warnings after fixing one test-analyzer warning) and
    `dotnet test` (204 passed)
  - the API run with the three curl examples (201, 302, stats) and Swagger
  - greenfield, brownfield and ambiguous with `--replay` (all completed), with `git switch main`
    between runs, and `report <runId> --lineage`

  After the first real commit, a plain `git clone` should be re-checked once.
- Coverage uses the collector bundled with Microsoft.NET.Test.Sdk (`--collect "Code Coverage"`),
  so no coverage package was added.
- The prerequisite is SDK 9.0.305 plus the .NET 8 runtimes, not the ".NET 8 SDK" named in the
  brief, because global.json pins the SDK chosen in Phase 0. The projects target net8.0.

## Dependency cleanup

- FluentAssertions was removed from the three test projects: no test used it (all assertions use
  xUnit's `Assert`), and version 8 requires a paid licence for commercial use. It was also removed
  from the NuGet allowlist, so agents cannot propose adding it.
- `OpenTelemetry.Extensions.Hosting` was removed from the API, which never called it. Tracing and
  metrics use the built-in `ActivitySource` and `Meter`, the .NET OpenTelemetry API; an exporter is a
  production step.
- `Orchestrator.Cli` keeps its Serilog and OpenTelemetry hosting references. The CLI does not call
  them directly, but they bring in `Microsoft.Extensions.Configuration`, which reads user-secrets and
  `LLM__*` environment variables. Replacing them with an explicit configuration package is a
  possible later cleanup.

## Live mode for new requirements

- New requirements are given in plain English with `--requirement` on `scenarios/feature.yaml`,
  which follows the brownfield lifecycle. They need a real model; replay has no answers for them.
- Agents receive code read-only: an outline, plus the full current contents of the existing product
  files named in the run's artifacts. A file the design proposes to create is not read (it doesn't
  exist yet). Very large files and context beyond the overall cap are listed as omitted rather than
  silently truncated.
- Context and the token budget interact: file contents are input tokens and count towards
  `maxTokens`. `feature.yaml` keeps the default 400,000-token budget, which with a small model costs
  only cents, rather than a lower cap that file context could exhaust.
- The model name comes from `LLM:Model` at the start of a run and is kept with the run, so a resume
  reports the model the run started with.
- Live mode has run against a real model: the ambiguous scenario with `gpt-6-luna` (sample run
  `a30912af…`). That run found three issues, all fixed with tests:
  - the time budget counted human waiting time
  - the Architect's 6,144-token output limit was too small once designs cite code
  - the answer sheet's generated summary looked editable and went stale after edits; it is now
    labelled, and re-rendered whenever the sheet is shown again
- The first live greenfield run (`27227535…`, URL normalizer) failed safely and exposed two more gaps,
  both fixed with tests:
  - The design named types (`LinkService`, `ShortLink`) but no paths, so the code context was empty.
    The Coder correctly answered "unable to implement" with no files, and the gate wrongly passed
    that. Files are now also found by type name, the Coder and Tester get the outline, and a
    code-writing stage must return at least one file.
  - The Tester never saw the API contract and guessed `url` for `longUrl`, so every HTTP test got
    400. The Tester now always receives `Contracts.cs` and `LinkEndpoints.cs`.

  Nothing broken landed: each failed attempt was rolled back and the run stopped as Failed.
- The second live greenfield run (`63000678…`) completed, but the Coder wrote a correct
  `UrlNormalizer` that nothing called. The Reviewer said so ("the core requirement is therefore not
  met"), yet validate passed, because its gate only checked the build and tests. The review is now
  binding: `Verdict: FAIL` fails the gate and triggers one rework cycle, in which the Coder, Tester
  and Docs re-run with the findings. The Release agent must state READY or NOT READY.
- Rework is bounded to one cycle (`StageExecutor.MaxReworks`) to cap cost and avoid endless
  review loops; the human can still resume a failed run after changing the requirement or design.
- The third live greenfield run (`67cdd834…`) failed safely: the design named no files, the Coder
  again wrote an unconnected normalizer, and the Tester's correct tests were retried and rolled back
  as if they were wrong. Two fixes, with tests:
  - Code roles (Architect, Coder, Tester, Reviewer) see the whole product source (without tests and
    migrations), within the 120,000-character budget; named files come first. The product is small
    enough for this; a larger one would need retrieval instead.
  - Tests may land red: at the tests gate, failures only in test classes the Tester newly created
    are allowed when the build and all existing tests pass. Validation then runs everything, and a
    failure there reworks implement, tests and docs once with the failure details. Edited existing
    test classes are not tolerated, so the Tester cannot weaken existing tests to get through.
- The fourth live greenfield run (`a6daadb7…`) showed both fixes working: the normalizer was wired
  into `LinkService`, seven new tests landed red, validation reworked the code and all 103 tests
  passed. The second review then failed with one real bug (a `?` inside a fragment is treated as a
  query) and one false finding: it called a save-path test missing that the first round had written,
  because the rework's artifacts no longer named that file. The Reviewer now sees every file the run
  changed (only the Reviewer; writing roles keep the product-source view). The real bug still fails
  the run, because rework stays bounded to one cycle.
- The fifth live greenfield run (`ea1a66cd…`) stopped at implement: the approved design listed
  `LinkServiceTests.cs` and `ApiTests.cs` among the files to change, and the Coder wrote them, which
  its boundary denies. The design and Coder prompts now assign each folder to one role, and such a
  denial is retried once with feedback instead of failing the run (ADR 0011). Writes outside the
  agent paths, secrets and packages still stop at once.
