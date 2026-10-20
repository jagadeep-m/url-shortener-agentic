# Run 4d964a3b-a8b8-4791-aab0-7bfebc2bad32

Status: Completed

Scenario: greenfield

Mode: Live (model gpt-6-luna)

Trace: c012b6beb009f0c67430a88b2ea0c5b0

Branch: agent/run-4d964a3b-a8b8-4791-aab0-7bfebc2bad32 (last good commit d6409c120551ed3325dc2b620fcf419c0545e16f)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | 31966f0438 | 15.6 s | 0.0 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | bd0096a755 | 40.0 s | 0.0 s | Agent JSON and file boundaries validated. |
| implement | Completed | 3 | — | a80086ca1d | 1 min 49 s | 0.0 s | dotnet build succeeded; dotnet test: 90 passed, 0 failed, 0 skipped. |
| tests | Completed | 2 | — | c7520057fe | 1 min 2 s | 0.0 s | dotnet build succeeded; dotnet test: 105 passed, 0 failed, 0 skipped. |
| docs | Completed | 4 | — | 9e2f8117c0 | 1 min 7 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Completed | 2 | — | 8ac741e8b0 | 27.8 s | 0.0 s | dotnet build succeeded; dotnet test: 105 passed, 0 failed, 0 skipped. Review verdict: PASS. |
| release | Completed | 1 | — | d6409c1205 | 10.1 s | 0.0 s | Agent JSON and file boundaries validated. |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 100% (1/1 runs completed) |
| Retry rate | 21.4% (3 retries / 14 stage attempts) |
| Rollback frequency | 1 per run (1 rollbacks) |
| MTTR | 44.0 s (2 recovered, 0 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 4 min 55 s |
| Machine time | 4 min 55 s |
| Human wait time | 0.1 s |

## Across all runs in runs/ (21 runs)

| Metric | Value |
| --- | --- |
| Success rate | 66.7% (14/21 runs completed) |
| Retry rate | 14.5% (25 retries / 172 stage attempts) |
| Rollback frequency | 0.86 per run (18 rollbacks) |
| MTTR | 47.5 s (19 recovered, 1 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 59 min 53 s |
| Machine time | 44 min 42 s |
| Human wait time | 15 min 10 s |

## Decision lineage

```text
Requirement: Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters be…
└─ [requirements] Specify the smallest testable URL-normalization change for link creat… · Requirements · a0454efc · commit 31966f0
   └─ [design] Normalize the destination in the application layer before constructin… · Architect · d8e3d6a5 · commit bd0096a
      ├─ [docs] Added user and operator documentation for URL destination handling. · Docs · 5e64981a · commit 60459b3 · superseded by re-plan
      ├─ [tests] Added integration tests for URL normalization on creation, persisted … · Tester · 6dc7a1e7 · commit 8e5f5c1 · superseded by re-plan
      ├─ [implement] Added a focused URL normalizer and connected it to link creation so n… · Coder · 682a534b · commit d8ed1ba · superseded by re-plan
      ├─ [implement] Fixed default-port removal in URL normalization while preserving non-… · Coder · 011d2b4a · commit a80086c
      │  └─ [validate] The URL normalizer is called in the link-creation flow before persist… · Reviewer · 10798ef0 · commit 8ac741e
      │     └─ [release] [READY] Release readiness: READY, based on the supplied PASS review a… · Release · a388b04c · commit d6409c1
      ├─ [tests] Added focused unit and HTTP integration tests for URL normalization, … · Tester · 0836448a · commit c752005
      │  └─ [validate] 10798ef0 (shown above)
      └─ [docs] Updated the URL destination documentation to describe the current nor… · Docs · c2ecbc48 · commit 9e2f811
         └─ [validate] 10798ef0 (shown above)
```

## Artifacts

- requirements: SHA-256 `ae87884a6a06d0646a71109732cae82361b9adbcf30af26c6d8b7e9009720c33`
- design: SHA-256 `bb5dc4506714de436ff1374899400c851c4e41783e864b7ae4f9f50b7039ca8b`
- implement: SHA-256 `cee9962a2c7299cbb7104e32eca2efe1681397101ff09c729cfa1b8529d4dba0`
- tests: SHA-256 `aa0d621c2591620d3d4c4b677d9db451b2d26fbcdd5e0f1eeba93d585b74e9b3`
- docs: SHA-256 `20335520f2d931210e0f0711a26057991a87c85d003c7ffecde4990e5c1f2cc2`
- validate: SHA-256 `a4a1397e3e5e7d739742e23e29f63b7560a65de762eed2623d00b8e7c06fcad7`
- release: SHA-256 `80861edcc5e0565f52130db9bcd4a3141191cc93dfba8573778d8487cde95885`

Reported model tokens: 198446 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
