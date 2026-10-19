# ADR 0008: Grounded impact analysis and human-answered clarification

Status: Accepted for Phase 8.

**Impact analysis.** A model asked "which files are affected?" can invent files or miss callers. The
ImpactAnalyst therefore proposes only seed types and members. The host computes the impact from
Roslyn syntax trees of the code as it is now: declarations, references, minimal API routes
(including `MapGroup` prefixes) and EF table mappings. Unknown seeds are reported rather than
trusted. Syntax trees need no build and are fast; a semantic model (a full compilation) would
resolve overloads and namespaces and is the next step if name-based matching proves too coarse.

**Clarification.** Ambiguity is resolved by a person, not inferred. The Clarifier must offer 2–3
concrete interpretations per question. The answer sheet is a plain Markdown file a reviewer edits
in any editor. Answers are parsed strictly (option letters or "Other:") and recorded as Decisions,
so later artifacts trace back to the answer that justified them. Because the sheet is a reviewed
artifact, changing an answer reuses the existing re-planning mechanism instead of a special case.

**Replay variants.** Deterministic demos must still react to human choices. Recordings may hold
variants selected by substrings of the prompt. This keeps replay free and offline while showing
different downstream content for different answers.
