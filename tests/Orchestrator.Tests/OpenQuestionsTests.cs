using Orchestrator.Agents;
using Xunit;

namespace Orchestrator.Tests;

public sealed class OpenQuestionsTests
{
    private const string Sheet = """
        # Open questions
        ## Q1. Secure how?
        Why it matters: cost differs.
        - A. Screen targets
        - B. Longer codes
        - C. API keys
        Answer: c, a
        ## Q2. Analytics how?
        - A. Country
        - B. CSV export
        Answer:
        """;

    [Fact]
    public void Answers_are_normalized_and_rendered_canonically()
    {
        var questions = OpenQuestions.Parse(Sheet);
        Assert.Equal(("A, C", (string?)null), (questions[0].Answer, questions[1].Answer));
        Assert.Equal("cost differs.", questions[0].Why);
        Assert.Equal("A. Screen targets; C. API keys", OpenQuestions.Describe(questions[0]));
        var rendered = OpenQuestions.Render("Make links secure.", questions);
        Assert.Contains("- Q1: A, C\n- Q2: (unanswered)", rendered);
        Assert.Equal(questions, OpenQuestions.Parse(rendered), new ShapeComparer()); // round-trips
        Assert.Equal("Other: rate limits per key", OpenQuestions.Parse(Sheet.Replace("Answer:\n", "Answer: other: rate limits per key\n").Replace("Answer:\r\n", "Answer: other: rate limits per key\r\n"))[1].Answer
            ?? OpenQuestions.Normalize(questions[1], "other: rate limits per key"));
    }

    [Theory]
    [InlineData("- A. Only one", "2-3 interpretations")]
    [InlineData("- A. One\n- B. Two\n- C. Three\n- D. Four", "2-3 interpretations")]
    [InlineData("- A. One\n- A. Again", "repeats an option letter")]
    public void Each_question_needs_two_or_three_distinct_interpretations(string options, string error)
    {
        var exception = Assert.Throws<InvalidDataException>(() => OpenQuestions.Parse($"## Q1. Pick\n{options}\nAnswer:\n"));
        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public void Unknown_option_letters_and_empty_documents_are_rejected()
    {
        Assert.Contains("unknown option(s) D", Assert.Throws<InvalidDataException>(() => OpenQuestions.Parse(Sheet.Replace("Answer: c, a", "Answer: D"))).Message);
        Assert.Throws<InvalidDataException>(() => OpenQuestions.Parse("# nothing here"));
    }

    private sealed class ShapeComparer : IEqualityComparer<OpenQuestion>
    {
        public bool Equals(OpenQuestion? x, OpenQuestion? y) => x!.Id == y!.Id && x.Text == y.Text && x.Answer == y.Answer && x.Options.SequenceEqual(y.Options);
        public int GetHashCode(OpenQuestion obj) => obj.Id.GetHashCode();
    }
}
