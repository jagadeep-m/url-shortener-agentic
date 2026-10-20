# Input

Scenario: brownfield

Started: 2026-10-20T14:41:06.1559180Z

## Requirement

Add an optional per-link click limit: POST /api/v1/links accepts maxClicks (1 to 1,000,000); once a link has recorded that many clicks, its redirect returns 410 Gone. Links without a limit must behave exactly as they do today.
