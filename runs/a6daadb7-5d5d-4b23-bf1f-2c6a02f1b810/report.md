# Run a6daadb7-5d5d-4b23-bf1f-2c6a02f1b810

Status: Failed

Scenario: greenfield

Mode: Live (model gpt-6-luna)

Trace: 2b00df69b1c439771634425e570a0134

Stop reason: The review still failed after 1 rework(s): — preserve fragments when '?' occurs after '#', add a regression test for that case, and add a create/save-path test that verifies the normalized destination is persisted and exposed.

Branch: agent/run-a6daadb7-5d5d-4b23-bf1f-2c6a02f1b810 (last good commit c4fc37c766d2ad56b0f9128ae7ee8573c6185d52)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | 5c937491ec | 12.3 s | 6 min 27 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | a64789f454 | 27.2 s | 37.6 s | Agent JSON and file boundaries validated. |
| implement | Completed | 3 | — | a0b8df3a39 | 1 min 57 s | 0.0 s | dotnet build succeeded; dotnet test: 94 passed, 0 failed, 0 skipped. |
| tests | Completed | 4 | — | 292713bea9 | 2 min 30 s | 0.0 s | dotnet build succeeded; dotnet test: 103 passed, 0 failed, 0 skipped. |
| docs | Completed | 2 | — | c4fc37c766 | 51.6 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Failed | 2 | — | — | 31.5 s | 0.0 s | The review still failed after 1 rework(s): — preserve fragments when '?' occurs after '#', add a regression test for that case, and add a create/save-path test that verifies the normalized destination is persisted and exposed. |
| release | Pending | 0 | — | — | — | — | — |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 0% (0/1 runs completed) |
| Retry rate | 23.1% (3 retries / 13 stage attempts) |
| Rollback frequency | 3 per run (3 rollbacks) |
| MTTR | 1 min 8 s (2 recovered, 0 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 11 min 28 s |
| Machine time | 4 min 23 s |
| Human wait time | 7 min 5 s |

## Across all runs in runs/ (10 runs)

| Metric | Value |
| --- | --- |
| Success rate | 60% (6/10 runs completed) |
| Retry rate | 17.7% (17 retries / 96 stage attempts) |
| Rollback frequency | 1.8 per run (18 rollbacks) |
| MTTR | 1 min 9 s (11 recovered, 3 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 72 min 14 s |
| Machine time | 33 min 36 s |
| Human wait time | 38 min 37 s |

## Decision lineage

```text
Requirement: Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters be…
└─ [requirements] Specify minimal acceptance requirements for normalizing URLs before t… · Requirements · 8f07d3bb · commit 5c93749
   └─ [design] Normalize destination URLs in the Application layer before constructi… · Architect · 07663656 · commit a64789f
      ├─ [docs] Documented URL normalization for link creation, including API example… · Docs · db7c9ae9 · commit 8d210ba · superseded by re-plan
      ├─ [implement] Added an Application-layer URL normalizer and connected it to link cr… · Coder · a0aeadd5 · commit 21d9e42 · superseded by re-plan
      ├─ [tests] Added unit and HTTP integration tests for URL normalization through l… · Tester · 8f0cd6ca · commit 664b07a · superseded by re-plan
      ├─ [implement] Fixed URL normalization so paths and fragments are preserved when UTM… · Coder · 46465b62 · commit a0b8df3
      ├─ [tests] Added focused unit tests for URL normalization, including path and fr… · Tester · 83ee035a · commit 292713b
      └─ [docs] Documented URL normalization behavior, API usage, configuration, and … · Docs · 43bafb42 · commit c4fc37c
```

## Artifacts

- requirements: SHA-256 `f2f0aa7a8888842b07a667908c112748620592b4ec0b7b199f8e6256ccdde130`
- design: SHA-256 `6b824204a27d5805cb8ed999acda724c61aa033bc2f605fa68aa0e036db50aed`
- implement: SHA-256 `1bbd1d782a1171150f524fe6a6d31c9da6309f6f204a1a541ad3c61d4eac04c9`
- tests: SHA-256 `89cbb2e0c1610d0fd67a5ae25e13aca7b7581797f9f17834839ac020d19ab104`
- docs: SHA-256 `fe532376e27f9c1c962180036d76d965275a2a353d29b2537742aa0da054b80c`

Reported model tokens: 260573 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
