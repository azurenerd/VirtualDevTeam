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
