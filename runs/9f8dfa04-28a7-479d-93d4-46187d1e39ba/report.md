# Run 9f8dfa04-28a7-479d-93d4-46187d1e39ba

Status: Completed

Scenario: greenfield

Mode: Live (model gpt-6-luna)

Trace: 8729c263db7beacae8cb80336981c14a

Branch: agent/run-9f8dfa04-28a7-479d-93d4-46187d1e39ba (last good commit 885a3525034a1fa90f1a0e4e9ef51a28808da7f1)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | fdbb78999b | 12.3 s | 0.0 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | ee8f48db58 | 58.1 s | 0.0 s | Agent JSON and file boundaries validated. |
| implement | Completed | 3 | — | b125133f49 | 1 min 33 s | 0.0 s | dotnet build succeeded; dotnet test: 102 passed, 0 failed, 0 skipped. |
| tests | Completed | 2 | — | 448d8f66af | 1 min 46 s | 0.0 s | dotnet build succeeded; dotnet test: 106 passed, 0 failed, 0 skipped. |
| docs | Completed | 2 | — | e795b8b8bd | 42.3 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Completed | 3 | — | c2b0a7fd49 | 1 min 49 s | 0.0 s | dotnet build succeeded; dotnet test: 106 passed, 0 failed, 0 skipped. Review verdict: PASS. |
| release | Completed | 1 | — | 885a352503 | 13.1 s | 0.0 s | Agent JSON and file boundaries validated. |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 100% (1/1 runs completed) |
| Retry rate | 15.4% (2 retries / 13 stage attempts) |
| Rollback frequency | 1 per run (1 rollbacks) |
| MTTR | 1 min 51 s (2 recovered, 0 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 6 min 40 s |
| Machine time | 6 min 40 s |
| Human wait time | 0.1 s |

## Across all runs in runs/ (5 runs)

| Metric | Value |
| --- | --- |
| Success rate | 100% (5/5 runs completed) |
| Retry rate | 11.8% (6 retries / 51 stage attempts) |
| Rollback frequency | 0.6 per run (3 rollbacks) |
| MTTR | 1 min 10 s (6 recovered, 0 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 32 min 21 s |
| Machine time | 17 min 11 s |
| Human wait time | 15 min 9 s |

## Decision lineage

```text
Requirement: Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters be…
└─ [requirements] Define minimal, testable requirements for normalizing destination URL… · Requirements · e0234501 · commit fdbb789
   └─ [design] Normalize valid HTTP(S) destination URLs in the application service i… · Architect · 5df8105e · commit ee8f48d
      ├─ [docs] Added documentation describing current URL handling for link creation. · Docs · ae525d0e · commit c645f2f · superseded by re-plan
      ├─ [implement] Normalize the validated destination immediately before creating a new… · Coder · 04aeb152 · commit b592c77 · superseded by re-plan
      ├─ [tests] Added unit and HTTP integration tests for URL normalization, includin… · Tester · c429be68 · commit c55ea72 · superseded by re-plan
      ├─ [implement] Fixed UTM parameter removal when stripping a default port shifts the … · Coder · d72b590a · commit b125133
      │  └─ [validate] The requested URL normalization is implemented on the link-creation p… · Reviewer · b4366644 · commit c2b0a7f
      │     └─ [release] [READY] Release readiness: READY based on the supplied PASS review an… · Release · 77ad872d · commit 885a352
      ├─ [tests] Added API regression coverage for UTM removal when default ports are … · Tester · 55c7a882 · commit 448d8f6
      │  └─ [validate] b4366644 (shown above)
      └─ [docs] Documented destination URL normalization in the existing feature docu… · Docs · 1e9e2122 · commit e795b8b
         └─ [validate] b4366644 (shown above)
```

## Artifacts

- requirements: SHA-256 `4abc8103b02bbadad08a4194edc650bcb691782c2d144169c4e3a6f6b31a5e29`
- design: SHA-256 `c7d53b11e8c8378ae9ff79288f3630cd38b0be9e196f32f30162d06b9adf6eec`
- implement: SHA-256 `9cef3244b23caa5c80e91d0ca5ccb7414a6f6a1fa1dc11ad2148e3d55cbb9cd1`
- tests: SHA-256 `8317c65e17113307ba4beeacf050efd1362cb3b682b7d48936d73f45f8fd5600`
- docs: SHA-256 `f829d3bcff0d0f579a0f81fa71c30c92f846471b8437099f88b2b27ed66a8e67`
- validate: SHA-256 `a2f9d37e2ff17948aadda29b16219665b22f1193015a9b746551472f6aef6337`
- release: SHA-256 `d2634271b1eb8c914a1117a3feb98ef18fde58e4f1e31d05d62e38f107953579`

Reported model tokens: 203131 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
