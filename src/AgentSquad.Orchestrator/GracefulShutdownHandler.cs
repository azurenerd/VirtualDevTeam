using AgentSquad.Core.Agents;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Orchestrator;

/// <summary>
/// Handles graceful shutdown — saves all agent states and stops agents cleanly.
/// Hooks into the host lifetime and Console.CancelKeyPress for Ctrl+C handling.
/// </summary>
public class GracefulShutdownHandler : IHostedService, IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly AgentStateStore _stateStore;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GracefulShutdownHandler> _logger;
    private readonly TimeSpan _shutdownTimeout;
    private CancellationTokenSource? _shutdownCts;
    private bool _disposed;

    public GracefulShutdownHandler(
        AgentRegistry registry,
        AgentStateStore stateStore,
        IHostApplicationLifetime lifetime,
        ILogger<GracefulShutdownHandler> logger,
        TimeSpan? shutdownTimeout = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(30);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Console.CancelKeyPress += OnCancelKeyPress;
        _lifetime.ApplicationStopping.Register(OnApplicationStopping);

        _logger.LogInformation("Graceful shutdown handler initialized (timeout: {Timeout}s)", _shutdownTimeout.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Graceful shutdown initiated — saving agent states...");

        var agents = _registry.GetAllAgents();
        var agentCount = agents.Count;

        if (agentCount == 0)
        {
            _logger.LogInformation("No active agents to shut down");
            return;
        }

        _logger.LogInformation("Shutting down {Count} agent(s)...", agentCount);

        // Step 1: Signal all agents to stop
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_shutdownTimeout);

        var stopTasks = new List<Task>();
        foreach (var agent in agents)
        {
            stopTasks.Add(StopAgentSafelyAsync(agent, timeoutCts.Token));
        }

        // Step 2: Wait for agents to finish current operations (with timeout)
        try
        {
            await Task.WhenAll(stopTasks);
            _logger.LogInformation("All agents stopped successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Shutdown timeout reached — some agents may not have stopped cleanly");
        }

        // Step 3: Save each agent's state to AgentStateStore
        var saveTasks = new List<Task>();
        foreach (var agent in agents)
        {
            saveTasks.Add(SaveAgentStateSafelyAsync(agent, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(saveTasks);
            _logger.LogInformation("All agent states saved to store");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errors occurred while saving agent states");
        }

        // Step 4: Log final status
        await LogFinalStatusAsync(agents, CancellationToken.None);

        _logger.LogInformation("Graceful shutdown complete — {Count} agent(s) processed", agentCount);
    }

    private async Task StopAgentSafelyAsync(IAgent agent, CancellationToken ct)
    {
        try
        {
            if (agent.Status is AgentStatus.Terminated or AgentStatus.Offline)
            {
                _logger.LogDebug("Agent {Id} already stopped ({Status})", agent.Identity.Id, agent.Status);
                return;
            }

            _logger.LogDebug("Stopping agent {Id} ({Name})...", agent.Identity.Id, agent.Identity.DisplayName);
            await agent.StopAsync(ct);
            _logger.LogDebug("Agent {Id} stopped", agent.Identity.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout stopping agent {Id} — forcing state save", agent.Identity.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping agent {Id}", agent.Identity.Id);
        }
    }

    private async Task SaveAgentStateSafelyAsync(IAgent agent, CancellationToken ct)
    {
        try
        {
            await _stateStore.SaveCheckpointAsync(
                agentId: agent.Identity.Id,
                role: agent.Identity.Role.ToString(),
                status: agent.Status.ToString(),
                currentTask: agent.Identity.AssignedPullRequest,
                serializedState: null,
                ct);

            await _stateStore.LogActivityAsync(
                agent.Identity.Id,
                "shutdown",
                $"Agent state saved during graceful shutdown (status: {agent.Status})",
                ct);

            _logger.LogDebug("State saved for agent {Id}", agent.Identity.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save state for agent {Id}", agent.Identity.Id);
        }
    }

    private async Task LogFinalStatusAsync(IReadOnlyList<IAgent> agents, CancellationToken ct)
    {
        try
        {
            var statusGroups = agents
                .GroupBy(a => a.Status)
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();

            var summary = string.Join(", ", statusGroups);
            _logger.LogInformation("Final agent status — {Summary}", summary);

            foreach (var agent in agents)
            {
                await _stateStore.RecordMetricAsync(
                    agent.Identity.Id,
                    "shutdown_status",
                    (double)agent.Status,
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log final status metrics");
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        _logger.LogWarning("Ctrl+C received — initiating graceful shutdown...");
        e.Cancel = true; // Prevent immediate termination
        _lifetime.StopApplication();
    }

    private void OnApplicationStopping()
    {
        _logger.LogInformation("Application stopping signal received");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Console.CancelKeyPress -= OnCancelKeyPress;
        _shutdownCts?.Cancel();
        _shutdownCts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
