using VirtualDevTeam.Core.Agents;

namespace VirtualDevTeam.Core.Messaging;

/// <summary>
/// Sent by FlowMonitor's <c>NudgeReviewerAction</c> when an open PR has been waiting
/// longer than the stuck threshold for a specific reviewer role's approval label.
/// Subscriber agents should enqueue the PR number for immediate review.
/// </summary>
public record ReviewNudgeMessage : AgentMessage
{
    /// <summary>The PR that needs review.</summary>
    public required int PrNumber { get; init; }

    /// <summary>
    /// Canonical reviewer role name — "ProgramManager", "Architect", or "SoftwareEngineer".
    /// Subscribers should check this matches their own role before acting.
    /// </summary>
    public required string ReviewerRole { get; init; }

    /// <summary>Short human-readable reason for the nudge (for logs and audit trails).</summary>
    public required string Reason { get; init; }
}
