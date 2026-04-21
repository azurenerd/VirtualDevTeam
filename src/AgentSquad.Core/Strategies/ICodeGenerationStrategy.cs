namespace AgentSquad.Core.Strategies;

/// <summary>
/// A code-generation strategy (baseline / mcp-enhanced / agentic-delegation). Each
/// strategy receives a scoped worktree and returns a patch plus cost/timing metadata.
/// Strategies must never mutate files outside <see cref="StrategyInvocation.WorktreePath"/>.
/// </summary>
public interface ICodeGenerationStrategy
{
    /// <summary>Stable strategy identifier used in config, logs, experiment records, and commit trailers.</summary>
    string Id { get; }

    /// <summary>Generate code inside the worktree. Must be safe to cancel at any point.</summary>
    Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct);
}

/// <summary>Task-level context supplied to every candidate (shared, immutable).</summary>
public record TaskContext
{
    public required string TaskId { get; init; }
    public required string TaskTitle { get; init; }
    public required string TaskDescription { get; init; }
    public required string PrBranch { get; init; }
    public required string BaseSha { get; init; }
    public required string RunId { get; init; }
    /// <summary>Full repo path of the SE agent's LocalWorkspace (candidate root is derived from this).</summary>
    public required string AgentRepoPath { get; init; }
    /// <summary>Complexity hint from task metadata; influences SamplingPolicy evaluation.</summary>
    public int Complexity { get; init; } = 1;
    /// <summary>True when the task is web/UI — enables Gate3 (AppStarts).</summary>
    public bool IsWebTask { get; init; }

    // ── Optional code-gen context fields (populated by SE when invoking the orchestrator) ──
    // Strategies that do real LLM generation (BaselineStrategy + IBaselineCodeGenerator) read
    // these to build a single-pass prompt with parity to the SE agent's legacy code-gen path.
    // Null/empty means "not supplied" — the generator falls back to a minimal default.

    /// <summary>PM specification document body (Markdown). Optional context for code-gen prompts.</summary>
    public string? PmSpec { get; init; }
    /// <summary>Architecture document body (Markdown). Optional context for code-gen prompts.</summary>
    public string? Architecture { get; init; }
    /// <summary>Tech stack hint (e.g., "Blazor Server, .NET 8"). Used by prompts and design-context heuristics.</summary>
    public string? TechStack { get; init; }
    /// <summary>Source GitHub issue context, formatted as "## GitHub Issue #N: Title\nbody". Optional.</summary>
    public string? IssueContext { get; init; }
    /// <summary>UI design context (HTML mockups, design tokens) gated to UI tasks. Optional.</summary>
    public string? DesignContext { get; init; }
}

/// <summary>Per-candidate invocation handed to a strategy at run time.</summary>
public record StrategyInvocation
{
    public required TaskContext Task { get; init; }
    public required string WorktreePath { get; init; }
    public required string StrategyId { get; init; }
    /// <summary>Hard wall-clock timeout for this strategy.</summary>
    public required TimeSpan Timeout { get; init; }
}

/// <summary>What a strategy returns after executing inside its worktree.</summary>
public record StrategyExecutionResult
{
    public required string StrategyId { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    /// <summary>Wall-clock elapsed time.</summary>
    public required TimeSpan Elapsed { get; init; }
    /// <summary>Tokens consumed (input + output) if the strategy tracks them. Null when unknown.</summary>
    public long? TokensUsed { get; init; }
    /// <summary>Diagnostic log lines (truncated). Stored in experiment record.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
}

/// <summary>Evaluator output for a single candidate after hard-gate + LLM scoring.</summary>
public record CandidateResult
{
    public required string StrategyId { get; init; }
    public required bool Survived { get; init; }
    /// <summary>The first gate that failed (null when all passed).</summary>
    public string? FailedGate { get; init; }
    public string? FailureDetail { get; init; }
    /// <summary>Diff against base SHA (may be empty string when Gate1 failed).</summary>
    public string Patch { get; init; } = "";
    public int PatchSizeBytes { get; init; }
    public required StrategyExecutionResult Execution { get; init; }
    /// <summary>LLM scoring output (null when evaluator skipped or gate failed).</summary>
    public CandidateScore? Score { get; init; }
    /// <summary>PNG screenshot bytes captured after build gate passed (null if capture failed/skipped/non-web).</summary>
    public byte[]? ScreenshotBytes { get; init; }
}

/// <summary>Scores from the LLM judge (0-10 integer scale per the doc).</summary>
public record CandidateScore
{
    public int AcceptanceCriteriaScore { get; init; }
    public int DesignScore { get; init; }
    public int ReadabilityScore { get; init; }
    public string Reasoning { get; init; } = "";
}

/// <summary>Final evaluator verdict for a task: all candidates + the winner pick.</summary>
public record EvaluationResult
{
    public required IReadOnlyList<CandidateResult> Candidates { get; init; }
    public CandidateResult? Winner { get; init; }
    public string? TieBreakReason { get; init; }
    public TimeSpan EvaluationElapsed { get; init; }
}
