using System.Collections.Concurrent;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Notifications;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Scenarios;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Orchestrator;

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
    private readonly IMessageBus _messageBus;
    private readonly ILogger<HealthMonitor> _logger;
    private readonly LimitsConfig _limits;
    private readonly FlowMonitorPersistence? _flowMonitorPersistence;
    private readonly IOptionsMonitor<FlowMonitorConfig>? _flowMonitorConfig;
    private readonly GateNotificationService? _notifications;
    private readonly ConcurrentDictionary<string, DateTime> _workingStartTimes = new();
    private readonly List<IDisposable> _busSubscriptions = [];
    private Timer? _timer;
    private bool _disposed;

    // T1.8: "Who watches the watcher" — track whether we've already alerted on a stale
    // FlowMonitor heartbeat so we don't spam the notifications page every poll cycle.
    // Reset to false once the heartbeat recovers.
    private bool _flowMonitorLivenessAlertSent;

    // post-run3-stronger-eng-complete-guard: platform services for the hardened
    // engineering-complete check. Optional defaults preserve constructor compatibility
    // with tests + standalone Dashboard host that don't wire them.
    private readonly VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService? _pullRequestService;
    private readonly VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService? _workItemService;

    // healthmon-false-research-complete: hardened doc-signal heuristic dependencies.
    // - _repoContent: used to verify the artifact actually exists on the working branch
    //   before firing research.doc.ready / architecture.doc.ready.
    // - _runBranchProvider: provides the effective working branch for the platform check.
    // - _workflowProfile: provides the canonical doc paths (Research.md / Architecture.md
    //   under the run-scoped artifact base). Fallbacks to bare filenames at repo root.
    // - _healthMonitorConfig: kill-switch + cooldown for the auto-detect heuristic.
    // All optional — tests can pass null to disable the platform check entirely (causing
    // the heuristic to safely no-op rather than fire false-positive signals).
    private readonly IRepositoryContentService? _repoContent;
    private readonly IRunBranchProvider? _runBranchProvider;
    private readonly IWorkflowProfile? _workflowProfile;
    private readonly IOptionsMonitor<HealthMonitorConfig>? _healthMonitorConfig;

    // Daily prune: retention cleanup for activity_log + metrics tables.
    // Runs at most once per 24h inside the existing health-check timer loop.
    private readonly AgentStateStore? _stateStore;
    private readonly PipelineAssessmentStore? _assessmentStore;
    private DateTimeOffset _lastPruneAt = DateTimeOffset.MinValue;
    private const int DefaultRetentionDays = 30;

    // One-time startup cleanup for stale candidate worktrees
    private readonly VirtualDevTeam.Core.Strategies.GitWorktreeManager? _worktreeManager;
    private readonly VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig? _vdtConfig;
    private readonly IScenarioRegistry? _scenarioRegistry;
    private bool _startupCleanupDone;

    // Log activity + LLM call tracking for smarter stuck detection
    private readonly AgentCliLogService? _agentLogService;
    private readonly ActiveLlmCallTracker? _activeLlmTracker;
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(10);

    // Per-phase cooldown timestamps for the doc-existence platform check. Throttles to
    // at most one platform call per phase per HealthMonitorConfig.DocCheckCooldownSeconds
    // (default 60s). UTC ticks; 0 = never checked.
    private long _lastResearchDocCheckTicks;
    private long _lastArchitectureDocCheckTicks;

    // Cooldown timestamp for the AllEngineeringComplete hard platform check. Throttles
    // to once per 90s while in ParallelDevelopment to bound platform API cost.
    // Volatile.Read/Write for atomic access across health-tick threads. UTC ticks; 0 = never.
    // Companion to the inverted-gate fix for healthmon-engineering-complete-stuck-phase
    // (2026-05-12). Hard platform check is now PRIMARY; status-reason heuristic is the
    // early-exit optimization.
    private long _lastEngineeringCompleteCheckTicks;
    private const int EngineeringCompleteCheckCooldownSeconds = 90;

    // Tracks whether we have ever observed at least one engineering-task issue being
    // opened during this run. Prevents the false-positive case where plan generation
    // succeeded but task creation failed and the platform reports 0 open tasks not
    // because work is done but because work was never filed. Set by the hard check
    // when it sees engineering-task issues (open OR closed) for the run's branch.
    private bool _engineeringTaskEverObserved;

    public HealthMonitor(
        AgentRegistry registry,
        WorkflowStateMachine workflow,
        IMessageBus messageBus,
        ILogger<HealthMonitor> logger,
        IOptions<LimitsConfig> limitsOptions,
        FlowMonitorPersistence? flowMonitorPersistence = null,
        IOptionsMonitor<FlowMonitorConfig>? flowMonitorConfig = null,
        GateNotificationService? notifications = null,
        VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService? pullRequestService = null,
        VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService? workItemService = null,
        IRepositoryContentService? repoContent = null,
        IRunBranchProvider? runBranchProvider = null,
        IWorkflowProfile? workflowProfile = null,
        IOptionsMonitor<HealthMonitorConfig>? healthMonitorConfig = null,
        AgentStateStore? stateStore = null,
        PipelineAssessmentStore? assessmentStore = null,
        VirtualDevTeam.Core.Strategies.GitWorktreeManager? worktreeManager = null,
        IOptions<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>? vdtConfig = null,
        IScenarioRegistry? scenarioRegistry = null,
        AgentCliLogService? agentLogService = null,
        ActiveLlmCallTracker? activeLlmTracker = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
        _flowMonitorPersistence = flowMonitorPersistence;
        _flowMonitorConfig = flowMonitorConfig;
        _notifications = notifications;
        _pullRequestService = pullRequestService;
        _workItemService = workItemService;
        _repoContent = repoContent;
        _runBranchProvider = runBranchProvider;
        _workflowProfile = workflowProfile;
        _healthMonitorConfig = healthMonitorConfig;

        _stateStore = stateStore;
        _assessmentStore = assessmentStore;
        _worktreeManager = worktreeManager;
        _vdtConfig = vdtConfig?.Value;
        _scenarioRegistry = scenarioRegistry;
        _agentLogService = agentLogService;
        _activeLlmTracker = activeLlmTracker;

        _registry.AgentStatusChanged += OnAgentStatusChanged;
        SubscribeToExplicitSignals();
    }

    public event EventHandler<AgentStuckEventArgs>? AgentStuck;

    /// <summary>
    /// Subscribe to explicit StatusUpdateMessage types that agents publish on the bus.
    /// Maps well-known message types directly to workflow signals — no keyword heuristics needed.
    /// </summary>
    private void SubscribeToExplicitSignals()
    {
        var sub = _messageBus.Subscribe<StatusUpdateMessage>("__health-monitor__", (msg, ct) =>
        {
            switch (msg.MessageType)
            {
                case "ResearchComplete":
                    SignalIfNew(WorkflowStateMachine.Signals.ResearchDocReady);
                    SignalIfNew(WorkflowStateMachine.Signals.ResearchComplete);
                    break;
                case "ArchitectureComplete":
                    SignalIfNew(WorkflowStateMachine.Signals.ArchitectureDocReady);
                    SignalIfNew(WorkflowStateMachine.Signals.ArchitectureComplete);
                    break;
                case "EngineeringPlanReady":
                    SignalIfNew(WorkflowStateMachine.Signals.EngineeringPlanReady);
                    SignalIfNew(WorkflowStateMachine.Signals.SoftwareEngineerReady);
                    break;
                case "AllTasksComplete":
                case "EngineeringComplete":
                    // Don't fire immediately — schedule the hard platform check instead.
                    // The bus message is an agent's self-report, but it can be stale after
                    // restart (agent recovered state from a prior run). The hard check at
                    // lines 515-559 verifies no open issues/PRs remain before firing.
                    // Without this guard, the signal fires before platform validation,
                    // causing false "Project Complete" on restart.
                    break;
            }

            // Try to advance phase immediately after any explicit signal
            string? blocker;
            _workflow.TryAdvancePhase(out blocker);

            return Task.CompletedTask;
        });
        _busSubscriptions.Add(sub);
    }

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
                        // Check log activity — if agent is producing output, it's working not stuck
                        if (_agentLogService is not null)
                        {
                            var lastLog = _agentLogService.GetLatestEntryTimestamp(kvp.Key);
                            if (lastLog.HasValue && (now - lastLog.Value) < LogActivityWindow)
                            {
                                _logger.LogDebug(
                                    "Agent '{AgentId}' past timeout ({Duration}) but has recent log activity ({LogAge} ago) — not stuck.",
                                    kvp.Key, duration, now - lastLog.Value);
                                continue;
                            }
                        }

                        // Check active LLM call — if waiting for AI response, it's working
                        if (_activeLlmTracker?.GetActiveCall(kvp.Key) is not null)
                        {
                            _logger.LogDebug(
                                "Agent '{AgentId}' past timeout ({Duration}) but has active LLM call — not stuck.",
                                kvp.Key, duration);
                            continue;
                        }

                        _logger.LogWarning(
                            "Agent '{AgentId}' appears stuck. Working for {Duration}. No recent log activity or LLM calls.",
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

            // T1.8: Liveness watchdog — verify the FlowMonitor service is still ticking.
            // If it has stopped, the entire health/auto-recovery layer is silent — operator
            // must be alerted. Best-effort; never throws.
            CheckFlowMonitorLiveness();

            // Daily retention prune for activity_log + metrics tables.
            // Mirrors FlowMonitorService pattern (once per 24h, best-effort).
            PruneStaleDbEntries();

            // One-time startup: clean stale candidate worktrees from crashed runs
            CleanupStaleCandidateWorktrees();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check.");
        }
    }

    /// <summary>
    /// "Who watches the watcher" — verifies the FlowMonitor service is producing ticks
    /// at its configured cadence. If the last tick is older than 2× the poll interval
    /// (or no ticks have ever been recorded), surfaces a notification so the operator
    /// knows the autonomous-recovery layer is silent.
    /// Best-effort: optional dependencies, swallows exceptions, logs warnings only.
    /// </summary>
    private void CheckFlowMonitorLiveness()
    {
        // Optional deps — if any are missing, FlowMonitor isn't configured in this host
        // (e.g., test harness, standalone dashboard). Nothing to watch.
        if (_flowMonitorPersistence is null || _flowMonitorConfig is null || _notifications is null)
            return;

        try
        {
            var pollSeconds = _flowMonitorConfig.CurrentValue.PollIntervalSeconds;
            if (pollSeconds <= 0) pollSeconds = 30;
            var threshold = TimeSpan.FromSeconds(2 * pollSeconds);

            var lastTick = _flowMonitorPersistence.GetLastTick();
            var now = DateTimeOffset.UtcNow;

            bool isStale = lastTick is null || now - lastTick.Value > threshold;

            if (isStale)
            {
                if (!_flowMonitorLivenessAlertSent)
                {
                    var lastTickStr = lastTick?.ToString("o") ?? "never";
                    var context =
                        $"FlowMonitor heartbeat is stale — last tick {lastTickStr}; " +
                        $"expected within {threshold}. The monitor service may be stopped or unhealthy.";

                    _logger.LogWarning(
                        "FlowMonitor liveness alert: last tick {LastTick}, threshold {Threshold}",
                        lastTickStr, threshold);

                    // Fire-and-forget — notifications service is async but liveness is
                    // observability, not control flow. Don't block the health timer.
                    _ = _notifications.AddNotificationAsync("flow-monitor:liveness", context, null);

                    _flowMonitorLivenessAlertSent = true;
                    // DO NOT auto-resolve — operator must investigate and confirm recovery.
                }
            }
            else if (_flowMonitorLivenessAlertSent)
            {
                // Heartbeat recovered — clear the previously-fired alert so a future
                // outage will alert again.
                _logger.LogInformation(
                    "FlowMonitor heartbeat recovered (last tick {LastTick}). Resolving liveness alert.",
                    lastTick);

                try
                {
                    _notifications.Resolve("flow-monitor:liveness", null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve flow-monitor:liveness notification");
                }

                _flowMonitorLivenessAlertSent = false;
            }
        }
        catch (Exception ex)
        {
            // Best-effort observability — never let a watchdog failure crash the health loop.
            _logger.LogWarning(ex, "CheckFlowMonitorLiveness failed (non-fatal)");
        }
    }

    /// <summary>
    /// Daily retention prune for activity_log and metrics tables.
    /// Mirrors FlowMonitorService's prune pattern: runs at most once per 24h,
    /// best-effort (never throws). Default retention: 30 days.
    /// </summary>
    private void PruneStaleDbEntries()
    {
        if (_stateStore is null)
            return;

        try
        {
            if ((DateTimeOffset.UtcNow - _lastPruneAt).TotalHours < 24)
                return;

            _stateStore.PruneOldEntriesAsync(TimeSpan.FromDays(DefaultRetentionDays))
                .GetAwaiter().GetResult();
            _assessmentStore?.PruneOlderThan(TimeSpan.FromDays(DefaultRetentionDays));
            _lastPruneAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Daily DB retention prune completed: activity_log + metrics older than {Days}d removed",
                DefaultRetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daily DB retention prune failed (non-fatal)");
        }
    }

    /// <summary>
    /// One-time startup cleanup: remove stale candidate worktree directories
    /// left behind after runner crashes. Runs once on the first health tick.
    /// </summary>
    private void CleanupStaleCandidateWorktrees()
    {
        if (_startupCleanupDone || _worktreeManager is null || _vdtConfig is null)
            return;

        _startupCleanupDone = true;

        try
        {
            var wsRoot = _vdtConfig.Workspace.RootPath;
            if (string.IsNullOrWhiteSpace(wsRoot) || !Directory.Exists(wsRoot))
                return;

            // Scan each agent workspace under .agents/ for stale .candidates/ dirs
            foreach (var agentDir in Directory.GetDirectories(wsRoot))
            {
                // Agent dirs contain repo dirs (e.g., .agents/softwareengineer-xxx/Compliance2/)
                foreach (var repoDir in Directory.GetDirectories(agentDir))
                {
                    var candidatesDir = Path.Combine(repoDir, ".candidates");
                    if (Directory.Exists(candidatesDir))
                    {
                        _worktreeManager.CleanupStaleCandidateWorktreesAsync(repoDir)
                            .GetAwaiter().GetResult();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup candidate worktree cleanup failed (non-fatal)");
        }
    }

    /// <summary>
    /// requiring agents to explicitly call Signal(). Checks status reasons
    /// and role states to detect when milestones have been reached.
    /// </summary>
    private void AutoDetectSignals()
    {
        // healthmon-false-research-complete: master kill-switch. When operators disable
        // auto-detect, the workflow still advances through explicit StatusUpdateMessage
        // subscriptions (SubscribeToExplicitSignals) — this only turns OFF the heuristic
        // inference layer that has historically been the source of false-positive signals.
        if (_healthMonitorConfig?.CurrentValue.AutoDetectSignals == false)
        {
            return;
        }

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

        // --- Research phase signals (healthmon-false-research-complete) ---
        // Hard platform check: only fire research.doc.ready if Research.md ACTUALLY exists
        // on the working branch. Then only fire research.complete if a Researcher status
        // reason matches a POSITIVE-completion phrase (not the previous loose substrings
        // like "complete" or "monitoring" that produced false positives when an agent
        // crashed). Fire-and-forget — result lands on next tick if the platform confirms.
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete) ||
            !_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchDocReady))
        {
            _ = TryFireResearchDocSignalsAsync(default);
        }

        // --- Architecture phase signals (healthmon-false-research-complete) ---
        // Same pattern: hard file-existence check for Architecture.md before firing
        // architecture.doc.ready; positive-completion phrase required for architecture.complete.
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete) ||
            !_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureDocReady))
        {
            _ = TryFireArchitectureDocSignalsAsync(default);
        }

        // --- Scenario→Architecture mapping signal ---
        // When architecture is complete (both doc.ready and complete signals fired), the
        // scenario→component mapping is implicitly done — the Architecture.md is already
        // merged and whatever mapping it contains is final. Auto-fire this signal so the
        // Architecture→EngineeringPlanning gate isn't permanently blocked.
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.ScenariosArchitectureMapped) &&
            _workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureDocReady) &&
            _workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete))
        {
            SignalIfNew(WorkflowStateMachine.Signals.ScenariosArchitectureMapped);
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

        // --- Scenario→Task mapping signal ---
        // When the engineering plan is ready, check that all critical scenarios have
        // implementing tasks. If the scenario registry is absent or has no scenarios,
        // auto-fire (same semantics as IsScenariosMechanismAbsent in WorkflowStateMachine).
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.ScenariosTasksAssigned) &&
            _workflow.HasSignal(WorkflowStateMachine.Signals.EngineeringPlanReady))
        {
            bool allCriticalMapped = true;
            if (_scenarioRegistry is not null && _scenarioRegistry.Current.Count > 0)
            {
                var critical = _scenarioRegistry.Critical;
                allCriticalMapped = critical.Count == 0 ||
                    critical.All(s => s.ImplementingTasks.Count > 0);
            }

            if (allCriticalMapped)
            {
                SignalIfNew(WorkflowStateMachine.Signals.ScenariosTasksAssigned);
            }
        }

        // --- Parallel Development signals ---
        if (!_workflow.HasSignal(WorkflowStateMachine.Signals.AllEngineeringComplete))
        {
            // healthmon-engineering-complete-stuck-phase (2026-05-12 fix): INVERTED GATE.
            // The hard platform check is now the PRIMARY signal source. The status-reason
            // heuristic is the OPTIONAL early-exit optimization (skip platform call when
            // agents clearly are not done yet). Previously the heuristic gated the platform
            // check, so when status reasons drifted to non-canonical phrases the platform
            // check never ran and the workflow stalled forever in ParallelDevelopment.
            //
            // Throttling: at most one platform check per EngineeringCompleteCheckCooldownSeconds
            // (90s default) to bound API cost. Cooldown stored as Volatile.Read/Write tick value
            // for atomic access across health-tick threads.

            // Heuristic — fast-path for the happy case: agents have explicitly self-reported
            // completion. Fires the signal immediately without waiting for the platform check
            // cooldown. Status reasons remain authoritative when agents follow the contract.
            bool seComplete = HasReasonContaining(AgentRole.SoftwareEngineer,
                AgentStatusReasons.EngineeringComplete,
                AgentStatusReasons.AllTasksComplete,
                AgentStatusReasons.AllTasksDone);

            var engineers = agents.Where(a => a.Identity.Role is AgentRole.SoftwareEngineer && a.Identity.Rank > 0).ToList();
            bool engineersDone = engineers.Count > 0 && engineers.All(a =>
                a.Status is AgentStatus.Online or AgentStatus.Idle &&
                ((a.StatusReason ?? "").Contains(AgentStatusReasons.Complete, StringComparison.OrdinalIgnoreCase) ||
                 (a.StatusReason ?? "").Contains(AgentStatusReasons.NoTask, StringComparison.OrdinalIgnoreCase) ||
                 (a.StatusReason ?? "").Contains(AgentStatusReasons.NoAssigned, StringComparison.OrdinalIgnoreCase)));

            // The seLeaderWaitingForIntegration veto previously blocked the platform check,
            // creating a permanent stall when the SE Leader's status reason said "integration pr"
            // long after the integration PR was actually merged. We keep this veto ONLY for the
            // fast-path heuristic — the platform check below runs anyway and will fire the signal
            // if the platform truly confirms zero open SE PRs (no integration PR can be open if
            // no SE PR is open).
            bool seLeaderWaitingForIntegration = agents.Any(a =>
                a.Identity.Role == AgentRole.SoftwareEngineer && a.Identity.Rank == 0 &&
                (a.StatusReason ?? "").Contains(AgentStatusReasons.IntegrationPr, StringComparison.OrdinalIgnoreCase));

            // Decide whether to schedule the hard platform check this tick.
            // - Schedule if heuristic fires (subject to integration-PR veto) — fast-path the
            //   happy case; platform check still runs to validate.
            // - Schedule unconditionally if cooldown has elapsed — covers the stuck-status-reason
            //   case the original heuristic-as-gate missed.
            var heuristicSaysTry = (seComplete || engineersDone) && !seLeaderWaitingForIntegration;
            var cooldownElapsed = (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastEngineeringCompleteCheckTicks))
                                  >= TimeSpan.FromSeconds(EngineeringCompleteCheckCooldownSeconds).Ticks;

            if ((heuristicSaysTry || cooldownElapsed) && _workItemService is not null && _pullRequestService is not null)
            {
                // Reserve the cooldown slot atomically so concurrent ticks don't all schedule
                // their own platform check. Lose-the-race ticks just exit early.
                var prevTicks = Volatile.Read(ref _lastEngineeringCompleteCheckTicks);
                if (Interlocked.CompareExchange(ref _lastEngineeringCompleteCheckTicks, DateTime.UtcNow.Ticks, prevTicks) != prevTicks)
                {
                    // Another tick won the race; nothing to do.
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // SAFEGUARD: also probe CLOSED engineering-task issues so we can
                            // distinguish "all tasks done" (open=0, closed>0) from "no tasks
                            // ever filed" (open=0, closed=0 — plan generation succeeded but
                            // task creation failed). The latter is a SILENT FAILURE we must
                            // never auto-resolve.
                            var openTasks = await _workItemService.ListByLabelAsync(
                                IssueWorkflow.Labels.EngineeringTask, "open", default).ConfigureAwait(false);
                            if (openTasks.Count > 0)
                            {
                                _logger.LogDebug(
                                    "engineering.all.complete suppressed by hard-check: {Count} open engineering-task issue(s) remain",
                                    openTasks.Count);
                                return;
                            }

                            var openPrs = await _pullRequestService.ListOpenAsync(default).ConfigureAwait(false);
                            var openSePrs = openPrs.Count(p =>
                                p.HeadBranch?.StartsWith(BranchPatterns.AgentPrefix, StringComparison.OrdinalIgnoreCase) == true &&
                                p.HeadBranch.Contains(BranchPatterns.AgentSoftwareEngineerInfix, StringComparison.OrdinalIgnoreCase));
                            if (openSePrs > 0)
                            {
                                _logger.LogDebug(
                                    "engineering.all.complete suppressed by hard-check: {Count} open softwareengineer PR(s) remain",
                                    openSePrs);
                                return;
                            }

                            // Sticky observation: once we've seen an engineering-task issue
                            // (open OR closed) during this run, remember it so a transient
                            // post-merge zero-open-zero-closed snapshot doesn't suppress the
                            // signal. Closed tasks have the engineering-task label too.
                            if (!_engineeringTaskEverObserved)
                            {
                                var closedTasks = await _workItemService.ListByLabelAsync(
                                    IssueWorkflow.Labels.EngineeringTask, "closed", default).ConfigureAwait(false);
                                if (closedTasks.Count > 0)
                                    _engineeringTaskEverObserved = true;
                            }

                            // SAFEGUARD: refuse to fire when no engineering-task issue was
                            // ever seen — protects against the plan-without-tasks failure mode.
                            if (!_engineeringTaskEverObserved)
                            {
                                _logger.LogDebug(
                                    "engineering.all.complete suppressed: no engineering-task issue ever observed during this run (plan-without-tasks safeguard)");
                                return;
                            }

                            // Platform confirms no work — fire the signal.
                            SignalIfNew(WorkflowStateMachine.Signals.AllEngineeringComplete);
                        }
                        catch (Exception ex)
                        {
                            // Promoted from LogDebug to LogWarning so platform API failures
                            // (rate limit, auth rotation, network blip) are visible at the
                            // default log level. Next tick retries after the cooldown.
                            _logger.LogWarning(ex,
                                "engineering.all.complete hard-check failed (transient — next retry after {Cooldown}s)",
                                EngineeringCompleteCheckCooldownSeconds);
                        }
                    });
                }
            }
            else if (heuristicSaysTry && (_workItemService is null || _pullRequestService is null))
            {
                // Platform services unavailable (tests / standalone host) — fall back to the
                // status-reason heuristic alone. Pre-fix behaviour, acceptable for those envs.
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
        if (_workflow.Signal(signal))
        {
            _logger.LogInformation("Auto-detected signal: {Signal}", signal);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // healthmon-false-research-complete: hardened doc-signal heuristic.
    //
    // Replaces the previous log-text substring matcher (which fired research.doc.ready /
    // research.complete based on phrases like "monitoring" or "complete" appearing in ANY
    // agent's status reason) with:
    //
    //   1. A hard platform check via IRepositoryContentService — Research.md / Architecture.md
    //      MUST actually exist on the working branch before *.doc.ready fires.
    //   2. A POSITIVE-completion-phrase requirement (e.g., "research published") in the
    //      relevant agent's status reason before *.complete fires. Loose substrings like
    //      "complete" or "monitoring" are no longer matched here.
    //   3. A per-phase cooldown (default 60s, via HealthMonitorConfig.DocCheckCooldownSeconds)
    //      so the platform call can't be re-issued from every OnAgentStatusChanged tick.
    //
    // If the optional platform deps are not registered (tests, standalone host), this
    // heuristic safely no-ops — the explicit StatusUpdateMessage path is unaffected.
    //
    // The methods are `internal` so they can be awaited deterministically from
    // VirtualDevTeam.Integration.Tests (see InternalsVisibleTo in the .csproj).
    // ─────────────────────────────────────────────────────────────────────────

    internal async Task TryFireResearchDocSignalsAsync(CancellationToken ct)
    {
        try
        {
            if (_repoContent is null) return;

            // Quick gate: don't bother with the platform call if both signals are already set.
            if (_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchDocReady) &&
                _workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete))
            {
                return;
            }

            if (!TryAcquireCooldownSlot(ref _lastResearchDocCheckTicks))
            {
                _logger.LogDebug("research.doc.ready check skipped — cooldown not elapsed");
                return;
            }

            var path = ResolveDocPath(isResearch: true);
            var branch = ResolveWorkingBranch();
            var content = await _repoContent.GetFileContentAsync(path, branch, ct).ConfigureAwait(false);
            bool exists = !string.IsNullOrEmpty(content);

            if (!exists)
            {
                _logger.LogDebug(
                    "research.doc.ready suppressed: '{Path}' not found on branch '{Branch}'",
                    path, branch ?? "<default>");
                return;
            }

            SignalIfNew(WorkflowStateMachine.Signals.ResearchDocReady);

            // research.complete requires BOTH the doc-ready signal already set AND
            // a positive-completion phrase in a Researcher status reason. We don't accept
            // loose substrings like "complete" or "monitoring" — those caused false
            // positives in the bug this fix addresses.
            // When Researcher is disabled, PM generates Research.md inline — accept the doc
            // existence as sufficient proof of completion (no Researcher agent to check).
            if (HasResearcherCompletionPhrase() || _workflow.IsResearcherDisabled())
            {
                SignalIfNew(WorkflowStateMachine.Signals.ResearchComplete);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Research doc-signal platform check failed (non-fatal)");
        }
    }

    internal async Task TryFireArchitectureDocSignalsAsync(CancellationToken ct)
    {
        try
        {
            if (_repoContent is null) return;

            if (_workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureDocReady) &&
                _workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete))
            {
                return;
            }

            if (!TryAcquireCooldownSlot(ref _lastArchitectureDocCheckTicks))
            {
                _logger.LogDebug("architecture.doc.ready check skipped — cooldown not elapsed");
                return;
            }

            var path = ResolveDocPath(isResearch: false);
            var branch = ResolveWorkingBranch();
            var content = await _repoContent.GetFileContentAsync(path, branch, ct).ConfigureAwait(false);
            bool exists = !string.IsNullOrEmpty(content);

            if (!exists)
            {
                _logger.LogDebug(
                    "architecture.doc.ready suppressed: '{Path}' not found on branch '{Branch}'",
                    path, branch ?? "<default>");
                return;
            }

            SignalIfNew(WorkflowStateMachine.Signals.ArchitectureDocReady);

            if (HasArchitectCompletionPhrase())
            {
                SignalIfNew(WorkflowStateMachine.Signals.ArchitectureComplete);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Architecture doc-signal platform check failed (non-fatal)");
        }
    }

    /// <summary>
    /// Returns true if the cooldown for the given slot has elapsed and updates the
    /// slot to "now". Uses Interlocked.CompareExchange so concurrent calls (e.g., from
    /// the timer thread and an agent-status event) can't both pass.
    /// </summary>
    private bool TryAcquireCooldownSlot(ref long lastCheckTicks)
    {
        var cooldownSeconds = _healthMonitorConfig?.CurrentValue.DocCheckCooldownSeconds ?? 60;
        if (cooldownSeconds <= 0) return true; // disabled — always permit
        var now = DateTime.UtcNow.Ticks;
        var prev = Interlocked.Read(ref lastCheckTicks);
        var elapsedSeconds = (now - prev) / TimeSpan.TicksPerSecond;
        if (prev != 0 && elapsedSeconds < cooldownSeconds)
        {
            return false;
        }
        // Race-safe acquire — only one caller actually takes the slot.
        return Interlocked.CompareExchange(ref lastCheckTicks, now, prev) == prev;
    }

    private string ResolveDocPath(bool isResearch)
    {
        if (_workflowProfile is not null)
        {
            var name = isResearch
                ? _workflowProfile.ResearchDocName
                : _workflowProfile.ArchitectureDocName;
            return _workflowProfile.GetArtifactPath(name);
        }
        return isResearch ? "Research.md" : "Architecture.md";
    }

    private string? ResolveWorkingBranch() => _runBranchProvider?.EffectiveBranch;

    private bool HasResearcherCompletionPhrase()
    {
        var agents = _registry.GetAllAgents();
        return agents
            .Where(a => a.Identity.Role == AgentRole.Researcher)
            .Any(a => MatchesAny(a.StatusReason, AgentStatusReasons.ResearchCompletePhrases));
    }

    private bool HasArchitectCompletionPhrase()
    {
        var agents = _registry.GetAllAgents();
        return agents
            .Where(a => a.Identity.Role == AgentRole.Architect)
            .Any(a => MatchesAny(a.StatusReason, AgentStatusReasons.ArchitectureCompletePhrases));
    }

    private static bool MatchesAny(string? reason, IReadOnlyList<string> phrases)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        for (int i = 0; i < phrases.Count; i++)
        {
            if (reason.Contains(phrases[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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

        // Immediately check signals on status changes (don't wait for 30s poll)
        try
        {
            AutoDetectSignals();
            _workflow.TryAdvancePhase(out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-detect workflow signals from agent status change");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        _registry.AgentStatusChanged -= OnAgentStatusChanged;
        foreach (var sub in _busSubscriptions)
            sub.Dispose();
        _busSubscriptions.Clear();
    }
}
