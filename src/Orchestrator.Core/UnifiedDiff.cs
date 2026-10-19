namespace Orchestrator.Core;

public sealed record DiffLine(string Path, int LineNumber, string Text);

// Line-based unified diff (3 lines of context) so policies and reviewers see exactly what changes.
public static class UnifiedDiff
{
    private const long MaxCells = 4_000_000;
    private enum Op { Keep, Remove, Add }
    private readonly record struct Edit(Op Op, string Line, int OldPos, int NewPos);

    public static (string Text, int Added, int Removed) Create(string path, string? oldText, string newText, int context = 3)
    {
        var edits = Edits(Lines(oldText), Lines(newText));
        var changes = edits.Select((e, i) => (e, i)).Where(x => x.e.Op != Op.Keep).Select(x => x.i).ToArray();
        if (changes.Length == 0) return ("", 0, 0);
        var text = new System.Text.StringBuilder();
        text.Append(oldText is null ? "--- /dev/null\n" : $"--- a/{path}\n").Append($"+++ b/{path}\n");
        for (var c = 0; c < changes.Length;)
        {
            var start = Math.Max(0, changes[c] - context);
            var last = changes[c];
            while (c + 1 < changes.Length && changes[c + 1] - last <= 2 * context) last = changes[++c];
            c++;
            var end = Math.Min(edits.Count - 1, last + context);
            var hunk = edits.Skip(start).Take(end - start + 1).ToArray();
            int oldCount = hunk.Count(e => e.Op != Op.Add), newCount = hunk.Count(e => e.Op != Op.Remove);
            text.Append($"@@ -{(oldCount == 0 ? hunk[0].OldPos : hunk[0].OldPos + 1)},{oldCount} +{(newCount == 0 ? hunk[0].NewPos : hunk[0].NewPos + 1)},{newCount} @@\n");
            foreach (var e in hunk) text.Append(e.Op switch { Op.Add => '+', Op.Remove => '-', _ => ' ' }).Append(e.Line).Append('\n');
        }
        return (text.ToString(), edits.Count(e => e.Op == Op.Add), edits.Count(e => e.Op == Op.Remove));
    }

    // Added lines with their new line numbers, for content scanners.
    public static IEnumerable<DiffLine> AddedLines(string diff)
    {
        string? path = null;
        var line = 0;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.StartsWith("+++ b/", StringComparison.Ordinal)) { path = raw[6..]; continue; }
            if (raw.StartsWith("--- ", StringComparison.Ordinal)) continue;
            if (raw.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var plus = raw.IndexOf('+');
                line = int.Parse(raw[(plus + 1)..raw.IndexOf(',', plus)]) - 1;
                continue;
            }
            if (path is null || raw.Length == 0) continue;
            if (raw[0] == '+') yield return new(path, ++line, raw[1..]);
            else if (raw[0] == ' ') line++;
        }
    }

    // Files the diff modifies that already existed (their old side is not /dev/null).
    public static IEnumerable<string> ModifiedExistingFiles(string diff) =>
        diff.Split('\n').Where(l => l.StartsWith("--- a/", StringComparison.Ordinal)).Select(l => l[6..]);

    public static int ChangedLineCount(string diff) => diff.Split('\n')
        .Count(l => l.Length > 0 && (l[0] == '+' || l[0] == '-') && !l.StartsWith("+++ ", StringComparison.Ordinal) && !l.StartsWith("--- ", StringComparison.Ordinal));

    private static string[] Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static List<Edit> Edits(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var edits = new List<Edit>(a.Length + b.Length);
        int i = 0, j = 0;
        void Keep() { edits.Add(new(Op.Keep, a[i], i, j)); i++; j++; }
        void Remove() { edits.Add(new(Op.Remove, a[i], i, j)); i++; }
        void Add() { edits.Add(new(Op.Add, b[j], i, j)); j++; }
        while (i < prefix) Keep();
        int aEnd = a.Length - suffix, bEnd = b.Length - suffix, n = aEnd - prefix, m = bEnd - prefix;
        if ((long)n * m <= MaxCells)
        {
            // Longest common subsequence over the changed middle section.
            var lcs = new int[n + 1, m + 1];
            for (var x = n - 1; x >= 0; x--)
                for (var y = m - 1; y >= 0; y--)
                    lcs[x, y] = a[prefix + x] == b[prefix + y] ? lcs[x + 1, y + 1] + 1 : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);
            while (i < aEnd && j < bEnd)
            {
                if (a[i] == b[j]) Keep();
                else if (lcs[i - prefix + 1, j - prefix] >= lcs[i - prefix, j - prefix + 1]) Remove();
                else Add();
            }
        }
        while (i < aEnd) Remove();
        while (j < bEnd) Add();
        while (i < a.Length) Keep();
        return edits;
    }
}
