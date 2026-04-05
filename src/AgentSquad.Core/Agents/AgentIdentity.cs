namespace AgentSquad.Core.Agents;

public record AgentIdentity
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AgentRole Role { get; init; }
    public required string ModelTier { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? AssignedPullRequest { get; set; }
}
