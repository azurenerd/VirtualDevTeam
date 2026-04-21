namespace AgentSquad.Core.Frameworks;

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
}

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
}

// ── Execution Result ──

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
