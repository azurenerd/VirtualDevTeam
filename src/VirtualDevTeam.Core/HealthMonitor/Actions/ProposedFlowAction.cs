using System.Text.Json.Serialization;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// A proposed FlowMonitor remediation action awaiting operator approval. Generated
/// by the escalation ladder at Rung 3 (between rung-2 explicit-ask and rung-4
/// escalate-to-human) when a finding has been unresolved by the safer rungs.
///
/// <para>
/// Operator workflow: Approvals page shows each pending proposal as a card with
/// Title, Rationale, RiskAssessment, and Approve/Reject buttons. On approve,
/// <see cref="VirtualDevTeam.Core.HealthMonitor.Actions.IFlowActionExecutor"/> dispatches
/// by <see cref="Type"/> to run the action.
/// </para>
/// </summary>
public sealed record ProposedFlowAction
{
    public required string Id { get; init; }
    public required string FindingId { get; init; }
    public required FlowActionType Type { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }

    /// <summary>Operator-facing title shown on the Approvals page card.</summary>
    public required string Title { get; init; }

    /// <summary>Why this action is being proposed — links the finding evidence to the action.</summary>
    public required string Rationale { get; init; }

    /// <summary>What could go wrong if approved. Tier (LOW/MEDIUM/HIGH) + free-form description.</summary>
    public required string RiskAssessment { get; init; }

    /// <summary>Risk tier — drives default policy (Safe-auto / Reversible-soft-approve / etc.).</summary>
    public required FlowActionRiskTier RiskTier { get; init; }

    public required ProposedFlowActionState State { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? OperatorActionAt { get; init; }
    public string? OperatorRationale { get; init; }
    public string? ExecutionResult { get; init; }
    public DateTime? ExpiresAt { get; init; }   // default: CreatedAt + 1h

    // Phase-2 fields for analytics — null in MVP, populated by FlowActionExecutor on success/failure
    public int? ExecutionDurationMs { get; init; }
    public string? ExecutionLog { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowActionType
{
    /// <summary>Add a label to a PR. Parameters: prNumber, labelName.</summary>
    AddPrLabel,

    /// <summary>Remove a label from a PR. Parameters: prNumber, labelName.</summary>
    RemovePrLabel,

    /// <summary>Post a comment on a PR. Parameters: prNumber, body.</summary>
    PostPrComment,

    /// <summary>Publish a FlowMonitorNudge to an agent. Parameters: agentId, prNumber, reason.</summary>
    NudgeAgent,

    /// <summary>Force re-run of a specific stuck agent step. Parameters: agentId, stepName.</summary>
    RetryAgentStep,

    /// <summary>Clear an agent's head-SHA-dedup cache for a PR. Parameters: agentId, prNumber.</summary>
    ClearReviewCache,

    /// <summary>Recycle an agent's run loop (NOT process restart). Parameters: agentId.</summary>
    RestartAgent,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProposedFlowActionState { Pending, Approved, Rejected, Executed, Failed, Expired }

/// <summary>
/// Risk tiers from the plan's policy table. Used to gate which actions can be auto-executed
/// vs require operator approval. Configurable per-deployment via appsettings.json.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowActionRiskTier
{
    /// <summary>Always-reversible, no external side-effects (NudgeAgent, RetryAgentStep, ClearReviewCache).
    /// Can be auto-executed when operator opts in to policy.</summary>
    SafeAuto,

    /// <summary>Reversible but visible (AddPrLabel, RemovePrLabel, PostPrComment).
    /// Default: operator-approval required, single-click approve.</summary>
    ReversibleSoftApprove,

    /// <summary>State-changing with broader impact (RestartAgent). Default: operator-approval required
    /// with 2-step confirmation in the UI.</summary>
    StateChangingHardApprove,

    /// <summary>Not a FlowAction at all — operator manual action only (merge PR, close issue, push commit).
    /// Reserved for the enum's completeness, no execution path provided.</summary>
    NeverAuto,
}
