# Sample runs

One completed `--replay` run of each scenario, produced by the CLI with real git branches and the
real `dotnet build` / `dotnet test` gates (in a scratch clone, so this repository's product code is
unchanged). Each folder holds `input.md`, `plan.json`, `audit.jsonl`, `report.md` and `diff.patch`.

| Scenario | Run | What it shows |
| --- | --- | --- |
| Greenfield | [f0f8eac8…](f0f8eac8-2e43-47b5-8419-1b5261d5bde1/report.md) | Requirements → design → implement ‖ tests ‖ docs → validate → release. The Tester's first attempt fails the real test gate, is rolled back and retried (retry rate 12.5%, MTTR 8.1 s). |
| Brownfield | [360814b7…](360814b7-a4b1-49ec-a922-b3e8d4e4516c/report.md) | Per-link click limit. Roslyn-grounded impact analysis ([impact.json](360814b7-a4b1-49ec-a922-b3e8d4e4516c/impact.json)): 12 files, 3 endpoints, the `ShortLinks` table, affected tests. The EF migration needs approval; the first attempt's NOT NULL column fails 15 tests, 8 files are rolled back to the last good commit, and the retry lands. [diff.patch](360814b7-a4b1-49ec-a922-b3e8d4e4516c/diff.patch) is the whole change. |
| Ambiguous | [43cdab25…](43cdab25-adf9-435f-a94e-94a289893d70/report.md) | "Make the links more secure and give marketing better analytics." The Clarifier writes [open-questions.md](43cdab25-adf9-435f-a94e-94a289893d70/open-questions.md) with 2–3 interpretations each; answers become decisions; changing Q1 from A to C at the design prompt re-plans requirements and design only (see the lineage in report.md). |

The `runs/orchestrator.db` checkpoint store and per-attempt build/test logs are not committed.
