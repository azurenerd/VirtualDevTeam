namespace VirtualDevTeam.Core.Agents;

public record AgentMessage
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString();
    public required string FromAgentId { get; init; }
    public required string ToAgentId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public required string MessageType { get; init; }
}

public record TaskAssignmentMessage : AgentMessage
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? PullRequestUrl { get; init; }
    public required string Complexity { get; init; }
    /// <summary>Optional linked GitHub issue number for tracking.</summary>
    public int? IssueNumber { get; init; }
}

public record StatusUpdateMessage : AgentMessage
{
    public required AgentStatus NewStatus { get; init; }
    public string? CurrentTask { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// FlowMonitor nudge message — tells an agent to re-check its loop state immediately.
/// Split from StatusUpdateMessage per SimplificationRecommendations §2.7 to eliminate
/// fragile MessageType string checks.
/// </summary>
public record FlowMonitorNudgeMessage : AgentMessage
{
    /// <summary>Human-readable reason for the nudge (e.g., finding summary).</summary>
    public string? Reason { get; init; }
}

// NOTE: HelpRequestMessage was removed as a dead letter (PM subscribed but nobody published).
// See SimplificationRecommendations.md §1.4.

public record ResourceRequestMessage : AgentMessage
{
    public required AgentRole RequestedRole { get; init; }
    public required string Justification { get; init; }
    public required int CurrentTeamSize { get; init; }
    /// <summary>
    /// Number of agents to spawn. The PM will spawn up to this many (capped by pool limits).
    /// Default is 1 for backward compatibility.
    /// </summary>
    public int RequestedCount { get; init; } = 1;
    /// <summary>
    /// Desired skill capabilities for the requested agent. If non-empty, the spawn manager
    /// should prefer creating a specialist with matching capabilities.
    /// </summary>
    public List<string> DesiredCapabilities { get; init; } = [];
}

public record ReviewRequestMessage : AgentMessage
{
    public required int PrNumber { get; init; }
    public required string PrTitle { get; init; }
    public required string ReviewType { get; init; }
}

public record ChangesRequestedMessage : AgentMessage
{
    public required int PrNumber { get; init; }
    public required string PrTitle { get; init; }
    public required string ReviewerAgent { get; init; }
    public required string Feedback { get; init; }
}

/// <summary>
/// Sent by a reviewer (PM/Architect) when a PR is approved, so the owning engineer
/// and other interested agents (e.g., TE) wake immediately instead of polling.
/// </summary>
public record PrApprovedMessage : AgentMessage
{
    public required int PrNumber { get; init; }
    public required string PrTitle { get; init; }
    public required string ApproverAgent { get; init; }
}

/// <summary>
/// Sent by the TE when tests have been added for a PR, so the PM can immediately
/// proceed to final review instead of polling for the tests-added label.
/// </summary>
public record TestsCompletedMessage : AgentMessage
{
    public required int PrNumber { get; init; }
    public required string PrTitle { get; init; }
}

/// <summary>
/// Broadcast when a PR is successfully merged. Eliminates per-tick
/// ListMergedAsync polling in downstream agents (PM, TE, SE workers).
/// Published with ToAgentId="*" (broadcast).
/// </summary>
public record PrMergedMessage : AgentMessage
{
    public required int PrNumber { get; init; }
    public required string PrTitle { get; init; }
    public required string HeadBranch { get; init; }
    /// <summary>Issue number linked to this PR, if any.</summary>
    public int? LinkedIssueNumber { get; init; }
}

/// <summary>
/// Queued rework item for an engineer to address reviewer feedback on a PR.
/// </summary>
public record ReworkItem(int PrNumber, string PrTitle, string Feedback, string Reviewer);

/// <summary>
/// Broadcast on the in-process message bus when an engineer claims a work item.
/// All engineer agents subscribe and record the claim in <see cref="ClaimedTaskRegistry"/>
/// so no other agent attempts to claim the same task. Published with ToAgentId="*" (broadcast).
/// </summary>
public record TaskClaimedMessage : AgentMessage
{
    public required int IssueNumber { get; init; }
    public required string IssueTitle { get; init; }
}

/// <summary>
/// Sent by the PM to the PE after all User Story Issues have been created from the PMSpec.
/// Signals that the PE can begin building the engineering plan.
/// </summary>
public record PlanningCompleteMessage : AgentMessage
{
    /// <summary>Total number of User Story Issues created.</summary>
    public required int IssueCount { get; init; }
}

/// <summary>
/// Sent by the PE (or PM) to an engineer to assign them a GitHub Issue to work on.
/// The engineer is responsible for reading the Issue and creating their own PR.
/// </summary>
public record IssueAssignmentMessage : AgentMessage
{
    public required int IssueNumber { get; init; }
    public required string IssueTitle { get; init; }
    public required string Complexity { get; init; }
    public string? IssueUrl { get; init; }
}

/// <summary>
/// Sent by an engineer to the PM when they need clarification on an Issue
/// before starting or while working. The PM should respond on the GitHub Issue.
/// </summary>
public record ClarificationRequestMessage : AgentMessage
{
    public required int IssueNumber { get; init; }
    public required string Question { get; init; }
}

/// <summary>
/// Sent by the PM back to the engineer after answering a clarification question on the Issue.
/// </summary>
public record ClarificationResponseMessage : AgentMessage
{
    public required int IssueNumber { get; init; }
    public required string Response { get; init; }
}

/// <summary>
/// Sent by the PE Leader when all issues are closed and the project is complete.
/// Agents should delete their local workspaces when they receive this message.
/// </summary>
public record WorkspaceCleanupMessage : AgentMessage
{
    public required string Reason { get; init; }
}

// === SME Agent Messages ===
// NOTE: SpawnSmeAgentMessage and SmeResultMessage were removed as dead letters
// (no publishers or no subscribers). See SimplificationRecommendations.md §1.4.

/// <summary>
/// The PM's proposal for the optimal agent team for a project.
/// </summary>
public record TeamCompositionProposal
{
    public required string ProjectSummary { get; init; }
    public required List<BuiltInAgentRequest> BuiltInAgents { get; init; }
    public required List<string> ExistingTemplateIds { get; init; }
    public required List<Configuration.SMEAgentDefinition> NewSmeAgents { get; init; }
    public required string Rationale { get; init; }
}

/// <summary>
/// A request for a specific built-in agent role as part of team composition.
/// </summary>
public record BuiltInAgentRequest
{
    public required AgentRole Role { get; init; }
    public required int Count { get; init; }
    public string? Justification { get; init; }

    /// <summary>
    /// Optional role description override that the PM assigns to this agent role
    /// during team composition. Stored in <see cref="Configuration.AgentConfig.RoleDescription"/>
    /// and injected as [ROLE CUSTOMIZATION] into every system prompt for this role.
    /// </summary>
    public string? RoleDescription { get; init; }
}
