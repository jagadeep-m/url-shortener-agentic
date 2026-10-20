# Run 7b077f47-c8e6-42ad-ac0f-3730aa723e32

Status: Completed

Scenario: greenfield

Mode: Live (model gpt-6-luna)

Trace: 9e6625cc6400d568bb74c1b51ef26ac2

Branch: agent/run-7b077f47-c8e6-42ad-ac0f-3730aa723e32 (last good commit 72df9569cfbf112c3000fe7d79e0ed2044e3478d)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | 01dfa7186a | 15.9 s | 0.0 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | 90285e7f59 | 43.3 s | 0.1 s | Agent JSON and file boundaries validated. |
| implement | Completed | 4 | — | bdabada753 | 2 min 34 s | 0.0 s | dotnet build succeeded; dotnet test: 92 passed, 0 failed, 0 skipped. |
| tests | Completed | 2 | — | 1e7572eedd | 58.1 s | 0.0 s | dotnet build succeeded; dotnet test: 96 passed, 0 failed, 0 skipped. |
| docs | Completed | 2 | — | bdd80a3c5c | 30.9 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Completed | 4 | — | 71fbbc7e11 | 1 min 37 s | 0.0 s | dotnet build succeeded; dotnet test: 96 passed, 0 failed, 0 skipped. Review verdict: PASS. |
| release | Completed | 1 | — | 72df9569cf | 9.4 s | 0.0 s | Agent JSON and file boundaries validated. |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 100% (1/1 runs completed) |
| Retry rate | 26.7% (4 retries / 15 stage attempts) |
| Rollback frequency | 2 per run (2 rollbacks) |
| MTTR | 2 min 17 s (2 recovered, 0 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 6 min 58 s |
| Machine time | 6 min 58 s |
| Human wait time | 0.1 s |

## Across all runs in runs/ (13 runs)

| Metric | Value |
| --- | --- |
| Success rate | 69.2% (9/13 runs completed) |
| Retry rate | 16.1% (18 retries / 112 stage attempts) |
| Rollback frequency | 1 per run (13 rollbacks) |
| MTTR | 1 min 0 s (13 recovered, 1 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 49 min 1 s |
| Machine time | 33 min 51 s |
| Human wait time | 15 min 10 s |

## Decision lineage

```text
Requirement: Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters be…
└─ [requirements] Specify normalization of destination URLs before they are saved by th… · Requirements · 8d8cac75 · commit 01dfa71
   └─ [design] Normalize validated destination URLs in the link-creation application… · Architect · d6b91e3b · commit 90285e7
      ├─ [docs] Added documentation describing current link-creation URL behavior and… · Docs · f5df5dd9 · commit f683f5c · superseded by re-plan
      ├─ [tests] Added unit and HTTP integration tests for URL normalization and failu… · Tester · 11b50b1d · commit 5862432 · superseded by re-plan
      ├─ [implement] Added URL normalization in the link-creation flow so newly saved dest… · Coder · eecd5f6e · commit 73d386a · superseded by re-plan
      ├─ [implement] Added percent-encoded UTM parameter-name detection while preserving o… · Coder · e668b192 · commit bdabada
      │  └─ [validate] The URL normalizer is called in the link-creation flow before persist… · Reviewer · bca42db3 · commit 71fbbc7
      │     └─ [release] [READY] Release readiness: READY. The reviewed artifacts report the r… · Release · 8c60d7e4 · commit 72df956
      ├─ [tests] Added focused unit and HTTP integration coverage for percent-encoded … · Tester · 8aefb178 · commit 1e7572e
      │  └─ [validate] bca42db3 (shown above)
      └─ [docs] Update the URL-normalization guide to match the current implementatio… · Docs · 72f1c4ec · commit bdd80a3
         └─ [validate] bca42db3 (shown above)
```

## Artifacts

- requirements: SHA-256 `13d673880b180f8b8797341b649519df07cd834feb2271af12196bbd7ce944da`
- design: SHA-256 `dc235b51cb022c4b78a59b6db3b54d9a59257efb26d0f6209bc3f03e0b8dd69c`
- implement: SHA-256 `209b3909b312098d6d71b48bedc1b9c1203f7c8e03dfc4602ee1400dd05ce401`
- tests: SHA-256 `c26b7f6f0a58541404f0b7be9ee0a39f9a992a854e527ac1c1e8342e80481d0a`
- docs: SHA-256 `393a9662304ccedddd903f8b1a8f96a0914dee409223d5b580f37b3b0cd01af8`
- validate: SHA-256 `7656b4cbbcea24ede59eff900f5c685499b32ccff379340ce46ecb8227204c5e`
- release: SHA-256 `dbcde72a54a6624c31eb6e79b5904cb6b0950e13afcc662fcf347fab8f502f26`

Reported model tokens: 308129 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
