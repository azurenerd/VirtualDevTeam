namespace AgentSquad.Core.Strategies.Contracts;

/// <summary>
/// Post-execution summary for a single strategy candidate. Built from data
/// already available after evaluation: patch (parsed into file changes),
/// diagnostic logs, metrics, judge reasoning, and gate results.
/// Consumed by the dashboard Strategies page for the detailed tabs view.
/// </summary>
public record CandidateExecutionSummary
{
    /// <summary>Strategy/framework identifier (e.g., "baseline", "squad").</summary>
    public required string StrategyId { get; init; }

    /// <summary>Whether the candidate survived build gates.</summary>
    public bool Survived { get; init; }

    /// <summary>Name of the first gate that failed (null when all passed).</summary>
    public string? FailedGate { get; init; }

    /// <summary>Detail message for gate failure.</summary>
    public string? FailureDetail { get; init; }

    /// <summary>LLM judge reasoning text (null when judge didn't run).</summary>
    public string? JudgeReasoning { get; init; }

    /// <summary>Why the judge was skipped (null when judge ran normally).</summary>
    public string? JudgeSkippedReason { get; init; }

    /// <summary>File changes parsed from the unified diff.</summary>
    public IReadOnlyList<FileChangeSummary> FilesChanged { get; init; } = Array.Empty<FileChangeSummary>();

    /// <summary>Total lines added across all files.</summary>
    public int TotalLinesAdded { get; init; }

    /// <summary>Total lines removed across all files.</summary>
    public int TotalLinesRemoved { get; init; }

    /// <summary>Raw patch size in bytes.</summary>
    public int PatchSizeBytes { get; init; }

    /// <summary>Wall-clock elapsed time in seconds.</summary>
    public double ElapsedSec { get; init; }

    /// <summary>Tokens consumed (null when unknown).</summary>
    public long? TokensUsed { get; init; }

    /// <summary>Last N diagnostic log lines from the strategy execution.</summary>
    public IReadOnlyList<string> DiagnosticLog { get; init; } = Array.Empty<string>();

    /// <summary>LLM judge scores (null when judge didn't run).</summary>
    public ScoreSummary? Scores { get; init; }
}

/// <summary>Per-file change summary for display in the dashboard.</summary>
public record FileChangeSummary
{
    public required string Path { get; init; }
    public required string Type { get; init; }  // Added, Modified, Deleted, Renamed
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public bool IsBinary { get; init; }
}

/// <summary>Score summary from the LLM judge.</summary>
public record ScoreSummary
{
    public int AcceptanceCriteria { get; init; }
    public int Design { get; init; }
    public int Readability { get; init; }
    /// <summary>Visual quality score (0-10). Null when not applicable (non-visual task).</summary>
    public int? Visuals { get; init; }
    /// <summary>Total score: /40 when Visuals applies, /30 when it doesn't.</summary>
    public int Total => AcceptanceCriteria + Design + Readability + (Visuals ?? 0);
    /// <summary>Maximum possible score for this candidate.</summary>
    public int MaxScore => Visuals.HasValue ? 40 : 30;
}
