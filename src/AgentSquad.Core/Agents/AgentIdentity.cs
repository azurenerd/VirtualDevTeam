namespace AgentSquad.Core.Agents;

public record AgentIdentity
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AgentRole Role { get; init; }
    public required string ModelTier { get; init; }

    /// <summary>
    /// Rank within the role pool. 0 = original/leader, 1+ = additional/worker.
    /// Used for leader election among multiple SoftwareEngineer agents.
    /// </summary>
    public int Rank { get; init; } = 0;

    /// <summary>
    /// For custom agents (Role == Custom), the configuration name that maps to
    /// the corresponding <see cref="Configuration.CustomAgentConfig"/> entry.
    /// Null/empty for built-in roles.
    /// </summary>
    public string? CustomAgentName { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? AssignedPullRequest { get; set; }
}
