namespace VirtualDevTeam.Core.Agents;

/// <summary>
/// Structured reason for why an agent is in <see cref="AgentStatus.Blocked"/>.
/// Populated when an agent waits on a human gate (see <see cref="AgentBase.WaitForHumanGateAsync"/>);
/// cleared when the gate is resolved.
///
/// Surfaced on agent cards/detail pages so the operator can deep-link to the
/// matching gate on /approvals without parsing the free-form StatusReason text.
/// </summary>
public sealed record BlockedReason
{
    /// <summary>Stable gate identifier (e.g. <c>ArchitectureDesign</c>) — matches <see cref="Configuration.GateIds"/>.</summary>
    public required string GateId { get; init; }

    /// <summary>Human-readable gate name (e.g. "Architecture Design"). Falls back to GateId if unknown.</summary>
    public required string GateName { get; init; }

    /// <summary>UTC timestamp when the agent entered the Blocked state on this gate.</summary>
    public required DateTime BlockedSince { get; init; }

    /// <summary>Associated PR number, when the gate is scoped to a PR.</summary>
    public int? PullRequestNumber { get; init; }

    /// <summary>Associated work-item / issue number, when the gate is scoped to one.</summary>
    public int? IssueNumber { get; init; }
}
