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
    public required string PullRequestUrl { get; init; }
    public required string ReviewType { get; init; }
}
