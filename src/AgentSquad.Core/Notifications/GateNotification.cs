namespace AgentSquad.Core.Notifications;

/// <summary>
/// Represents a notification about a human gate requiring attention.
/// </summary>
public record GateNotification
{
    public required string Id { get; init; }
    public required string GateId { get; init; }
    public required string GateName { get; init; }
    public required string Context { get; init; }
    public int? ResourceNumber { get; init; }
    public string? ResourceType { get; init; } // "PR" or "Issue"
    public string? GitHubUrl { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
    public bool IsResolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
