namespace Orchestrator.Cli;

public sealed record CommandLine(string Command, string Target, bool Replay, bool AutoApprove, bool Lineage, string? Requirement, long? MaxTokens = null)
{
    public const string Usage = "Usage: run <scenario.yaml> [--replay] [--auto-approve] [--requirement \"<plain-English requirement>\"]"
        + " | resume <runId> [--max-tokens <n>] | report <runId> [--lineage] | stop <runId>";

    // Strict parsing: unknown options, misplaced options and contradictory combinations are errors.
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] is not ("run" or "resume" or "report" or "stop")) throw new ArgumentException(Usage);
        bool replay = false, autoApprove = false, lineage = false;
        string? requirement = null;
        long? maxTokens = null;
        for (var i = 2; i < args.Count; i++)
        {
            switch (args[0], args[i])
            {
                case ("run", "--replay"): replay = true; break;
                case ("run", "--auto-approve"): autoApprove = true; break;
                case ("report", "--lineage"): lineage = true; break;
                case ("run", "--requirement"):
                    if (requirement is not null) throw new ArgumentException("--requirement may be given only once.");
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--requirement needs a plain-English requirement in quotes, for example --requirement \"Add QR codes for short links\".");
                    requirement = args[++i].Trim();
                    break;
                case ("resume", "--max-tokens"):
                    if (maxTokens is not null) throw new ArgumentException("--max-tokens may be given only once.");
                    if (i + 1 >= args.Count || !long.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit)
                        || limit is < 1 or > 10_000_000)
                        throw new ArgumentException("--max-tokens needs a whole number of tokens between 1 and 10000000, for example --max-tokens 800000.");
                    maxTokens = limit;
                    i++;
                    break;
                default: throw new ArgumentException($"Unknown option '{args[i]}' for '{args[0]}'. {Usage}");
            }
        }
        // Replay only knows the recorded answers of the three demo scenarios; a new requirement needs a real model.
        if (replay && requirement is not null)
            throw new ArgumentException("--requirement needs a real model; remove --replay and configure LLM:ApiKey and LLM:Model (see README).");
        return new(args[0], args[1], replay, autoApprove, lineage, requirement, maxTokens);
    }
}
