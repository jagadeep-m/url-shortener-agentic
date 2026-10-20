# Run 2a2cd0af-dc93-462c-920d-9492b49082fd

Status: Completed

Scenario: feature

Mode: Live (model gpt-6-luna)

Trace: 0f3a0224158d0927aebdfca534fa6575

Branch: agent/run-2a2cd0af-dc93-462c-920d-9492b49082fd (last good commit a7f0b1473e45287332f6c0d577bcf14d1cf81287)

| Stage | Status | Attempts | Fallback | Commit | Active | Human wait | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| requirements | Completed | 1 | — | afda4b4ed8 | 9.4 s | 34.9 s | Agent JSON and file boundaries validated. |
| impact | Completed | 1 | — | 328274cc21 | 7.5 s | 49.6 s | Agent JSON and file boundaries validated. |
| design | Completed | 1 | — | 73e2d7c51f | 42.8 s | 51.6 s | Agent JSON and file boundaries validated. |
| implement | Completed | 2 | — | 953d86c30a | 3 min 9 s | 2 min 59 s | dotnet build succeeded; dotnet test: 84 passed, 0 failed, 0 skipped. |
| docs | Completed | 2 | — | 2de62dc880 | 37.6 s | 0.0 s | Agent JSON and file boundaries validated. |
| tests | Completed | 2 | — | 31a96f6fdc | 1 min 6 s | 0.0 s | dotnet build succeeded; dotnet test: 86 passed, 0 failed, 0 skipped. |
| validate | Completed | 1 | — | 4312b432c2 | 44.0 s | 0.0 s | dotnet build succeeded; dotnet test: 86 passed, 0 failed, 0 skipped. |
| release | Completed | 1 | — | a7f0b1473e | 9.2 s | 1 min 31 s | Agent JSON and file boundaries validated. |

## Reliability metrics (this run)

| Metric | Value |
| --- | --- |
| Success rate | 100% (1/1 runs completed) |
| Retry rate | 27.3% (3 retries / 11 stage attempts) |
| Rollback frequency | 2 per run (2 rollbacks) |
| MTTR | 1 min 42 s (3 recovered, 0 unrecovered) |
| Re-plans | 0 |
| End-to-end latency | 12 min 59 s |
| Machine time | 6 min 13 s |
| Human wait time | 6 min 46 s |

## Across all runs in runs/ (5 runs)

| Metric | Value |
| --- | --- |
| Success rate | 100% (5/5 runs completed) |
| Retry rate | 14.3% (7 retries / 49 stage attempts) |
| Rollback frequency | 0.8 per run (4 rollbacks) |
| MTTR | 1 min 12 s (7 recovered, 0 unrecovered) |
| Re-plans | 2 |
| End-to-end latency | 38 min 40 s |
| Machine time | 16 min 43 s |
| Human wait time | 21 min 56 s |

## Decision lineage

```text
Requirement: Add an endpoint that returns a QR code for a short link
└─ [requirements] Specify a minimal QR-code endpoint for existing short links. · Requirements · 101e5dc8 · commit afda4b4
   └─ [impact] Add the QR endpoint in LinkEndpoints.MapLinkEndpoints, reusing existi… · ImpactAnalyst · 28c5cee2 · commit 328274c
      └─ [design] Add GET /api/v1/links/{code}/qr, returning a standards-compliant SVG … · Architect · 72b8f8a3 · commit 73e2d7c
         ├─ [docs] Documented the short-link QR code endpoint, including its API contrac… · Docs · bc8baf95 · commit 2de62dc
         │  └─ [validate] The endpoint is wired, documented, and covered by basic HTTP integrat… · Reviewer · b9faf850 · commit 4312b43
         │     └─ [release] NO-GO for release. Do not publish or deploy the QR endpoint until QR … · Release · 644be694 · commit a7f0b14
         └─ [implement] Added GET /api/v1/links/{code}/qr, returning a generated SVG QR code … · Coder · c35cb9d8 · commit 953d86c
            ├─ [tests] Added integration tests for successful QR-code responses and unknown-… · Tester · 55f0ae6d · commit 31a96f6
            │  └─ [validate] b9faf850 (shown above)
            └─ [validate] b9faf850 (shown above)
```

## Artifacts

- requirements: SHA-256 `4116b8ab7a5b5979f6fe05465d8f6427db13e7fa4ba30e4b8e81f4e421de8ca7`
- impact: SHA-256 `bd1961bb21c775493d55a5f87892c2dab32e1be89d4bcc4b67a973945e9d93f0`
- design: SHA-256 `fc0615b2c36043704ee85df34ba9c16e2f014d5eb3f22928aa4d041175c1fc09`
- implement: SHA-256 `622849079bcd4eba8cbaf67d92b917c1b8abc45027abd54ed52399df5e978a84`
- docs: SHA-256 `068f782d8796cb0f19ac86c94eaa87f5d26ae1ccc49a48a8f22bfa1a7df02c79`
- tests: SHA-256 `b8076f42da4f4216c805dde5fd962f3759b69434180d006f4e784dcb14cbe021`
- validate: SHA-256 `674ddb9dc78a99e6ac6ecd070a8dc26c3df42816e7ce1403a2f0c179e9675f6d`
- release: SHA-256 `3e5d20fab78505e9397a7668535d4c50eacb8f4e164d3fd83d74e5edd5e002f9`

Reported model tokens: 186589 (replay has no model usage).

This report does not claim deployment; merging the run branch is a human decision.
