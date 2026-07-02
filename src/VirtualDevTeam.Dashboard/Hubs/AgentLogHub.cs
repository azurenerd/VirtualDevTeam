using Microsoft.AspNetCore.SignalR;

namespace VirtualDevTeam.Dashboard.Hubs;

/// <summary>
/// SignalR hub for streaming agent CLI log entries to subscribed Dashboard clients.
/// Each client subscribes to a specific agent by ID and receives real-time log entries.
/// </summary>
public sealed class AgentLogHub : Hub
{
    /// <summary>
    /// Subscribe to log entries for a specific agent.
    /// </summary>
    public async Task SubscribeToAgent(string agentId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"agent-log:{agentId}");
    }

    /// <summary>
    /// Unsubscribe from log entries for a specific agent.
    /// </summary>
    public async Task UnsubscribeFromAgent(string agentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"agent-log:{agentId}");
    }
}
