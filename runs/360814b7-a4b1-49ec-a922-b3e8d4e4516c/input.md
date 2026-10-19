# Input

Scenario: brownfield

Started: 2026-10-18T04:56:09.3184300Z

## Requirement

Add an optional per-link click limit to the existing URL shortener: POST /api/v1/links accepts maxClicks (1-1,000,000); once a link has recorded that many clicks its redirect returns 410 Gone. Links without a limit must behave exactly as they do today. The first recorded implementation attempt contains a migration mistake, so the run shows a failed gate, a rollback to the last good commit and a retry.
