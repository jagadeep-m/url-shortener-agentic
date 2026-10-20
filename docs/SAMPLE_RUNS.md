# Sample runs (evidence)

Every folder under `runs/` is one orchestrator run. This page lists them and explains what each one
shows. The folders hold only run data; all documentation lives in `docs/`.

## Replay runs

One completed `--replay` run of each scenario, produced by the CLI with real git branches and the
real `dotnet build` / `dotnet test` gates (in a scratch clone, so this repository's product code is
unchanged). Each folder holds `input.md`, `plan.json`, `audit.jsonl`, `report.md` and `diff.patch`.

| Scenario | Run | What it shows |
| --- | --- | --- |
| Greenfield | [f0f8eac8…](../runs/f0f8eac8-2e43-47b5-8419-1b5261d5bde1/report.md) | Requirements, design, then implement, tests and docs in parallel, then validate and release. The Tester's first attempt fails the real test gate, is rolled back and retried (retry rate 12.5%, MTTR 8.1 s). |
| Brownfield | [360814b7…](../runs/360814b7-a4b1-49ec-a922-b3e8d4e4516c/report.md) | Per-link click limit. Roslyn-grounded impact analysis ([impact.json](../runs/360814b7-a4b1-49ec-a922-b3e8d4e4516c/impact.json)): 12 files, 3 endpoints, the `ShortLinks` table, affected tests. The EF migration needs approval; the first attempt's NOT NULL column fails 15 tests, 8 files are rolled back to the last good commit, and the retry lands. [diff.patch](../runs/360814b7-a4b1-49ec-a922-b3e8d4e4516c/diff.patch) is the whole change. |
| Ambiguous | [43cdab25…](../runs/43cdab25-adf9-435f-a94e-94a289893d70/report.md) | "Make the links more secure and give marketing better analytics." The Clarifier writes [open-questions.md](../runs/43cdab25-adf9-435f-a94e-94a289893d70/open-questions.md) with 2 to 3 interpretations each; answers become decisions; changing Q1 from A to C at the design prompt re-plans requirements and design only (see the lineage in report.md). |

## Live runs (real model)

All live runs used `gpt-6-luna` through the OpenAI-compatible client. Each row links to the run's
`report.md`; the folder also holds `audit.jsonl`, `plan.json`, `diff.patch` and the model's own
answers (`requirements.json`, `design.json`, `implement.json`, `tests.json`, `docs.json`,
`validate.json`, `release.json`).

### Ambiguous requirement

| Run | What it shows |
| --- | --- |
| [a30912af…](../runs/a30912af-d7be-4606-a100-d2d4a34b12ac/report.md) | The vague request answered by a real model, with nothing scripted. The model wrote **6 clarifying questions of its own** ([open-questions.md](../runs/a30912af-d7be-4606-a100-d2d4a34b12ac/open-questions.md)), then requirements, a design, a 127-line scope document and a review from the answers. Changing Q6 from A to B after design re-planned clarify, requirements and design only. Twice the design was cut off at the output limit and recovered on retry. The run paused at the 20-minute time budget and resumed cleanly. 56,754 tokens. |

It exposed three issues, fixed in `419aab1`: the time budget counted human waiting time; the
Architect's output limit was too small; and the answer sheet's summary looked editable and went stale.

### Brownfield: change existing code

Both runs used `scenarios/brownfield.yaml` with the click-limit requirement shown in step 6.3 of the
README, in a scratch clone, with a script typing `approve` at every prompt.

| Run | Result | What it shows |
| --- | --- | --- |
| [0ce75786…](../runs/0ce75786-e287-44f5-9fc9-99b9e5a240d0/report.md) | **Completed** after a resume | Impact analysis, design, a real EF Core migration approved at the policy prompt, code, tests and docs, then two rework cycles for real review findings (a concurrency test, idempotency with older requests). The third review would have passed the old 400,000-token budget, so the run paused safely. A plain `resume` could not continue it, which led to the 600,000-token default and `resume --max-tokens`; resuming with `--max-tokens 800000` (audited as `token_budget_changed`) completed it with 107 tests passing and 470,935 tokens. |
| [9a60f0f8…](../runs/9a60f0f8-013c-40b1-9fc1-42ad8578f411/report.md) | **Completed** | The same requirement from a fresh start with the new budget. Impact analysis named 17 files, 2 endpoints and the `ShortLinks` table; the migration added a nullable `MaxClicks` column; validation caught 5 red tests and one rework fixed them; review PASS with 106 tests; released in 9 min 22 s with 269,124 tokens. [diff.patch](../runs/9a60f0f8-013c-40b1-9fc1-42ad8578f411/diff.patch) holds all 14 changed files. |

### New features from a plain-English requirement

These runs used `--requirement`. Each one taught the orchestrator something; the fix is named
with the commit that made it, and every fix is covered by tests. Together they are the evidence
for the "Live-run hardening" section of [ENGINEERING_SUMMARY.md](ENGINEERING_SUMMARY.md).

