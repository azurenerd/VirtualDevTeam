using AgentSquad.Dashboard.Services;
using Microsoft.AspNetCore.SignalR;

namespace AgentSquad.Dashboard.Hubs;

public class AgentHub : Hub
{
    private readonly DashboardDataService _dataService;

    public AgentHub(DashboardDataService dataService)
    {
        _dataService = dataService;
    }

    public async Task RequestAgentDetails(string agentId)
    {
        var agent = _dataService.GetAgentSnapshot(agentId);
        if (agent is not null)
        {
            await Clients.Caller.SendAsync("AgentDetails", agent);
        }
    }

    public async Task RequestHealthSnapshot()
    {
        var snapshot = _dataService.GetCurrentHealthSnapshot();
        await Clients.Caller.SendAsync("HealthUpdate", snapshot);
    }
}
