using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
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
    public string? StatusReason { get; init; }
    public string ActiveModel { get; init; } = "";
    public DateTime LastStatusChange { get; set; } = DateTime.UtcNow;
    public int ErrorCount { get; init; }
}

public sealed class DashboardDataService : BackgroundService
{
    private readonly AgentRegistry _registry;
    private readonly HealthMonitor _healthMonitor;
    private readonly DeadlockDetector _deadlockDetector;
    private readonly ModelRegistry _modelRegistry;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly ILogger<DashboardDataService> _logger;

    private readonly Dictionary<string, AgentSnapshot> _agentCache = new();
    private readonly Dictionary<string, List<AgentLogEntry>> _agentErrors = new();
    private readonly Dictionary<string, IAgent> _trackedAgents = new();
    private readonly object _cacheLock = new();

    private AgentHealthSnapshot? _lastHealthSnapshot;

    public DashboardDataService(
        AgentRegistry registry,
        HealthMonitor healthMonitor,
        DeadlockDetector deadlockDetector,
        ModelRegistry modelRegistry,
        IHubContext<AgentHub> hubContext,
        ILogger<DashboardDataService> logger)
    {
        _registry = registry;
        _healthMonitor = healthMonitor;
        _deadlockDetector = deadlockDetector;
        _modelRegistry = modelRegistry;
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

    /// <summary>Gets error/warning log entries for a specific agent.</summary>
    public IReadOnlyList<AgentLogEntry> GetAgentErrors(string agentId)
    {
        lock (_cacheLock)
        {
            return _agentErrors.TryGetValue(agentId, out var errors)
                ? errors.ToList()
                : [];
        }
    }

    /// <summary>Clears tracked errors for a specific agent and updates the snapshot.</summary>
    public void ClearAgentErrors(string agentId)
    {
        lock (_cacheLock)
        {
            if (_agentErrors.ContainsKey(agentId))
                _agentErrors[agentId].Clear();

            if (_agentCache.TryGetValue(agentId, out var snapshot))
                _agentCache[agentId] = snapshot with { ErrorCount = 0 };
        }

        if (_trackedAgents.TryGetValue(agentId, out var agent))
            agent.ClearErrors();

        _ = _hubContext.Clients.All.SendAsync("AgentErrorsCleared", agentId);
        NotifyStateChanged();
    }

    /// <summary>Get the list of available model names for the dropdown.</summary>
    public IReadOnlyList<string> GetAvailableModels() => ModelRegistry.AvailableCopilotModels;

    /// <summary>Change the model for a specific agent at runtime.</summary>
    public void SetAgentModel(string agentId, string modelName)
    {
        _modelRegistry.SetAgentModelOverride(agentId, modelName);

        lock (_cacheLock)
        {
            if (_agentCache.TryGetValue(agentId, out var snapshot))
                _agentCache[agentId] = snapshot with { ActiveModel = modelName };
        }

        _ = _hubContext.Clients.All.SendAsync("AgentModelChanged", new { AgentId = agentId, Model = modelName });
        NotifyStateChanged();
    }

    private void SeedCache()
    {
        var agents = _registry.GetAllAgents();
        lock (_cacheLock)
        {
            foreach (var agent in agents)
            {
                _agentCache[agent.Identity.Id] = ToSnapshot(agent);
                _trackedAgents[agent.Identity.Id] = agent;
                SubscribeToErrors(agent);
            }
        }
    }

    private void OnAgentRegistered(object? sender, AgentRegistryChangedEventArgs e)
    {
        var snapshot = ToSnapshot(e.Agent);
        lock (_cacheLock)
        {
            _agentCache[e.Agent.Identity.Id] = snapshot;
            _trackedAgents[e.Agent.Identity.Id] = e.Agent;
            SubscribeToErrors(e.Agent);
        }

        _ = _hubContext.Clients.All.SendAsync("AgentRegistered", snapshot);
        NotifyStateChanged();
    }

    private void OnAgentUnregistered(object? sender, AgentRegistryChangedEventArgs e)
    {
        lock (_cacheLock)
        {
            _agentCache.Remove(e.Agent.Identity.Id);
            _agentErrors.Remove(e.Agent.Identity.Id);
            _trackedAgents.Remove(e.Agent.Identity.Id);
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
                // Only reset the timer when the status enum actually changes,
                // not when just the status reason text updates
                var statusChangeTime = e.OldStatus != e.NewStatus
                    ? DateTime.UtcNow
                    : cached.LastStatusChange;

                _agentCache[e.Agent.Id] = cached with
                {
                    Status = e.NewStatus,
                    StatusReason = e.Reason,
                    LastStatusChange = statusChangeTime
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

    private void SubscribeToErrors(IAgent agent)
    {
        agent.ErrorsChanged += OnAgentErrorsChanged;
    }

    private void OnAgentErrorsChanged(object? sender, EventArgs e)
    {
        if (sender is not IAgent agent) return;

        var agentId = agent.Identity.Id;
        var currentErrors = agent.RecentErrors;

        lock (_cacheLock)
        {
            _agentErrors[agentId] = currentErrors.ToList();

            if (_agentCache.TryGetValue(agentId, out var snapshot))
            {
                _agentCache[agentId] = snapshot with { ErrorCount = currentErrors.Count };
            }
        }

        _ = _hubContext.Clients.All.SendAsync("AgentErrorsUpdated", new
        {
            AgentId = agentId,
            ErrorCount = currentErrors.Count
        });
        NotifyStateChanged();
    }

    private AgentSnapshot ToSnapshot(IAgent agent) => new()
    {
        Id = agent.Identity.Id,
        DisplayName = agent.Identity.DisplayName,
        Role = agent.Identity.Role,
        ModelTier = agent.Identity.ModelTier,
        Status = agent.Status,
        StatusReason = agent.StatusReason,
        CreatedAt = agent.Identity.CreatedAt,
        AssignedPullRequest = agent.Identity.AssignedPullRequest,
        ActiveModel = _modelRegistry.GetEffectiveModel(agent.Identity.Id),
        LastStatusChange = DateTime.UtcNow,
        ErrorCount = agent.RecentErrors.Count
    };

    // Observable event for Blazor components to subscribe to for re-rendering
    public event Action? OnChange;
    private void NotifyStateChanged() => OnChange?.Invoke();
}
