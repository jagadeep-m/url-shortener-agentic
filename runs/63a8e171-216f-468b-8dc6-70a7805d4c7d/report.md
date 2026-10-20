# Run 63a8e171-216f-468b-8dc6-70a7805d4c7d

Status: Failed

Scenario: greenfield

Mode: Live (model gpt-6-luna)

Trace: 706909f69ecf12066232baeceed527da

Stop reason: No deterministic template exists for the Coder role.

Branch: agent/run-63a8e171-216f-468b-8dc6-70a7805d4c7d (last good commit baec7f2cdd49afc40ee8858f1adde0652d40e27e)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | 5ac9ae955c | 17.2 s | 0.0 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | 647c6fa708 | 39.0 s | 0.0 s | Agent JSON and file boundaries validated. |
| implement | Failed | 5 | Template | — | 3 min 38 s | 0.0 s | No deterministic template exists for the Coder role. |
| tests | Completed | 2 | — | baec7f2cdd | 1 min 43 s | 0.0 s | dotnet build succeeded and every existing test passes; 5 new test(s) in UrlNormalizationTests fail against the current code (red), so validation will decide. First failures: UrlShortener.IntegrationTests.UrlNormalizationTests.CreateReturnsAndPersistsNormalizedDestination(inputUrl: "HTTP://ExAmPlE.CoM:80/Path?Keep=a%2Fb&utm_source=a"···, expectedUrl: "http://example.com/Path?Keep=a%2Fb&MiXeD=one+two#F"···): System.ObjectDisposedException : Cannot access a closed Stream.; UrlShortener.IntegrationTests.UrlNormalizationTests.CreateReturnsAndPersistsNormalizedDestination(inputUrl: "HTTP://ExAmPlE.CoM:8080/custom?utm_source=ad&keep="···, expectedUrl: "http://example.com:8080/custom?keep=value"): System.ObjectDisposedException : Cannot access a closed Stream.; UrlShortener.IntegrationTests.UrlNormalizationTests.CreateReturnsAndPersistsNormalizedDestination(inputUrl: "HTTPS://ExAmPlE.CoM:443/secure?first=1&UtM_Medium="···, expectedUrl: "https://example.com/secure?first=1&second=x%20y"): System.ObjectDisposedException : Cannot access a closed Stream. |
| docs | Completed | 1 | — | 8c74559901 | 24.8 s | 0.0 s | Agent JSON and file boundaries validated. |
| validate | Pending | 0 | — | — | — | — | — |
| release | Pending | 0 | — | — | — | — | — |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 0% (0/1 runs completed) |
| Retry rate | 30% (3 retries / 10 stage attempts) |
| Rollback frequency | 5 per run (5 rollbacks) |
| MTTR | 35.2 s (1 recovered, 1 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 4 min 40 s |
| Machine time | 4 min 40 s |
| Human wait time | 0.1 s |

## Across all runs in runs/ (8 runs)

| Metric | Value |
| --- | --- |
| Success rate | 75% (6/8 runs completed) |
| Retry rate | 13.6% (9 retries / 66 stage attempts) |
| Rollback frequency | 1.13 per run (9 rollbacks) |
| MTTR | 35.3 s (7 recovered, 1 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 31 min 14 s |
| Machine time | 16 min 4 s |
| Human wait time | 15 min 9 s |

## Decision lineage

```text
Requirement: Add a URL normalizer that lowercases the scheme and host, removes default ports, and strips utm_ tracking parameters be…
└─ [requirements] Specify the smallest testable change: normalize each URL before savin… · Requirements · 2df0608d · commit 5ac9ae9
   └─ [design] Normalize the destination URL in the application layer immediately be… · Architect · de26c4de · commit 647c6fa
      ├─ [docs] Added documentation describing the requested URL-normalization policy… · Docs · 44312ed6 · commit 8c74559
      └─ [tests] Added integration coverage for URL normalization on link creation, re… · Tester · 7f1b71db · commit baec7f2
```

## Artifacts

- requirements: SHA-256 `eb250a1d060629ea5a31e3297db3948d87aba92f2fe91d2705da1c7590ab1b0e`
- design: SHA-256 `1157385127c72f5d7a3ae684b3749e4a3ec351ff2a8da7153de82873d16994ba`
- tests: SHA-256 `bd9bfbcd9453a7b6de49c55cbffb37f66b724db4f91db537e39157ee35fd2ee4`
- docs: SHA-256 `8b625169c0c25484fe7b85734a822971c18f3e63d14bffa29bb88bd7b9c6a737`

Reported model tokens: 152482 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
