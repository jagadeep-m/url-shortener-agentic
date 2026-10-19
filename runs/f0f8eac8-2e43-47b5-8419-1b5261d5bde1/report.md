# Run f0f8eac8-2e43-47b5-8419-1b5261d5bde1

Status: Completed

Scenario: greenfield

Mode: Replay (offline)

Trace: 8767421fb90083131c95f24d0ca6b851

Branch: agent/run-f0f8eac8-2e43-47b5-8419-1b5261d5bde1 (last good commit ef148ce0d43207317a56abc4e40ba04f301c0aae)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | 76e1db2d01 | 0.0 s | 0.0 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | e3d2e33fb8 | 0.0 s | 0.0 s | Agent JSON and file boundaries validated. |
| implement | Completed | 1 | — | 1881627831 | 7.4 s | 0.0 s | dotnet build succeeded; dotnet test: 84 passed, 0 failed, 0 skipped. |
| tests | Completed | 2 | — | 889524c17f | 18.8 s | 0.0 s | dotnet build succeeded; dotnet test: 84 passed, 0 failed, 0 skipped. |
| docs | Completed | 1 | — | 64581ca73b | 12.8 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Completed | 1 | — | bf47ac936c | 5.0 s | 0.0 s | dotnet build succeeded; dotnet test: 84 passed, 0 failed, 0 skipped. |
| release | Completed | 1 | — | ef148ce0d4 | 0.0 s | 0.0 s | Agent JSON and file boundaries validated. |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 100% (1/1 runs completed) |
| Retry rate | 12.5% (1 retries / 8 stage attempts) |
| Rollback frequency | 1 per run (1 rollbacks) |
| MTTR | 8.1 s (1 recovered, 0 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 26.0 s |
| Machine time | 25.9 s |
| Human wait time | 0.0 s |

## Across all runs in runs/ (1 runs)

| Metric | Value |
| --- | --- |
| Success rate | 100% (1/1 runs completed) |
| Retry rate | 12.5% (1 retries / 8 stage attempts) |
| Rollback frequency | 1 per run (1 rollbacks) |
| MTTR | 8.1 s (1 recovered, 0 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 26.0 s |
| Machine time | 25.9 s |
| Human wait time | 0.0 s |

## Decision lineage

```text
Requirement: Build the URL shortener from AGENTS.md: .NET 8, Clean Architecture, SQLite, seven-character random Base62 codes, valida…
└─ [requirements] URL shortener acceptance criteria · Requirements · e7ecdb20 · commit 76e1db2
   └─ [design] Approve the Clean Architecture design · Architect · 87fc54bd · commit e3d2e33
      ├─ [implement] Emit the approved Base62 generator · Coder · 036a5147 · commit 1881627
      │  └─ [validate] Review the implementation, tests and documentation · Reviewer · e5b8885a · commit bf47ac9
      │     └─ [release] Ready for human release-readiness review · Release · 7e3f6088 · commit ef148ce
      ├─ [docs] Document the replay workflow · Docs · f5959c95 · commit 64581ca
      │  └─ [validate] e5b8885a (shown above)
      └─ [tests] Emit domain acceptance tests · Tester · 3d67235f · commit 889524c
         └─ [validate] e5b8885a (shown above)
```

## Artifacts

- requirements: SHA-256 `c95a636fa797621388046273255e4d2d206a6c160a4505d6d6fc68fe650a441b`
- design: SHA-256 `87036e244904d6fcce957e3c907708c7c42ad9df5335658104d6f77568347916`
- implement: SHA-256 `87a1cedde7cf943c80155ee5e206f2aa36c61c0ba5d210a223b85957941984c9`
- tests: SHA-256 `d9195017c4c494767aa2a8f72b668a7e8bad442b4145cf3a396278cbe55d6a0d`
- docs: SHA-256 `823cbe7a284eef17ef71f39b7a92c8e2c59fa70c3655c0dbed325968802ff488`
- validate: SHA-256 `a7c3b8a880cbdbb3ec7cd32ca6fb12634d0d50c5e8a9596d7309fd2c33afd78a`
- release: SHA-256 `5eb68659e0df7c7165116f852999798814f61e7f1c074de7fe28a0f860928e0e`

Reported model tokens: 0 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
