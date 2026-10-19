using System.Text;
using System.Text.RegularExpressions;

namespace Orchestrator.Agents;

public sealed record QuestionOption(string Letter, string Text);
public sealed record OpenQuestion(string Id, string Text, string? Why, IReadOnlyList<QuestionOption> Options, string? Answer);

// The open-questions.md format: "## Q<n>. question", optional "Why it matters:", 2-3 "- A. option" lines and an
// "Answer:" line the human fills with option letters ("A" or "A, C") or "Other: free text".
public static partial class OpenQuestions
{
    [GeneratedRegex(@"^## (Q\d+)\.\s+(.+)$")] private static partial Regex Header();
    [GeneratedRegex(@"^- ([A-Z])\.\s+(.+)$")] private static partial Regex Option();
    [GeneratedRegex(@"^Why it matters:\s*(.+)$", RegexOptions.IgnoreCase)] private static partial Regex Why();
    [GeneratedRegex(@"^Answer:\s*(.*)$", RegexOptions.IgnoreCase)] private static partial Regex AnswerLine();

    public static IReadOnlyList<OpenQuestion> Parse(string markdown)
    {
        var questions = new List<OpenQuestion>();
        string? id = null, text = null, why = null, answer = null;
        var options = new List<QuestionOption>();
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (Header().Match(line) is { Success: true } header) { Flush(); (id, text) = (header.Groups[1].Value, header.Groups[2].Value.Trim()); continue; }
            if (line.StartsWith("## ", StringComparison.Ordinal)) { Flush(); continue; } // e.g. the "## Answers" summary
            if (id is null) continue;
            if (Option().Match(line) is { Success: true } option) options.Add(new(option.Groups[1].Value, option.Groups[2].Value.Trim()));
            else if (Why().Match(line) is { Success: true } reason) why = reason.Groups[1].Value.Trim();
            else if (AnswerLine().Match(line) is { Success: true } given) answer = given.Groups[1].Value.Trim();
        }
        Flush();
        if (questions.Count == 0) throw new InvalidDataException("open-questions.md must contain at least one \"## Q<n>. question\".");
        if (questions.Select(q => q.Id).Distinct().Count() != questions.Count) throw new InvalidDataException("Question ids must be unique.");
        return questions;

        void Flush()
        {
            if (id is null) return;
            if (options.Count is < 2 or > 3) throw new InvalidDataException($"{id} must offer 2-3 interpretations; it has {options.Count}.");
            if (options.Select(o => o.Letter).Distinct().Count() != options.Count) throw new InvalidDataException($"{id} repeats an option letter.");
            var question = new OpenQuestion(id, text!, why, options.ToArray(), null);
            questions.Add(question with { Answer = Normalize(question, answer) });
            (id, text, why, answer) = (null, null, null, null);
            options.Clear();
        }
    }

    // "c, a" -> "A, C"; "other: x" -> "Other: x"; blank -> null.
    public static string? Normalize(OpenQuestion question, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith("Other:", StringComparison.OrdinalIgnoreCase))
            return raw[6..].Trim() is { Length: > 0 } other ? "Other: " + other : throw new InvalidDataException($"{question.Id}: \"Other:\" needs your answer text.");
        var letters = raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().TrimEnd('.').ToUpperInvariant()).Distinct().Order().ToArray();
        var unknown = letters.Where(l => question.Options.All(o => o.Letter != l)).ToArray();
        if (unknown.Length > 0) throw new InvalidDataException($"{question.Id}: unknown option(s) {string.Join(", ", unknown)}; use {string.Join("/", question.Options.Select(o => o.Letter))} or \"Other: ...\".");
        return string.Join(", ", letters);
    }

    public static string Describe(OpenQuestion question) => question.Answer is null ? "(unanswered)"
        : question.Answer.StartsWith("Other:", StringComparison.Ordinal) ? question.Answer
        : string.Join("; ", question.Answer.Split(", ").Select(l => $"{l}. {question.Options.First(o => o.Letter == l).Text}"));

    public static string Render(string requirement, IReadOnlyList<OpenQuestion> questions)
    {
        var text = new StringBuilder("# Open questions\n\n");
        text.Append("Requirement: ").Append(requirement.ReplaceLineEndings(" ").Trim()).Append("\n\n");
        text.Append("Answer each question on its \"Answer:\" line with one or more option letters (for example \"A\" or \"A, C\"), ")
            .Append("or \"Other: <your answer>\". Save the file, then approve. Changing an answer later re-plans the stages that depend on it.\n");
        foreach (var q in questions)
        {
            text.Append($"\n## {q.Id}. {q.Text}\n");
            if (q.Why is not null) text.Append($"Why it matters: {q.Why}\n");
            foreach (var option in q.Options) text.Append($"- {option.Letter}. {option.Text}\n");
            text.Append($"Answer: {q.Answer}\n");
        }
        text.Append("\n## Answers\n");
        foreach (var q in questions) text.Append($"- {q.Id}: {q.Answer ?? "(unanswered)"}\n");
        return text.ToString();
    }
}
