namespace VirtualDevTeam.Core.Agents.Steps;

/// <summary>
/// Represents a single logical step in an agent's task execution.
/// Steps are the high-level phases of work (e.g., "Read context", "Multi-turn design", "Self-assessment")
/// that group together into a complete task workflow.
/// </summary>
public record AgentTaskStep
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public required string TaskId { get; init; }
    public required int StepIndex { get; init; }
    public required string Name { get; init; }
    public string? Description { get; set; }
    public AgentTaskStepStatus Status { get; set; } = AgentTaskStepStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>If set, this step is nested under the specified parent step (e.g., a strategy candidate under "Multi-strategy code generation").</summary>
    public string? ParentStepId { get; init; }

    /// <summary>True when this step is a container for child steps (e.g., "Multi-strategy code generation"). Container steps are excluded from progress counting.</summary>
    public bool IsContainer { get; init; }

    /// <summary>Computed elapsed time from start to completion (or to now if in-progress).</summary>
    public TimeSpan? Elapsed => Status switch
    {
        AgentTaskStepStatus.Completed or AgentTaskStepStatus.Failed when StartedAt.HasValue && CompletedAt.HasValue
            => CompletedAt.Value - StartedAt.Value,
        AgentTaskStepStatus.InProgress when StartedAt.HasValue
            => DateTime.UtcNow - StartedAt.Value,
        _ => null
    };

    internal int _llmCallCount;
    public int LlmCallCount { get => _llmCallCount; set => _llmCallCount = value; }
    public decimal EstimatedCost { get; set; }
    public string? ModelUsed { get; set; }
    public List<AgentTaskSubStep> SubSteps { get; init; } = new();
}

/// <summary>
/// A sub-step within a task step (e.g., individual turns in a multi-turn conversation).
/// </summary>
public record AgentTaskSubStep
{
    public required int TurnIndex { get; init; }
    public required string Description { get; init; }
    public TimeSpan? Duration { get; init; }
    public decimal EstimatedCost { get; init; }
}

public enum AgentTaskStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
    WaitingOnHuman
}

/// <summary>
/// A group of steps that belong to the same logical task (e.g., "PE Planning", "PR #42 Testing").
/// Used by the dashboard to display steps organized by task rather than as a flat list.
/// </summary>
public record AgentTaskGroup
{
    public required string TaskId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<AgentTaskStep> Steps { get; init; }
    public int Completed => Steps.Count(s => !s.IsContainer && s.Status is AgentTaskStepStatus.Completed or AgentTaskStepStatus.Skipped);
    public int Total => Steps.Count(s => !s.IsContainer);
    public DateTime? StartedAt => Steps.Where(s => s.StartedAt.HasValue).Select(s => s.StartedAt!.Value).DefaultIfEmpty().Min();
    public DateTime? CompletedAt => Steps.All(s => s.Status is AgentTaskStepStatus.Completed or AgentTaskStepStatus.Skipped or AgentTaskStepStatus.Failed)
        ? Steps.Where(s => s.CompletedAt.HasValue).Select(s => s.CompletedAt!.Value).DefaultIfEmpty().Max()
        : null;

    public AgentTaskStepStatus Status
    {
        get
        {
            if (Steps.Count == 0) return AgentTaskStepStatus.Pending;
            if (Steps.Any(s => s.Status == AgentTaskStepStatus.WaitingOnHuman)) return AgentTaskStepStatus.WaitingOnHuman;
            if (Steps.Any(s => s.Status == AgentTaskStepStatus.InProgress)) return AgentTaskStepStatus.InProgress;
            if (Steps.Any(s => s.Status == AgentTaskStepStatus.Failed)) return AgentTaskStepStatus.Failed;
            if (Steps.All(s => s.Status is AgentTaskStepStatus.Completed or AgentTaskStepStatus.Skipped)) return AgentTaskStepStatus.Completed;
            return AgentTaskStepStatus.Pending;
        }
    }

    public TimeSpan? TotalElapsed
    {
        get
        {
            if (!StartedAt.HasValue) return null;
            if (CompletedAt.HasValue) return CompletedAt.Value - StartedAt.Value;
            return DateTime.UtcNow - StartedAt.Value;
        }
    }

    public int TotalLlmCalls => Steps.Sum(s => s.LlmCallCount);
    public decimal TotalCost => Steps.Sum(s => s.EstimatedCost);
}
