using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Orchestrator;

public class AgentRegistryChangedEventArgs : EventArgs
{
    public required IAgent Agent { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class AgentRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, IAgent> _agents = new();
    private readonly ILogger<AgentRegistry> _logger;
    private bool _disposed;

    public AgentRegistry(ILogger<AgentRegistry> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<AgentRegistryChangedEventArgs>? AgentRegistered;
    public event EventHandler<AgentRegistryChangedEventArgs>? AgentUnregistered;
    public event EventHandler<AgentStatusChangedEventArgs>? AgentStatusChanged;

    public async Task RegisterAsync(IAgent agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var agentId = agent.Identity.Id;

        if (!_agents.TryAdd(agentId, agent))
        {
            throw new InvalidOperationException($"Agent '{agentId}' is already registered.");
        }

        agent.StatusChanged += OnAgentStatusChanged;

        _logger.LogInformation("Agent '{AgentId}' ({Role}) registered.", agentId, agent.Identity.Role);

        AgentRegistered?.Invoke(this, new AgentRegistryChangedEventArgs { Agent = agent });

        await Task.CompletedTask;
    }

    public async Task UnregisterAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_agents.TryRemove(agentId, out var agent))
        {
            _logger.LogWarning("Attempted to unregister unknown agent '{AgentId}'.", agentId);
            return;
        }

        agent.StatusChanged -= OnAgentStatusChanged;

        if (agent.Status is not (AgentStatus.Offline or AgentStatus.Terminated))
        {
            try
            {
                await agent.StopAsync(ct);
                _logger.LogInformation("Agent '{AgentId}' stopped during unregistration.", agentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping agent '{AgentId}' during unregistration.", agentId);
            }
        }

        _logger.LogInformation("Agent '{AgentId}' unregistered.", agentId);

        AgentUnregistered?.Invoke(this, new AgentRegistryChangedEventArgs { Agent = agent });
    }

    public IAgent? GetAgent(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        _agents.TryGetValue(agentId, out var agent);
        return agent;
    }

    public IReadOnlyList<IAgent> GetAllAgents() =>
        _agents.Values.ToList().AsReadOnly();

    public IReadOnlyList<IAgent> GetAgentsByRole(AgentRole role) =>
        _agents.Values.Where(a => a.Identity.Role == role).ToList().AsReadOnly();

    public IReadOnlyList<IAgent> GetAgentsByStatus(AgentStatus status) =>
        _agents.Values.Where(a => a.Status == status).ToList().AsReadOnly();

    public int GetActiveAgentCount() =>
        _agents.Values.Count(a => a.Status is not (AgentStatus.Terminated or AgentStatus.Offline));

    private void OnAgentStatusChanged(object? sender, AgentStatusChangedEventArgs e)
    {
        _logger.LogDebug(
            "Agent '{AgentId}' status changed: {OldStatus} → {NewStatus}.",
            e.Agent.Id, e.OldStatus, e.NewStatus);

        AgentStatusChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var agent in _agents.Values)
        {
            agent.StatusChanged -= OnAgentStatusChanged;
        }

        _agents.Clear();

        _logger.LogInformation("AgentRegistry disposed. All agents removed.");
    }
}
