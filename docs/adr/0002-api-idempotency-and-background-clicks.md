# ADR 0002: Durable idempotency and bounded background analytics

Status: Accepted for Phase 2.

POST retries must not create additional links, including across concurrent requests or
process restarts. Persist a unique idempotency key, request fingerprint and link reference
in SQLite. Insert key and link atomically. Repeated matching requests replay creation;
a different fingerprint returns 409. Retain keys indefinitely in this prototype, with
future retention and authenticated key scoping called out as limitations.

Redirect latency must not include the analytics write. Enqueue into a bounded Channel
and process with a scoped repository in a BackgroundService. Use the existing atomic
counter/event transaction. Reject queue overflow with 503; bound storage retries to
three and drain on graceful shutdown. This deliberately trades durable event delivery
for the required single-process prototype stack. Record unrecoverable failures in logs.

Use a bounded memory cache and lock stripes so a racing lookup cannot repopulate an
active entry after a completed delete. Check expiry on every redirect and disable browser
caching of redirects. Multi-instance/external-writer coherence is outside this phase.

Swagger UI is supplied by Swashbuckle.AspNetCore 9.0.6 (supports net8.0 and OpenAPI 1.x).
The browser check exposed that older UI bundles rejected OpenAPI 3.0.4 despite serving
valid JSON; the compatible version is pinned. EF design tooling stays private to
Infrastructure, and no extra storage or distributed infrastructure is introduced.
