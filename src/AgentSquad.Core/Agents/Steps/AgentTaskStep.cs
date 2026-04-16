namespace AgentSquad.Core.Agents.Steps;

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
