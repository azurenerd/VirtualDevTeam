namespace VirtualDevTeam.Core.Agents.Reasoning;

/// <summary>
/// A single event in an agent's reasoning trace. Captures thinking, assessment,
/// refinement, and decisions so humans can observe the agent's thought process
/// in real-time and intervene if needed.
/// </summary>
public record AgentReasoningEvent
{
    public required string AgentId { get; init; }
    public required string AgentDisplayName { get; init; }
    public required AgentReasoningEventType EventType { get; init; }
    public required string Phase { get; init; }
    public required string Summary { get; init; }

    /// <summary>Full content of the event (AI prompt, response, assessment details, etc.)</summary>
    public string? Detail { get; init; }

    /// <summary>For assessment events: the specific gaps or criteria that failed.</summary>
    public IReadOnlyList<string>? Gaps { get; init; }

    /// <summary>For assessment events: did the output pass all criteria?</summary>
    public bool? Passed { get; init; }

    /// <summary>Which iteration of the assess/refine loop this event belongs to (0-based).</summary>
    public int Iteration { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Duration of the AI call that produced this event, if applicable.</summary>
    public TimeSpan? Duration { get; init; }
}

public enum AgentReasoningEventType
{
    /// <summary>Agent is planning what to do (gathering context, deciding approach).</summary>
    Planning,

    /// <summary>Agent is generating output (writing research, spec, architecture, code, etc.).</summary>
    Generating,

    /// <summary>Agent is assessing its own output against quality criteria.</summary>
    Assessing,

    /// <summary>Agent is refining its output based on assessment gaps.</summary>
    Refining,

    /// <summary>Agent made a decision (e.g., "output passes criteria" or "needs another iteration").</summary>
    Decision,

    /// <summary>Agent completed the task and is publishing the result.</summary>
    Publishing,

    /// <summary>Agent encountered an error or unexpected situation.</summary>
    Error,

    /// <summary>Human intervention was requested or received.</summary>
    HumanIntervention,
}
