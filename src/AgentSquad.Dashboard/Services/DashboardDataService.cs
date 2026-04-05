using AgentSquad.Core.Agents;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Orchestrator;
using Microsoft.AspNetCore.SignalR;

namespace AgentSquad.Dashboard.Services;

public sealed record AgentSnapshot
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AgentRole Role { get; init; }
    public required string ModelTier { get; init; }
    public required AgentStatus Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? AssignedPullRequest { get; init; }
    public DateTime LastStatusChange { get; set; } = DateTime.UtcNow;
}

public sealed class DashboardDataService : BackgroundService
{
    private readonly AgentRegistry _registry;
    private readonly HealthMonitor _healthMonitor;
    private readonly DeadlockDetector _deadlockDetector;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly ILogger<DashboardDataService> _logger;

    private readonly Dictionary<string, AgentSnapshot> _agentCache = new();
    private readonly object _cacheLock = new();

    private AgentHealthSnapshot? _lastHealthSnapshot;

    public DashboardDataService(
        AgentRegistry registry,
        HealthMonitor healthMonitor,
        DeadlockDetector deadlockDetector,
        IHubContext<AgentHub> hubContext,
        ILogger<DashboardDataService> logger)
    {
        _registry = registry;
        _healthMonitor = healthMonitor;
        _deadlockDetector = deadlockDetector;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _registry.AgentRegistered += OnAgentRegistered;
        _registry.AgentUnregistered += OnAgentUnregistered;
        _registry.AgentStatusChanged += OnAgentStatusChanged;

        SeedCache();

        _logger.LogInformation("Dashboard data service started");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PushHealthSnapshot(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        finally
        {
            _registry.AgentRegistered -= OnAgentRegistered;
            _registry.AgentUnregistered -= OnAgentUnregistered;
            _registry.AgentStatusChanged -= OnAgentStatusChanged;
        }
    }

    public IReadOnlyList<AgentSnapshot> GetAllAgentSnapshots()
    {
        lock (_cacheLock)
        {
            return _agentCache.Values.ToList();
        }
    }

    public AgentSnapshot? GetAgentSnapshot(string agentId)
    {
        lock (_cacheLock)
        {
            return _agentCache.GetValueOrDefault(agentId);
        }
    }

    public AgentHealthSnapshot GetCurrentHealthSnapshot()
    {
        return _lastHealthSnapshot ?? _healthMonitor.GetSnapshot();
    }

    public bool HasDeadlock(out List<string>? cycle)
    {
        return _deadlockDetector.HasDeadlock(out cycle);
    }

    private void SeedCache()
    {
        var agents = _registry.GetAllAgents();
        lock (_cacheLock)
        {
            foreach (var agent in agents)
            {
                _agentCache[agent.Identity.Id] = ToSnapshot(agent);
            }
        }
    }

    private void OnAgentRegistered(object? sender, AgentRegistryChangedEventArgs e)
    {
        var snapshot = ToSnapshot(e.Agent);
        lock (_cacheLock)
        {
            _agentCache[e.Agent.Identity.Id] = snapshot;
        }

        _ = _hubContext.Clients.All.SendAsync("AgentRegistered", snapshot);
        NotifyStateChanged();
    }

    private void OnAgentUnregistered(object? sender, AgentRegistryChangedEventArgs e)
    {
        lock (_cacheLock)
        {
            _agentCache.Remove(e.Agent.Identity.Id);
        }

        _ = _hubContext.Clients.All.SendAsync("AgentUnregistered", e.Agent.Identity.Id);
        NotifyStateChanged();
    }

    private void OnAgentStatusChanged(object? sender, AgentStatusChangedEventArgs e)
    {
        lock (_cacheLock)
        {
            if (_agentCache.TryGetValue(e.Agent.Id, out var cached))
            {
                _agentCache[e.Agent.Id] = cached with
                {
                    Status = e.NewStatus,
                    LastStatusChange = DateTime.UtcNow
                };
            }
        }

        _ = _hubContext.Clients.All.SendAsync("AgentStatusChanged", new
        {
            AgentId = e.Agent.Id,
            OldStatus = e.OldStatus.ToString(),
            NewStatus = e.NewStatus.ToString(),
            e.Reason
        });
        NotifyStateChanged();
    }

    private async Task PushHealthSnapshot(CancellationToken ct)
    {
        try
        {
            _lastHealthSnapshot = _healthMonitor.GetSnapshot();
            await _hubContext.Clients.All.SendAsync("HealthUpdate", _lastHealthSnapshot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push health snapshot");
        }
    }

    private static AgentSnapshot ToSnapshot(IAgent agent) => new()
    {
        Id = agent.Identity.Id,
        DisplayName = agent.Identity.DisplayName,
        Role = agent.Identity.Role,
        ModelTier = agent.Identity.ModelTier,
        Status = agent.Status,
        CreatedAt = agent.Identity.CreatedAt,
        AssignedPullRequest = agent.Identity.AssignedPullRequest,
        LastStatusChange = DateTime.UtcNow
    };

    // Observable event for Blazor components to subscribe to for re-rendering
    public event Action? OnChange;
    private void NotifyStateChanged() => OnChange?.Invoke();
}
