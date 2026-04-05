using AgentSquad.Core.Agents;

namespace AgentSquad.Agents;

internal class AgentTracking
{
    public required string AgentId { get; set; }
    public required AgentRole Role { get; set; }
    public AgentStatus LastKnownStatus { get; set; } = AgentStatus.Requested;
    public string? CurrentTask { get; set; }
    public DateTime LastStatusUpdate { get; set; } = DateTime.UtcNow;
    public int PrsCompleted { get; set; }
    public int IssuesCreated { get; set; }
}
