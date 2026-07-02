namespace VirtualDevTeam.Core.Frameworks;

// ── Invocation ──

/// <summary>Per-candidate invocation context for a framework adapter.</summary>
public record FrameworkInvocation
{
    /// <summary>Task-level context (id, title, description, branch, etc.).</summary>
    public required FrameworkTaskContext Task { get; init; }
    /// <summary>Isolated worktree path where the framework must write all output.</summary>
    public required string WorktreePath { get; init; }
    /// <summary>Framework adapter ID (matches <see cref="IAgenticFrameworkAdapter.Id"/>).</summary>
    public required string FrameworkId { get; init; }
    /// <summary>Hard wall-clock timeout for this execution.</summary>
    public required TimeSpan Timeout { get; init; }
    /// <summary>
    /// Optional progress callback for real-time activity streaming to the dashboard.
    /// Adapters report significant events (tool calls, decisions, sub-agent spawns) via this sink.
    /// </summary>
    public IProgress<FrameworkActivityEvent>? ActivitySink { get; init; }
    /// <summary>
    /// Non-null during revision rounds. Contains judge scores, feedback, and the
    /// original patch so framework adapters can make targeted fixes instead of regenerating.
    /// </summary>
    public Strategies.RevisionContext? Revision { get; init; }
}

/// <summary>Activity event reported by framework adapters during execution.</summary>
public record FrameworkActivityEvent(string Category, string Message, Dictionary<string, object>? Metadata = null);

/// <summary>Task metadata supplied to every framework adapter (shared, immutable).</summary>
public record FrameworkTaskContext
{
    public required string TaskId { get; init; }
    public required string TaskTitle { get; init; }
    public required string TaskDescription { get; init; }
    public required string PrBranch { get; init; }
    public required string BaseSha { get; init; }
    public required string RunId { get; init; }
    public required string AgentRepoPath { get; init; }
    public int Complexity { get; init; } = 1;
    public bool IsWebTask { get; init; }
    public string? PmSpec { get; init; }
    public string? Architecture { get; init; }
    public string? TechStack { get; init; }
    public string? IssueContext { get; init; }
    public string? DesignContext { get; init; }
    /// <summary>
    /// Pre-gathered summary of the existing project (README, copilot-instructions, structure,
    /// patterns, dependencies). When present, framework prompts include it so candidates
    /// understand the existing codebase before generating code.
    /// </summary>
    public string? ExistingProjectContext { get; init; }
}

/// <summary>What a framework adapter returns after executing inside its worktree.</summary>
public record FrameworkExecutionResult
{
    public required string FrameworkId { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public required TimeSpan Elapsed { get; init; }
    /// <summary>Tokens consumed (input + output). Null when unknown (not zero!).</summary>
    public long? TokensUsed { get; init; }
    /// <summary>Diagnostic log lines (truncated). Stored in experiment record.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
    /// <summary>Framework-specific metrics collected during execution.</summary>
    public FrameworkMetrics? Metrics { get; init; }
}

// ── Metrics ──

/// <summary>Aggregate metrics from a single framework execution.</summary>
public record FrameworkMetrics
{
    /// <summary>Tokens consumed (input + output). Null means "unknown" (not zero!).</summary>
    public long? TokensUsed { get; init; }
    /// <summary>Estimated cost in USD. Null for frameworks that don't report cost.</summary>
    public decimal? EstimatedCost { get; init; }
    public int FilesModified { get; init; }
    public int LlmCallsMade { get; init; }
    public int SubAgentSpawns { get; init; }
    public TimeSpan ElapsedTime { get; init; }
}

// ── Readiness / Install ──

public enum FrameworkReadiness
{
    Ready,
    InstallRequired,
    MissingDependency,
    Error
}

public record FrameworkReadinessResult(
    FrameworkReadiness Status,
    string Message,
    IReadOnlyList<string> MissingDependencies);

public record FrameworkInstallResult(
    bool Succeeded,
    string Message);

// ── Telemetry Events ──

public enum FrameworkEventType
{
    Decision,
    CodeGen,
    Review,
    SubAgentSpawn,
    SubAgentComplete,
    ToolCall,
    Error,
    Waiting,
    Approval
}

/// <summary>A single observable event from a framework execution.</summary>
public record FrameworkEvent(
    DateTimeOffset Timestamp,
    FrameworkEventType Type,
    string AgentName,
    string Description,
    Dictionary<string, object>? Metadata = null);

// ── Revision Invocation ──

/// <summary>
/// Lightweight invocation context for surgical revision. Contains only the feedback
/// and file hints needed for targeted edits — no full task description or architecture.
/// </summary>
public record FrameworkRevisionInvocation
{
    /// <summary>Isolated worktree path containing the original code to be surgically edited.</summary>
    public required string WorktreePath { get; init; }
    /// <summary>Framework adapter ID.</summary>
    public required string FrameworkId { get; init; }
    /// <summary>Brief task title for minimal context.</summary>
    public required string TaskTitle { get; init; }
    /// <summary>Task ID for logging/correlation.</summary>
    public required string TaskId { get; init; }
    /// <summary>Run ID for logging/correlation.</summary>
    public required string RunId { get; init; }
    /// <summary>Hard wall-clock timeout for the revision session.</summary>
    public required TimeSpan Timeout { get; init; }
    /// <summary>Initial judge scores by axis (e.g., "ac": 7, "design": 5).</summary>
    public required IReadOnlyDictionary<string, int> InitialScores { get; init; }
    /// <summary>Overall judge feedback (what to fix).</summary>
    public required string JudgeFeedback { get; init; }
    /// <summary>Per-axis feedback: acceptance criteria.</summary>
    public string? AcFeedback { get; init; }
    /// <summary>Per-axis feedback: design quality.</summary>
    public string? DesignFeedback { get; init; }
    /// <summary>Per-axis feedback: code readability.</summary>
    public string? ReadabilityFeedback { get; init; }
    /// <summary>Per-axis feedback: visual quality.</summary>
    public string? VisualsFeedback { get; init; }
    /// <summary>Independent rubber-duck critique (different model perspective).</summary>
    public string? RubberDuckFeedback { get; init; }
    /// <summary>List of files touched in the original patch (helps CLI stay focused).</summary>
    public IReadOnlyList<string> OriginalFiles { get; init; } = Array.Empty<string>();
    /// <summary>Optional progress callback for real-time activity streaming.</summary>
    public IProgress<FrameworkActivityEvent>? ActivitySink { get; init; }
    /// <summary>Base SHA for patch extraction after revision completes.</summary>
    public string? BaseSha { get; init; }
}

/// <summary>Point-in-time snapshot of framework activity.</summary>
public record FrameworkActivitySnapshot
{
    public int ActiveAgents { get; init; }
    public IReadOnlyList<FrameworkAgentStatus> Agents { get; init; } = Array.Empty<FrameworkAgentStatus>();
    public IReadOnlyList<string> RecentDecisions { get; init; } = Array.Empty<string>();
    public FrameworkMetrics? Metrics { get; init; }
}

/// <summary>Status of a single sub-agent within the framework.</summary>
public record FrameworkAgentStatus(
    string Name,
    string Role,
    string CurrentTask,
    string Status);
