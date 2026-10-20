# Run 67cdd834-be59-4b57-b261-444f280e535f

Status: Failed

Scenario: greenfield

Mode: Live (model gpt-6-luna)

Trace: 8b4f93b26e5cc9a58a59b29b2f881913

Stop reason: No deterministic template exists for the Tester role.

Branch: agent/run-67cdd834-be59-4b57-b261-444f280e535f (last good commit 4545af9815c64c523acbe5284e8d99d3dd2b1321)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | 06a19cc0dd | 16.0 s | 22.0 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | 6e2320613e | 26.0 s | 40.2 s | Agent JSON and file boundaries validated. |
| implement | Completed | 1 | — | 4545af9815 | 45.1 s | 0.0 s | dotnet build succeeded; dotnet test: 84 passed, 0 failed, 0 skipped. |
| tests | Failed | 5 | Template | — | 3 min 0 s | 0.0 s | No deterministic template exists for the Tester role. |
| docs | Completed | 1 | — | 0821df25a2 | 21.9 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Pending | 0 | — | — | — | — | — |
| release | Pending | 0 | — | — | — | — | — |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 0% (0/1 runs completed) |
| Retry rate | 22.2% (2 retries / 9 stage attempts) |
| Rollback frequency | 4 per run (4 rollbacks) |
| MTTR | — (0 recovered, 1 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 4 min 50 s |
| Machine time | 3 min 48 s |
| Human wait time | 1 min 2 s |

## Across all runs in runs/ (8 runs)

| Metric | Value |
| --- | --- |
| Success rate | 75% (6/8 runs completed) |
| Retry rate | 17.9% (14 retries / 78 stage attempts) |
| Rollback frequency | 1.88 per run (15 rollbacks) |
| MTTR | 1 min 9 s (9 recovered, 2 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 58 min 26 s |
| Machine time | 27 min 34 s |
| Human wait time | 30 min 51 s |

## Decision lineage

```text
Requirement: Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters be…
└─ [requirements] Specify URL normalization before a submitted destination is saved by … · Requirements · 3076eee3 · commit 06a19cc
   └─ [design] Normalize valid destination URLs in the application layer before link… · Architect · a55f3624 · commit 6e23206
      ├─ [docs] Documented URL normalization for link creation, including API usage, … · Docs · 9ecb2de9 · commit 0821df2
      └─ [implement] Added an application-layer URL normalizer that preserves unaffected U… · Coder · eaec5f80 · commit 4545af9
```

## Artifacts

- requirements: SHA-256 `349bb8b8bfb5372b67db10a762f6f712f2371f2618ba627922c0395dfdf2e7e0`
- design: SHA-256 `1a284595e922084ce7f4132b54306ed65e8c479fcd58b3043588c1b554f1d684`
- implement: SHA-256 `bd3e9d60820b5cf37957e66f06c8ad9676ac290278832140fcf7b1f625339ec9`
- docs: SHA-256 `5f5a3ad6e774fbf2a3485d305f118000d3b34472cb5e50630a98a6791ef014a9`

Reported model tokens: 53438 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
