# ADR 0011: One correction for a write in another role's folders

Status: Accepted. Amends ADR 0006.

**Decision.** When an agent returns a file outside its own role's folders (for example the Coder
writing `tests/`), the stage gets one retry with the denial as feedback: "Return only files under
src/UrlShortener.*; other roles write the rest." A second such denial in the same stage stops it.
Every other denial still stops the stage at once: the path allowlist (anything outside the agent
paths, such as `.github/` or the orchestrator), the secret scan, the package allowlist and
vulnerability check, and human rejections.

**Context.** A live run stopped because the approved design listed test files among the
implementation tasks and the Coder followed it. The role boundary held, but stopping a run
over a misfiled file costs a whole run, while the agent can fix it by leaving those files out.
The Architect and Coder prompts now also assign each folder to one role, so this should be rare.

**Why this is safe.** The check runs before anything is written, so there is nothing to undo.
The other roles' folders are writable by agents anyway, so this is a routing mistake, not an
attempt to reach a protected path. Re-prompting after a secret or a forbidden path could still
teach the model to evade the rule, so those denials keep ADR 0006's rule. The retry is bounded
(`StageExecutor.MaxBoundaryCorrections = 1`, counted in `StageState.BoundaryCorrections`), and
both the denial and the retry are in the audit log (`policy_denied`, then `retry_scheduled`).