| # | Run | Requirement | Result | What it showed | Fix (commit) |
| --- | --- | --- | --- | --- | --- |
| 1 | [2a2cd0af…](../runs/2a2cd0af-dc93-462c-920d-9492b49082fd/report.md) | QR code endpoint (`feature.yaml`) | **Completed** | First live code-writing run: impact analysis, design, code, tests and docs by the model. Three real errors (a compile error, a wrong namespace in a test, a malformed answer) were each fixed by a retry with the failure as feedback. | None needed: the mechanisms worked |
| 2 | [27227535…](../runs/27227535-ea23-4774-ad39-3884a16ea23f/report.md) | URL normalizer | Failed safely | The design named types but no file paths, so the Coder saw no code and answered "unable to implement" with no files, which the gate accepted. The Tester never saw the API contract and guessed `url` for `longUrl`. | Files found by type name; Coder and Tester get the outline; code stages must return files; the Tester always sees the API contract (`d764b6c`) |
| 3 | [63000678…](../runs/63000678-880a-438b-86db-4675fa2f83f6/report.md) | URL normalizer | Completed, but wrong | The Coder wrote a correct normalizer that nothing called. The Reviewer said so, but its opinion was not binding and the run completed. | Binding review (`Verdict: PASS/FAIL`) with a rework cycle; release must state READY / NOT READY (`f446510`) |
| 4 | [67cdd834…](../runs/67cdd834-be59-4b57-b261-444f280e535f/report.md) | URL normalizer | Failed safely | The design named no files; the Coder again wrote unconnected code, and the Tester's *correct* tests were blamed, rolled back and retried until the stage failed. | Code roles see the whole product source; new tests may land red; a failing validation reworks the code (`ec02ccf`) |
| 5 | [a6daadb7…](../runs/a6daadb7-5d5d-4b23-bf1f-2c6a02f1b810/report.md) | URL normalizer | Failed safely | Run 4's fixes worked (red tests, rework, 103 tests passing). The second review found one real bug and one false one: it called a test missing that an earlier round had written. | The Reviewer sees every file the run changed (`b742229`) |
| 6 | [ea1a66cd…](../runs/ea1a66cd-4cf5-4c3f-94dd-c80fe7578567/report.md) | URL normalizer | Failed safely | The approved design told the Coder to edit test files; its autonomy boundary denied the write and stopped the run. | Folders assigned to roles in the design and Coder prompts; one corrected retry for a write in another role's folder ([ADR 0011](adr/0011-one-correction-for-role-boundary-denials.md)) (`4efbde9`) |
| 7 | [08733518…](../runs/08733518-1840-467d-84cb-a2c671458612/report.md) | URL normalizer | Failed safely | Working code and 100 passing tests, but two false review findings: one trusted a stale "tests are red" note, one could not read `docs/`. | The full build and test run happens before the review and is given to the Reviewer; the Reviewer reads the run's docs (`8359861`) |
| 8 | [696440e5…](../runs/696440e5-f907-464d-9975-fbceab43305d/report.md) | URL normalizer | Failed safely | A real finding: Docs, running beside the Coder during rework, documented the code before the fix and added a second, disagreeing document. | Rework runs implement, then tests, then docs, one at a time; Docs updates its own document (`dd93293`) |
| 9 | [63a8e171…](../runs/63a8e171-216f-468b-8dc6-70a7805d4c7d/report.md) | URL normalizer | Failed safely | The reverse of run 4: the Tester's tests were wrong (read a response twice, counted every row in a shared database) and the Coder was retried against them. | Run-created tests may be red at both code gates; validation stays strict; failure details name the line that threw (`dd93293`) |
| 10 | [484a3044…](../runs/484a3044-7951-4f9e-a3ec-6a480cf01baa/report.md) | URL normalizer | Failed safely | Validation caught the broken test and the rework fixed it (91 passing), but Docs wrote "this test fails" into the feature document. | Docs document the feature, never the run's test results (`dd93293`) |
| 11 | [7b077f47…](../runs/7b077f47-c8e6-42ad-ac0f-3730aa723e32/report.md) | URL normalizer | **Completed** | Reviewed, reworked, reviewed again with PASS, released. Two reviews were retried for a missing `Verdict:` line. | Structured `verdict` field (`dd93293`) |
| 12 | [6219083a…](../runs/6219083a-3ea0-4b42-88a8-30f952bcbcb8/report.md) | URL normalizer | Failed safely | No retries at all; the second review found a new, real bug (a `?` inside a fragment) and one rework cycle was the limit. | Two rework cycles; the Reviewer lists every gap at once and re-checks its earlier findings (`dd93293`) |
| 13 | [4d964a3b…](../runs/4d964a3b-a8b8-4791-aab0-7bfebc2bad32/report.md) | URL normalizer | **Completed** | Validation caught two red tests, one rework fixed them (105 passing), review PASS, released. Docs needed two retries on a vague schema error. | Schema errors name the missing fields (`dd93293`) |
| 14 | [40a54cfa…](../runs/40a54cfa-1b23-438b-b2e0-6d04b90e076e/report.md) | URL normalizer | **Completed** | One red test, one rework, 95 passing, review PASS, released, no compile errors. One retry for an answer without a `files` key. | A missing `files` key means no file changes (`dd93293`) |
| 15 | [9f8dfa04…](../runs/9f8dfa04-28a7-479d-93d4-46187d1e39ba/report.md) | URL normalizer | **Completed** | Final run on the committed code from a fresh clone: the review found a real bug, one rework fixed it, 106 tests passing, review PASS, released in 6 min 40 s with 203,131 tokens. One review was cut off at 4,096 output tokens and retried. | Reviewer output limit 8,192 tokens (`678ac9b`) |

Notes:
- Runs 1 to 8 were approved by a person at the console. Runs 9 to 15 were run in a scratch clone of this
  repository with a script that typed `approve` at every prompt, so their human-wait time is near
  zero; the gates, policies and reviews were unchanged.
- Each run's `agent/run-<id>` branch is not part of this repository. `diff.patch` holds the whole
  change the run made, and `report.md` lists every stage's commit.
- One more live run (`ef44a771…`) is not kept: it was started without `--requirement`, so the model
  received the replay rehearsal text instead of a feature request.

The `runs/orchestrator.db` checkpoint store, per-attempt build/test logs and run internals
(`*.result.json`, lock files) are not committed.
