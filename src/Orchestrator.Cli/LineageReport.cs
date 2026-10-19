using System.Text;
using Orchestrator.Core;

namespace Orchestrator.Cli;

// Renders decision lineage as a tree from the requirement down to the commits that landed each decision.
// A decision with several parents (a join such as validate) is expanded once and referenced afterwards.
public static class LineageReport
{
    public static string Render(RunContext context)
    {
        var known = context.Decisions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var children = context.Decisions.SelectMany(d => d.ParentDecisionIds.Select(p => (Parent: p, Child: d)))
            .ToLookup(x => x.Parent, x => x.Child, StringComparer.Ordinal);
        var roots = context.Decisions.Where(d => !d.ParentDecisionIds.Any(known.Contains)).OrderBy(d => d.AtUtc).ToArray();
        var text = new StringBuilder($"Requirement: {OneLine(context.Requirement, 120)}\n");
        var shown = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < roots.Length; i++) Write(roots[i], "", i == roots.Length - 1);
        return text.ToString();

        void Write(Decision decision, string indent, bool last)
        {
            text.Append(indent).Append(last ? "└─ " : "├─ ");
            if (!shown.Add(decision.Id))
            {
                text.Append($"[{decision.StageId}] {Short(decision.Id)} (shown above)\n");
                return;
            }
            var commit = context.DecisionCommits.TryGetValue(decision.Id, out var sha) ? $" · commit {sha[..7]}" : "";
            var superseded = context.SupersededDecisions.Contains(decision.Id) ? " · superseded by re-plan" : "";
            text.Append($"[{decision.StageId}] {OneLine(decision.Summary, 70)} · {decision.MadeBy} · {Short(decision.Id)}{commit}{superseded}\n");
            var next = children[decision.Id].OrderBy(d => d.AtUtc).ToArray();
            for (var i = 0; i < next.Length; i++) Write(next[i], indent + (last ? "   " : "│  "), i == next.Length - 1);
        }
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;
    private static string OneLine(string value, int max)
    {
        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }
}
