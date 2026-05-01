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
    public int NonCompliantCount { get; init; }
    public List<string> NonCompliantAgentIds { get; init; } = [];
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
    private readonly WorkflowStateMachine _workflow;
    private readonly ILogger<HealthMonitor> _logger;
    private readonly LimitsConfig _limits;
    private readonly ConcurrentDictionary<string, DateTime> _workingStartTimes = new();
    private Timer? _timer;
    private bool _disposed;

    public HealthMonitor(
        AgentRegistry registry,
        WorkflowStateMachine workflow,
        ILogger<HealthMonitor> logger,
        IOptions<LimitsConfig> limitsOptions)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
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
        var nonCompliantIds = new List<string>();

        foreach (var agent in agents)
        {
            if (agent.CurrentDiagnostic is { IsCompliant: false })
                nonCompliantIds.Add(agent.Identity.Id);
        }

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
            LongestRunningAgentId = longestAgentId,
            NonCompliantCount = nonCompliantIds.Count,
            NonCompliantAgentIds = nonCompliantIds
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

            // Auto-detect signals from agent states and try to advance phases
            AutoDetectSignals();
            if (_workflow.TryAdvancePhase(out var blocker))
            {
                _logger.LogInformation("Phase auto-advanced. New phase: {Phase}", _workflow.CurrentPhase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check.");
        }
    }

    /// <summary>
    /// Infer workflow signals from the actual state of agents rather than
    /// requiring agents to explicitly call Signal(). Checks status reasons
    /// and role states to detect when milestones have been reached.
    /// </summary>
    private void AutoDetectSignals()
    {
        var agents = _registry.GetAllAgents();

        // Helper to check if any agent of a role has a status reason matching a pattern
        bool HasReasonContaining(AgentRole role, params string[] keywords) =>
            agents.Where(a => a.Identity.Role == role)
                  .Any(a => keywords.Any(k =>
                      (a.StatusReason ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)));

        // Helper: is a downstream agent working? If so, predecessor phases are implicitly done.
        bool IsDownstreamWorking(AgentRole role) =>
            agents.Where(a => a.Identity.Role == role)
                  .Any(a => a.Status == AgentStatus.Working &&
                       !(a.StatusReason ?? "").Contains("Waiting for", StringComparison.OrdinalIgnoreCase));

        var phase = _workflow.CurrentPhase;

        // --- Research phase signals ---
        // Infer research done if: Researcher status says complete/monitoring, OR
        // the PM is writing PMSpec (downstream activity proves research merged),
        // OR the Architect is already working on architecture
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete))
        {
            bool researchDone =
                HasReasonContaining(AgentRole.Researcher, "research complete", "monitoring", "complete", "waiting for research directives") ||
                HasReasonContaining(AgentRole.ProgramManager, "pmspec", "pm spec", "specification", "writing spec", "kickoff complete", "monitoring team") ||
                IsDownstreamWorking(AgentRole.Architect);

            if (researchDone)
            {
                SignalIfNew(WorkflowStateMachine.Signals.ResearchDocReady);
                SignalIfNew(WorkflowStateMachine.Signals.ResearchComplete);
            }
        }

        // --- Architecture phase signals ---
        // Infer architecture done if: Architect says complete, OR PE is actively working
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete))
        {
            bool archDone =
                HasReasonContaining(AgentRole.Architect, "architecture complete", "monitoring pr", "complete") ||
                IsDownstreamWorking(AgentRole.SoftwareEngineer);

            if (archDone)
            {
                SignalIfNew(WorkflowStateMachine.Signals.ArchitectureDocReady);
                SignalIfNew(WorkflowStateMachine.Signals.ArchitectureComplete);
            }
        }

        // --- Engineering Planning signals ---
        // Infer plan ready if: SE leader has created the plan (specific plan-complete phrases),
        // OR the SE leader is actively implementing tasks (proves plan exists),
        // OR any SME engineers have been spawned (proves plan exists and is being executed).
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.EngineeringPlanReady))
        {
            // Check SE leader (Rank 0) for specific plan-complete or active-work indicators
            bool HasLeaderReasonContaining(params string[] keywords) =>
                agents.Where(a => a.Identity.Role == AgentRole.SoftwareEngineer && a.Identity.Rank == 0)
                      .Any(a => keywords.Any(k =>
                          (a.StatusReason ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)));

            bool planReady =
                HasLeaderReasonContaining(
                    "engineering plan created", "plan complete", "tasks assigned",
                    "loaded", "tasks remaining", "orchestrating",
                    "implementing", "assigned task", "task done",
                    "tasks complete", "development loop", "working on task");

            // Durable condition: if any SME workers exist, the plan was definitely created
            if (!planReady)
            {
                planReady = agents.Any(a => a.Identity.Role == AgentRole.SoftwareEngineer && a.Identity.Rank > 0);
            }

            if (planReady)
            {
                SignalIfNew(WorkflowStateMachine.Signals.EngineeringPlanReady);
                SignalIfNew(WorkflowStateMachine.Signals.SoftwareEngineerReady);
            }
        }

        // --- Parallel Development signals ---
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.AllEngineeringComplete))
        {
            // Check if SE leader reports engineering complete (SE may do all coding itself)
            bool seComplete = HasReasonContaining(AgentRole.SoftwareEngineer,
                "engineering complete", "all tasks complete", "all tasks done", "integration pr");

            var engineers = agents.Where(a => a.Identity.Role is AgentRole.SoftwareEngineer && a.Identity.Rank > 0).ToList();
            bool engineersDone = engineers.Count > 0 && engineers.All(a =>
                a.Status is AgentStatus.Online or AgentStatus.Idle &&
                ((a.StatusReason ?? "").Contains("complete", StringComparison.OrdinalIgnoreCase) ||
                 (a.StatusReason ?? "").Contains("no task", StringComparison.OrdinalIgnoreCase) ||
                 (a.StatusReason ?? "").Contains("no assigned", StringComparison.OrdinalIgnoreCase)));

            if (seComplete || engineersDone)
            {
                SignalIfNew(WorkflowStateMachine.Signals.AllEngineeringComplete);
            }
        }

        // --- Testing signals ---
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.TestCoverageMet))
        {
            if (HasReasonContaining(AgentRole.TestEngineer, "all tested", "coverage met", "tests complete"))
            {
                SignalIfNew(WorkflowStateMachine.Signals.TestCoverageMet);
            }
        }

        // --- Review signals ---
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.AllReviewsApproved))
        {
            if (HasReasonContaining(AgentRole.ProgramManager, "all approved", "reviews complete", "all merged"))
            {
                SignalIfNew(WorkflowStateMachine.Signals.AllReviewsApproved);
            }
        }
    }

    private void SignalIfNew(string signal)
    {
        if (!_workflow.HasSignal(signal))
        {
            _workflow.Signal(signal);
            _logger.LogInformation("Auto-detected signal: {Signal}", signal);
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
