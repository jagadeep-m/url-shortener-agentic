# ADR 0004: Replay mode and a minimal live chat client

Status: Accepted for Phase 4.

Agents depend only on `IChatClient`. Demos and tests use `ReplayChatClient`, which returns
recorded JSON from `scenarios/recordings/<scenario>/<stage>.json`. Replay is deterministic,
free, needs no key or network, and lets recordings inject failures on purpose. A missing
recording is an error, never a silent fallback to a live model.

For live runs we implement a small OpenAI-compatible HTTPS adapter instead of adding a
provider SDK, keeping the package list to the agreed stack. It sends text messages only,
requests JSON output, disables redirects, and reports token usage for the run budget.
Agents get no tools: all file writes go through FileChangeService and its policies.

Trade-off: provider-specific features (tool calling, streaming deltas, prompt caching) are
not available. A provider package can replace the adapter behind the same interface.
