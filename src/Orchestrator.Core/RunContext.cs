using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Orchestrator.Core;

public sealed class RunBudget
{
    private long tokensUsed;
    public double MaxMinutes { get; set; } = 20;
    // Two rework cycles with whole-source context can need more than 400,000 tokens (a live brownfield run used 431,508).
    public long MaxTokens { get; set; } = 600_000;
    [YamlIgnore]
    public long TokensUsed { get => Interlocked.Read(ref tokensUsed); set => Interlocked.Exchange(ref tokensUsed, value); }
    public long AddTokens(long count) => Interlocked.Add(ref tokensUsed, count);
}

public sealed class StageState
{
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public int Attempts { get; set; }
    public bool Approved { get; set; }
    public string? Approver { get; set; }
    public string? Reason { get; set; }
    public string? LastFailure { get; set; }
    // Set when an upstream artifact changed after this stage ran; the stage regenerates instead of reusing its draft.
    public string? ReplanReason { get; set; }
    public FallbackLevel? FallbackLevel { get; set; }
    public string? CommitSha { get; set; }
    // How often this (reviewing) stage sent its upstream work back for rework.
    public int Reworks { get; set; }
    // How often this stage was given a retry after writing outside its role's folders (at most once).
    public int BoundaryCorrections { get; set; }
    // Set while this stage is being redone after a failed review; such stages run one at a time (see RunEngine).
    public bool InRework { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

// Stages read the context concurrently; the engine serializes every write and checkpoint.
public sealed class RunContext
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public string Requirement { get; set; } = string.Empty;
    public Scenario Scenario { get; init; } = new();
    public ConcurrentDictionary<string, Artifact> Artifacts { get; init; } = new(StringComparer.Ordinal);
    // Every file a stage of this run has landed (path -> the stage that last changed it), kept across reworks
    // so a reviewer sees tests from an earlier round even when the latest artifact no longer names them.
    public ConcurrentDictionary<string, string> ChangedFiles { get; init; } = new(StringComparer.Ordinal);
    // The subset of ChangedFiles that did not exist before this run (path -> the stage that created it).
    public ConcurrentDictionary<string, string> CreatedFiles { get; init; } = new(StringComparer.Ordinal);
    public ImmutableList<Decision> Decisions { get; set; } = [];
    // Decisions of stages that were re-run after a re-plan; kept for history, excluded as parents.
    public ImmutableHashSet<string> SupersededDecisions { get; set; } = [];
    // Decision ID -> commit that landed the stage output carrying it.
    public ImmutableDictionary<string, string> DecisionCommits { get; set; } = ImmutableDictionary<string, string>.Empty;
    public RunBudget Budget { get; init; } = new();
    public ConcurrentDictionary<string, StageState> StageStatuses { get; init; } = new(StringComparer.Ordinal);
    public RunStatus Status { get; set; }
    public string? StopReason { get; set; }
    public string? GitBranch { get; set; }
    public string? LastGoodCommit { get; set; }
    public string? BaseCommit { get; set; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    [JsonIgnore]
    public long Revision { get; internal set; }
}
