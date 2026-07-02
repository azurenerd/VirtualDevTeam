namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Outcome of a flow action attempt.
/// </summary>
public enum FlowActionResult
{
    /// <summary>Action ran and reported success (whatever the action defines as success).</summary>
    Success,
    /// <summary>Action ran but reported a soft failure (e.g., target was already resolved).</summary>
    NoOp,
    /// <summary>Action threw an exception or returned a hard failure.</summary>
    Failed,
    /// <summary>Action was rate-limited or gated by config and did not run.</summary>
    Skipped,
}

/// <summary>
/// A recorded action attempt by the FlowMonitor. Always paired with a FlowFinding
/// that motivated it. Persisted to SQLite so the dashboard can render an audit trail.
/// </summary>
public sealed record FlowAction
{
    public required string Id { get; init; }
    public required string FindingId { get; init; }
    public required string ActionType { get; init; }
    public string? Target { get; init; }
    public required DateTimeOffset InitiatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required FlowActionResult Result { get; init; }
    public string? Detail { get; init; }
    /// <summary>
    /// 1-based escalation rung that produced this action (T1.2). Rung 1 = bus nudge,
    /// rung 2 = explicit comment ask, rung 3 = human-escalation + label. Persisted to
    /// the <c>attempt_count</c> column so the routing layer can count prior actions on
    /// the same dedup_key and step up the ladder.
    /// </summary>
    public int AttemptCount { get; init; } = 1;
}
