# ADR 0006: Policy guardrails on the agent write path

Status: Accepted for Phase 6.

Agents have no tools; their only side effect is a change set handed to `FileChangeService`.
Enforcing policy there covers every agent with one checkpoint. The role boundary is checked
first, then a global path allowlist, so a misconfigured role cannot reach `.github/` or the
orchestrator.

Policies judge a real unified diff rather than raw file contents. Size limits then measure what
a reviewer would read, the secret scan looks only at added lines, and the migration policy can
tell a new migration from an edited one (by the `/dev/null` header). The same diff is shown in
approval prompts.

`IPolicy` keeps its agreed signature. Reasons come from an additive `IExplainablePolicy`
interface, so audit lines and prompts explain verdicts without changing the core contract.

Denials stop the stage instead of retrying. Re-prompting after a secret or a forbidden path
invites the model to evade the rule; a human should look first. Uncertain cases (generic
secret-looking strings, an unavailable vulnerability feed, large diffs, schema changes, new
packages) ask for approval rather than allow or deny silently.

The vulnerability check runs `dotnet list package --vulnerable` against a throwaway probe
project holding only the proposed packages. It needs no change to the real workspace before
approval, and it is skipped when no packages are added, which keeps replay offline.
