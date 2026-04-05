using System.Collections.Concurrent;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Orchestrator;

public class AgentHealthSnapshot
{
    public required Dictionary<AgentStatus, int> StatusCounts { get; init; }
    public int TotalAgents { get; init; }
    public TimeSpan? LongestRunningTask { get; init; }
    public string? LongestRunningAgentId { get; init; }
}

public class AgentStuckEventArgs : EventArgs
{
    public required string AgentId { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? CurrentTask { get; init; }
}

public class HealthMonitor : IHostedService, IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly ILogger<HealthMonitor> _logger;
    private readonly LimitsConfig _limits;
    private readonly ConcurrentDictionary<string, DateTime> _workingStartTimes = new();
    private Timer? _timer;
    private bool _disposed;

    public HealthMonitor(
        AgentRegistry registry,
        ILogger<HealthMonitor> logger,
        IOptions<LimitsConfig> limitsOptions)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));

        _registry.AgentStatusChanged += OnAgentStatusChanged;
    }

    public event EventHandler<AgentStuckEventArgs>? AgentStuck;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "HealthMonitor starting. Check interval: {Interval}s, timeout: {Timeout}m.",
            _limits.GitHubPollIntervalSeconds, _limits.AgentTimeoutMinutes);

        var interval = TimeSpan.FromSeconds(
            _limits.GitHubPollIntervalSeconds > 0 ? _limits.GitHubPollIntervalSeconds : 30);

        _timer = new Timer(CheckHealth, null, interval, interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HealthMonitor stopping.");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public AgentHealthSnapshot GetSnapshot()
    {
        var agents = _registry.GetAllAgents();
        var statusCounts = agents
            .GroupBy(a => a.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        TimeSpan? longestRunning = null;
        string? longestAgentId = null;
        var now = DateTime.UtcNow;

        foreach (var kvp in _workingStartTimes)
        {
            var duration = now - kvp.Value;
            if (longestRunning is null || duration > longestRunning)
            {
                longestRunning = duration;
                longestAgentId = kvp.Key;
            }
        }

        return new AgentHealthSnapshot
        {
            StatusCounts = statusCounts,
            TotalAgents = agents.Count,
            LongestRunningTask = longestRunning,
            LongestRunningAgentId = longestAgentId
        };
    }

    private void CheckHealth(object? state)
    {
        try
        {
            var timeout = TimeSpan.FromMinutes(
                _limits.AgentTimeoutMinutes > 0 ? _limits.AgentTimeoutMinutes : 15);
            var now = DateTime.UtcNow;

            foreach (var kvp in _workingStartTimes)
            {
                var duration = now - kvp.Value;
                if (duration > timeout)
                {
                    var agent = _registry.GetAgent(kvp.Key);
                    if (agent is not null && agent.Status == AgentStatus.Working)
                    {
                        _logger.LogWarning(
                            "Agent '{AgentId}' appears stuck. Working for {Duration}.",
                            kvp.Key, duration);

                        AgentStuck?.Invoke(this, new AgentStuckEventArgs
                        {
                            AgentId = kvp.Key,
                            Duration = duration,
                            CurrentTask = agent.Identity.AssignedPullRequest
                        });
                    }
                    else
                    {
                        // Agent is no longer Working — clean up stale entry
                        _workingStartTimes.TryRemove(kvp.Key, out _);
                    }
                }
            }

            var snapshot = GetSnapshot();
            _logger.LogDebug(
                "Health check: {Total} agents, {Active} active, longest task: {Longest}.",
                snapshot.TotalAgents,
                snapshot.StatusCounts.Where(kv =>
                    kv.Key is not (AgentStatus.Terminated or AgentStatus.Offline))
                    .Sum(kv => kv.Value),
                snapshot.LongestRunningTask?.ToString() ?? "none");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check.");
        }
    }

    private void OnAgentStatusChanged(object? sender, AgentStatusChangedEventArgs e)
    {
        var agentId = e.Agent.Id;

        if (e.NewStatus == AgentStatus.Working)
        {
            _workingStartTimes[agentId] = DateTime.UtcNow;
        }
        else
        {
            _workingStartTimes.TryRemove(agentId, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        _registry.AgentStatusChanged -= OnAgentStatusChanged;
    }
}
