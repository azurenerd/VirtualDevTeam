namespace VirtualDevTeam.Core.Agents.Reasoning;

/// <summary>
/// Captures and stores agent reasoning events for observability.
/// Humans can monitor the agent's thought process in real-time via the dashboard
/// and intervene (redirect, provide context, or stop) if the agent goes off-plan.
/// </summary>
public interface IAgentReasoningLog
{
    /// <summary>Log a reasoning event for an agent.</summary>
    void Log(AgentReasoningEvent evt);

    /// <summary>Get all events for a specific agent, ordered by timestamp.</summary>
    IReadOnlyList<AgentReasoningEvent> GetEvents(string agentId);

    /// <summary>Get events for a specific agent since a given timestamp (for polling).</summary>
    IReadOnlyList<AgentReasoningEvent> GetEventsSince(string agentId, DateTime since);

    /// <summary>Get the latest events across all agents (for dashboard overview).</summary>
    IReadOnlyList<AgentReasoningEvent> GetRecentEvents(int count = 50);

    /// <summary>Get all agents that have reasoning events.</summary>
    IReadOnlyList<string> GetAgentIds();

    /// <summary>Clear all events for a specific agent.</summary>
    void Clear(string agentId);

    /// <summary>Clear all events.</summary>
    void ClearAll();

    /// <summary>Fired when a new reasoning event is logged. Subscribe for real-time updates.</summary>
    event Action<AgentReasoningEvent>? OnEvent;
}
