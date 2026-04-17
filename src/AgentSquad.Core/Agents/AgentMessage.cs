namespace AgentSquad.Core.Agents;

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

public record HelpRequestMessage : AgentMessage
{
    public required string IssueTitle { get; init; }
    public required string IssueBody { get; init; }
    public required bool IsBlocker { get; init; }
}

public record ResourceRequestMessage : AgentMessage
{
    public required AgentRole RequestedRole { get; init; }
    public required string Justification { get; init; }
    public required int CurrentTeamSize { get; init; }
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
/// Queued rework item for an engineer to address reviewer feedback on a PR.
/// </summary>
public record ReworkItem(int PrNumber, string PrTitle, string Feedback, string Reviewer);

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

/// <summary>
/// Sent by PM or PE to request spawning an SME agent.
/// Triggers human gate approval before the agent is created.
/// </summary>
public record SpawnSmeAgentMessage : AgentMessage
{
    /// <summary>Template ID from the SME catalog, or null for AI-generated definitions.</summary>
    public string? DefinitionId { get; init; }

    /// <summary>Full definition for AI-generated or custom SME agents.</summary>
    public Configuration.SMEAgentDefinition? CustomDefinition { get; init; }

    /// <summary>Optional issue number to assign to the new agent upon spawn.</summary>
    public int? AssignToIssue { get; init; }

    /// <summary>Why this SME agent is needed.</summary>
    public string Justification { get; init; } = "";
}

/// <summary>
/// Sent by an SME agent to report its findings after completing a task.
/// PM or PE can use these results to inform project decisions.
/// </summary>
public record SmeResultMessage : AgentMessage
{
    /// <summary>The SME definition ID that produced this result.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>Brief summary of the task that was performed.</summary>
    public required string TaskSummary { get; init; }

    /// <summary>Detailed findings from the SME analysis.</summary>
    public required string Findings { get; init; }

    /// <summary>Actionable recommendations based on findings.</summary>
    public List<string> Recommendations { get; init; } = [];

    /// <summary>Related GitHub issue number, if applicable.</summary>
    public int? RelatedIssueNumber { get; init; }
}

/// <summary>
/// Sent by the PM after analyzing project documents to propose the full team composition.
/// Contains built-in agent counts, SME template activations, and new SME definitions.
/// </summary>
public record TeamCompositionProposalMessage : AgentMessage
{
    public required TeamCompositionProposal Proposal { get; init; }
}

/// <summary>
/// Sent by the human director (via dashboard) to approve/modify the PM's team composition.
/// </summary>
public record TeamCompositionApprovalMessage : AgentMessage
{
    public required TeamCompositionProposal ApprovedProposal { get; init; }
    public List<string> RejectedAgentIds { get; init; } = [];
    public string? DirectorNotes { get; init; }
}

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
