using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Dashboard.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// <see cref="IStrategyBroadcaster"/> implementation that pushes strategy
/// lifecycle events to dashboard clients over <see cref="AgentHub"/> using a
/// single topic, <c>"StrategyEvent"</c>.
/// </summary>
public sealed class SignalRStrategyBroadcaster : IStrategyBroadcaster
{
    private readonly IHubContext<AgentHub> _hub;

    public SignalRStrategyBroadcaster(IHubContext<AgentHub> hub)
    {
        _hub = hub;
    }

    public async Task BroadcastAsync(string eventName, object payload, CancellationToken ct)
    {
        await _hub.Clients.All.SendAsync("StrategyEvent", eventName, payload, ct).ConfigureAwait(false);
    }
}
