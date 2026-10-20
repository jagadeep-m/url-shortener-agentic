# Run 27227535-ea23-4774-ad39-3884a16ea23f

Status: Failed

Scenario: greenfield

Mode: Live (model gpt-6-luna)

Trace: f252fbdadeab4e432e15c2a66fbb660b

Stop reason: No deterministic template exists for the Tester role.

Branch: agent/run-27227535-ea23-4774-ad39-3884a16ea23f (last good commit 79f3f85b6fe4a76930bbd3fb7aafb9906ca29f86)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | aa57734ffb | 11.7 s | 1 min 47 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | 927a735bb2 | 29.8 s | 38.9 s | Agent JSON and file boundaries validated. |
| implement | Completed | 2 | — | a4dccf08a8 | 14.5 s | 0.0 s | Agent JSON and file boundaries validated. |
| tests | Failed | 5 | Template | — | 2 min 29 s | 0.0 s | No deterministic template exists for the Tester role. |
| docs | Completed | 1 | — | 79f3f85b6f | 36.1 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Pending | 0 | — | — | — | — | — |
| release | Pending | 0 | — | — | — | — | — |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 0% (0/1 runs completed) |
| Retry rate | 30% (3 retries / 10 stage attempts) |
| Rollback frequency | 4 per run (4 rollbacks) |
| MTTR | 9.9 s (1 recovered, 1 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 5 min 44 s |
| Machine time | 3 min 17 s |
| Human wait time | 2 min 26 s |

## Across all runs in runs/ (6 runs)

| Metric | Value |
| --- | --- |
| Success rate | 83.3% (5/6 runs completed) |
| Retry rate | 16.9% (10 retries / 59 stage attempts) |
| Rollback frequency | 1.33 per run (8 rollbacks) |
| MTTR | 1 min 4 s (8 recovered, 1 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 44 min 24 s |
| Machine time | 20 min 1 s |
| Human wait time | 24 min 23 s |

## Decision lineage

```text
Requirement: Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters be…
└─ [requirements] Specify the smallest testable change: normalize destination URLs befo… · Requirements · b3676306 · commit aa57734
   └─ [design] Normalize destination URLs in the application service before creating… · Architect · cd71d27a · commit 927a735
      ├─ [implement] Unable to implement the URL normalizer because no source files were p… · Coder · 793d2dbf · commit a4dccf0
      └─ [docs] Document URL normalization for link creation, including API usage, co… · Docs · f74b4966 · commit 79f3f85
```

## Artifacts

- requirements: SHA-256 `520e97952ce1c02995f92e01182f6fac8bb8d9a94c4112f4040da69cfe2f6f5c`
- design: SHA-256 `de04dd76050794e2596666bdb0575e1db07368c1f29172757270b955318b77a5`
- implement: SHA-256 `018ef5d099d0282130641a46412db0c870f74091fef84dbc558601ebd4ae5267`
- docs: SHA-256 `f6e4b840997a9a7acb50f669a3e97dfadeec4c9292428661a7ed22a85a67c9a4`

Reported model tokens: 33620 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
