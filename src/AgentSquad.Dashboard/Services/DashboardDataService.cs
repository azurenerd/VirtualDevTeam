using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Diagnostics;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Persistence;
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

    /// <summary>For SME/custom agents, the specialty name (e.g., "Blazor UI Developer").</summary>
    public string? Specialty { get; init; }

    /// <summary>Skill/domain capabilities (e.g., "frontend", "blazor", "css").</summary>
    public List<string> Capabilities { get; init; } = [];

    // Self-diagnostic
    public string? DiagnosticSummary { get; init; }
    public string? DiagnosticJustification { get; init; }
    public bool DiagnosticCompliant { get; init; } = true;
    public string? DiagnosticComplianceIssue { get; init; }
    public string? DiagnosticScenarioRef { get; init; }

    // Estimated usage & cost (MSRP)
    public int EstPromptTokens { get; init; }
    public int EstCompletionTokens { get; init; }
    public int EstTotalTokens => EstPromptTokens + EstCompletionTokens;
    public int AiCalls { get; init; }
    public decimal EstimatedCost { get; init; }
}

/// <summary>A point-in-time record of an agent's diagnostic justification.</summary>
public sealed record DiagnosticHistoryEntry
{
    public required string AgentId { get; init; }
    public required string AgentDisplayName { get; init; }
    public required AgentRole Role { get; init; }
    public required string Summary { get; init; }
    public required string Justification { get; init; }
    public required bool IsCompliant { get; init; }
    public string? ComplianceIssue { get; init; }
    public string? ScenarioRef { get; init; }
    public required DateTime Timestamp { get; init; }
}

/// <summary>Overall execution health assessment for the Health Monitor page.</summary>
public sealed record ExecutionHealthAssessment
{
    public required string OverallStatus { get; init; }
    public required string Phase { get; init; }
    public required TimeSpan Uptime { get; init; }
    public required int TotalAgents { get; init; }
    public required int WorkingAgents { get; init; }
    public required int CompliantAgents { get; init; }
    public required int NonCompliantAgents { get; init; }
    public required int ErrorAgents { get; init; }
    public required bool HasDeadlock { get; init; }
    public required List<string> Observations { get; init; }
    public required List<GateCondition> NextPhaseGates { get; init; }
    public required List<PhaseTimelineEntry> PhaseTimeline { get; init; }
}

/// <summary>Entry in the phase progression timeline.</summary>
public sealed record PhaseTimelineEntry
{
    public required string Phase { get; init; }
    public required bool IsCompleted { get; init; }
    public required bool IsCurrent { get; init; }
    public DateTime? CompletedAt { get; init; }
}

/// <summary>A significant event in the execution flow, shown in the timeline flowchart.</summary>
public sealed record ExecutionMilestone
{
    public required string Icon { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public required string Category { get; init; } // phase, document, pr, issues, review, test
    public required DateTime Timestamp { get; init; }
    public required bool IsCompleted { get; init; }
    public string? AgentName { get; init; }
}

public sealed class DashboardDataService : BackgroundService, IDashboardDataService
{
    private readonly AgentRegistry _registry;
    private readonly HealthMonitor _healthMonitor;
    private readonly DeadlockDetector _deadlockDetector;
    private readonly WorkflowStateMachine _workflow;
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentStateStore _stateStore;
    private readonly AgentChatService _chatService;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly IGitHubService _github;
    private readonly RateLimitManager _rateLimitManager;
    private readonly ILogger<DashboardDataService> _logger;

    private readonly Dictionary<string, AgentSnapshot> _agentCache = new();
    private readonly Dictionary<string, List<AgentLogEntry>> _agentErrors = new();
    private readonly Dictionary<string, IAgent> _trackedAgents = new();
    private readonly List<DiagnosticHistoryEntry> _diagnosticHistory = new();
    private readonly Dictionary<string, DateTime> _lastDiagnosticRecordTime = new();
    private readonly List<ExecutionMilestone> _milestones = new();
    private readonly HashSet<string> _recordedMilestoneKeys = new();
    private readonly object _cacheLock = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private const int MaxDiagnosticHistory = 500;

    private AgentHealthSnapshot? _lastHealthSnapshot;
    private IReadOnlyList<AgentPullRequest> _cachedPullRequests = Array.Empty<AgentPullRequest>();
    private DateTime _lastPrFetchUtc = DateTime.MinValue;
    private IReadOnlyList<AgentIssue> _cachedIssues = Array.Empty<AgentIssue>();
    private DateTime _lastIssueFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan PrCacheExpiry = TimeSpan.FromSeconds(30);

    public DashboardDataService(
        AgentRegistry registry,
        HealthMonitor healthMonitor,
        DeadlockDetector deadlockDetector,
        WorkflowStateMachine workflow,
        ModelRegistry modelRegistry,
        AgentStateStore stateStore,
        AgentChatService chatService,
        IHubContext<AgentHub> hubContext,
        IGitHubService github,
        RateLimitManager rateLimitManager,
        ILogger<DashboardDataService> logger)
    {
        _registry = registry;
        _healthMonitor = healthMonitor;
        _deadlockDetector = deadlockDetector;
        _workflow = workflow;
        _modelRegistry = modelRegistry;
        _stateStore = stateStore;
        _chatService = chatService;
        _hubContext = hubContext;
        _github = github;
        _rateLimitManager = rateLimitManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _registry.AgentRegistered += OnAgentRegistered;
        _registry.AgentUnregistered += OnAgentUnregistered;
        _registry.AgentStatusChanged += OnAgentStatusChanged;
        _workflow.PhaseChanged += OnPhaseChanged;

        RecordMilestone("🚀", "Session Started", "AgentSquad pipeline initialized", "phase");
        SeedCache();

        _logger.LogInformation("Dashboard data service started");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PushHealthSnapshot(stoppingToken);
                // In standalone mode, periodically refresh from DB since we don't get registry events
                if (_registry.GetAllAgents().Count == 0)
                    SeedFromDatabase();
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
            _workflow.PhaseChanged -= OnPhaseChanged;
        }
    }

    public IReadOnlyList<AgentSnapshot> GetAllAgentSnapshots()
    {
        lock (_cacheLock)
        {
            return _agentCache.Values.ToList();
        }
    }

    /// <summary>
    /// Clear all dashboard caches so the UI starts fresh after a reset.
    /// Agent event subscriptions remain active for newly spawned agents.
    /// </summary>
    public void ResetCaches()
    {
        lock (_cacheLock)
        {
            _agentCache.Clear();
            _agentErrors.Clear();
            _trackedAgents.Clear();
            _diagnosticHistory.Clear();
            _milestones.Clear();
            _recordedMilestoneKeys.Clear();
            _cachedPullRequests = Array.Empty<AgentPullRequest>();
            _cachedIssues = Array.Empty<AgentIssue>();
            _lastPrFetchUtc = DateTime.MinValue;
            _lastIssueFetchUtc = DateTime.MinValue;
        }

        RecordMilestone("🔄", "Project Reset", "Repository cleaned and agents restarted", "phase");
        _logger.LogInformation("Dashboard caches reset");
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

    /// <summary>Gets the activity log for a specific agent from the persistent store.</summary>
    public async Task<IReadOnlyList<ActivityLogEntry>> GetActivityLogAsync(
        string agentId, int count = 100, CancellationToken ct = default)
    {
        try
        {
            return await _stateStore.GetRecentActivityAsync(agentId, count, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve activity log for agent {AgentId}", agentId);
            return [];
        }
    }

    /// <summary>Get the list of available model names for the dropdown.</summary>
    public IReadOnlyList<string> GetAvailableModels() => ModelRegistry.AvailableCopilotModels;

    /// <summary>
    /// Refresh ActiveModel for all cached agents from ModelRegistry.
    /// Call this after FastMode or model config changes so cards show the correct model.
    /// </summary>
    public void RefreshActiveModels()
    {
        lock (_cacheLock)
        {
            foreach (var (agentId, snapshot) in _agentCache.ToList())
            {
                var effectiveModel = _modelRegistry.GetEffectiveModel(agentId);
                if (snapshot.ActiveModel != effectiveModel)
                {
                    _agentCache[agentId] = snapshot with { ActiveModel = effectiveModel };
                }
            }
        }

        _ = _hubContext.Clients.All.SendAsync("ModelsRefreshed");
        NotifyStateChanged();
    }

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

        // In standalone mode the registry is empty — hydrate from the shared SQLite DB
        if (agents.Count == 0)
        {
            SeedFromDatabase();
        }
    }

    private void SeedFromDatabase()
    {
        try
        {
            var usageMap = _stateStore.LoadAllAiUsage();
            var activityMap = _stateStore.GetLatestActivityPerAgent();
            var bootUtc = _stateStore.GetLastBootUtc();

            // Combine all known agent IDs from both sources
            var allAgentIds = new HashSet<string>(usageMap.Keys);
            foreach (var id in activityMap.Keys) allAgentIds.Add(id);

            if (allAgentIds.Count == 0) return;

            // Only include agents active since the last runner boot
            var activeIds = allAgentIds
                .Where(id => activityMap.TryGetValue(id, out var a) && a.Timestamp >= bootUtc)
                .OrderBy(id => id)
                .ToList();

            // Group by role and assign indices for display names
            var roleCounters = new Dictionary<AgentRole, int>();

            lock (_cacheLock)
            {
                // Remove stale agents no longer in the active set
                var staleIds = _agentCache.Keys.Where(k => !activeIds.Contains(k) && !_trackedAgents.ContainsKey(k)).ToList();
                foreach (var id in staleIds) _agentCache.Remove(id);

                foreach (var agentId in activeIds)
                {
                    var usage = usageMap.GetValueOrDefault(agentId);
                    activityMap.TryGetValue(agentId, out var activity);
                    var role = InferRole(agentId);

                    roleCounters.TryGetValue(role, out var idx);
                    roleCounters[role] = idx + 1;

                    // Parse actual status from activity details
                    var inferredStatus = AgentStatus.Online;
                    var statusReason = activity.Details ?? "";
                    if (activity.EventType == "status" && !string.IsNullOrEmpty(activity.Details))
                    {
                        // Activity details format: "Working → Idle: reason" or "Idle → Working: reason"
                        var details = activity.Details;
                        if (details.Contains("→"))
                        {
                            var arrow = details.IndexOf("→", StringComparison.Ordinal);
                            var afterArrow = details[(arrow + 1)..].Trim();
                            var colonIdx = afterArrow.IndexOf(':');
                            var targetState = colonIdx >= 0 ? afterArrow[..colonIdx].Trim() : afterArrow.Trim();
                            statusReason = colonIdx >= 0 ? afterArrow[(colonIdx + 1)..].Trim() : "";

                            inferredStatus = targetState switch
                            {
                                "Idle" => AgentStatus.Idle,
                                "Working" => AgentStatus.Working,
                                "Initializing" => AgentStatus.Initializing,
                                "Online" => AgentStatus.Online,
                                _ => AgentStatus.Idle
                            };
                        }
                    }
                    else if (!string.IsNullOrEmpty(activity.Details))
                    {
                        // Non-status events (task, build, test) mean the agent is working
                        inferredStatus = AgentStatus.Working;
                        statusReason = activity.Details;
                    }

                    _agentCache[agentId] = new AgentSnapshot
                    {
                        Id = agentId,
                        DisplayName = FormatDisplayName(agentId, role, idx),
                        Role = role,
                        ModelTier = role switch
                        {
                            AgentRole.ProgramManager or AgentRole.Architect or AgentRole.SoftwareEngineer => "premium",
                            _ => "standard"
                        },
                        Status = inferredStatus,
                        StatusReason = statusReason,
                        CreatedAt = activity.Timestamp != default ? activity.Timestamp : DateTime.UtcNow,
                        ActiveModel = usage.LastModel ?? "",
                        LastStatusChange = activity.Timestamp != default ? activity.Timestamp : DateTime.UtcNow,
                        EstPromptTokens = usage.PromptTokens,
                        EstCompletionTokens = usage.CompletionTokens,
                        AiCalls = usage.TotalCalls,
                        EstimatedCost = usage.EstimatedCost
                    };
                }
            }
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seed agent cache from database");
        }
    }

    private static AgentRole InferRole(string agentId)
    {
        if (agentId.StartsWith("programmanager", StringComparison.OrdinalIgnoreCase)) return AgentRole.ProgramManager;
        if (agentId.StartsWith("researcher", StringComparison.OrdinalIgnoreCase)) return AgentRole.Researcher;
        if (agentId.StartsWith("architect", StringComparison.OrdinalIgnoreCase)) return AgentRole.Architect;
        if (agentId.StartsWith("softwareengineer", StringComparison.OrdinalIgnoreCase)) return AgentRole.SoftwareEngineer;
        if (agentId.StartsWith("testengineer", StringComparison.OrdinalIgnoreCase)) return AgentRole.TestEngineer;
        return AgentRole.SoftwareEngineer;
    }

    private static string FormatDisplayName(string agentId, AgentRole role, int indexInRole)
    {
        var baseName = role switch
        {
            AgentRole.ProgramManager => "Program Manager",
            AgentRole.Researcher => "Researcher",
            AgentRole.Architect => "Architect",
            AgentRole.SoftwareEngineer => "Software Engineer",
            AgentRole.TestEngineer => "Test Engineer",
            _ => agentId
        };
        return indexInRole > 0 ? $"{baseName} {indexInRole}" : baseName;
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

        _ = _stateStore.LogActivityAsync(e.Agent.Identity.Id, "system",
            $"Agent registered: {e.Agent.Identity.DisplayName} ({e.Agent.Identity.Role})");

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
                    LastStatusChange = statusChangeTime,
                    AssignedPullRequest = e.Agent.AssignedPullRequest,
                    ActiveModel = _modelRegistry.GetEffectiveModel(e.Agent.Id)
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

        // Detect milestones from status reason text
        if (!string.IsNullOrEmpty(e.Reason))
        {
            DetectActivityMilestone(new AgentActivityEventArgs
            {
                AgentId = e.Agent.Id,
                EventType = "status",
                Details = e.Reason
            });
        }

        NotifyStateChanged();
    }

    private async Task PushHealthSnapshot(CancellationToken ct)
    {
        try
        {
            _lastHealthSnapshot = _healthMonitor.GetSnapshot();
            RefreshUsageStats();
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
        agent.ActivityLogged += OnAgentActivityLogged;
        agent.DiagnosticChanged += OnAgentDiagnosticChanged;
    }

    private void OnAgentDiagnosticChanged(object? sender, DiagnosticChangedEventArgs e)
    {
        lock (_cacheLock)
        {
            if (_agentCache.TryGetValue(e.AgentId, out var snapshot))
            {
                // Always update the snapshot cache
                _agentCache[e.AgentId] = snapshot with
                {
                    DiagnosticSummary = e.Diagnostic.Summary,
                    DiagnosticJustification = e.Diagnostic.Justification,
                    DiagnosticCompliant = e.Diagnostic.IsCompliant,
                    DiagnosticComplianceIssue = e.Diagnostic.ComplianceIssue,
                    DiagnosticScenarioRef = e.Diagnostic.ScenarioRef
                };

                // Record to history on actual changes or every 30s for heartbeat
                var now = DateTime.UtcNow;
                var lastRecorded = _lastDiagnosticRecordTime.GetValueOrDefault(e.AgentId, DateTime.MinValue);
                bool shouldRecord = e.IsChanged || (now - lastRecorded).TotalSeconds >= 30;

                if (shouldRecord)
                {
                    _lastDiagnosticRecordTime[e.AgentId] = now;
                    _diagnosticHistory.Add(new DiagnosticHistoryEntry
                    {
                        AgentId = e.AgentId,
                        AgentDisplayName = snapshot.DisplayName,
                        Role = snapshot.Role,
                        Summary = e.Diagnostic.Summary,
                        Justification = e.Diagnostic.Justification,
                        IsCompliant = e.Diagnostic.IsCompliant,
                        ComplianceIssue = e.Diagnostic.ComplianceIssue,
                        ScenarioRef = e.Diagnostic.ScenarioRef,
                        Timestamp = e.Diagnostic.Timestamp
                    });

                    if (_diagnosticHistory.Count > MaxDiagnosticHistory)
                        _diagnosticHistory.RemoveRange(0, _diagnosticHistory.Count - MaxDiagnosticHistory);
                }
            }
        }

        _ = _hubContext.Clients.All.SendAsync("AgentDiagnosticChanged", new
        {
            e.AgentId,
            e.Diagnostic.Summary,
            e.Diagnostic.Justification,
            e.Diagnostic.IsCompliant,
            e.Diagnostic.ComplianceIssue,
            e.Diagnostic.ScenarioRef
        });
        NotifyStateChanged();
    }

    private void OnAgentActivityLogged(object? sender, AgentActivityEventArgs e)
    {
        try
        {
            _ = _stateStore.LogActivityAsync(e.AgentId, e.EventType, e.Details);

            _ = _hubContext.Clients.All.SendAsync("AgentActivityLogged", new
            {
                e.AgentId,
                e.EventType,
                e.Details,
                Timestamp = DateTime.UtcNow
            });

            // Detect milestones from activity events
            DetectActivityMilestone(e);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist activity log for agent {AgentId}", e.AgentId);
        }
    }

    private void OnPhaseChanged(object? sender, PhaseTransitionEventArgs e)
    {
        var phaseIcon = e.NewPhase switch
        {
            ProjectPhase.Research => "🔬",
            ProjectPhase.Architecture => "🏗️",
            ProjectPhase.EngineeringPlanning => "📋",
            ProjectPhase.ParallelDevelopment => "⚙️",
            ProjectPhase.Testing => "🧪",
            ProjectPhase.Review => "🔍",
            ProjectPhase.Completion => "🎉",
            _ => "▶️"
        };

        RecordMilestone(phaseIcon, $"{FormatPhase(e.NewPhase)} Phase Started",
            e.Reason, "phase");
    }

    private void DetectActivityMilestone(AgentActivityEventArgs e)
    {
        var details = e.Details ?? "";
        var detailsLower = details.ToLowerInvariant();
        string? agentName;
        lock (_cacheLock)
        {
            agentName = _agentCache.TryGetValue(e.AgentId, out var snap) ? snap.DisplayName : e.AgentId;
        }

        // Detect PR creation
        if (detailsLower.Contains("created pr") || detailsLower.Contains("opened pr") ||
            (e.EventType == "status" && detailsLower.Contains("pr #") && detailsLower.Contains("creat")))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("📝", $"PR {prRef} Created",
                $"{agentName}: {TruncateDetail(details)}", "pr", agentName);
        }

        // Detect PR merge
        if (detailsLower.Contains("merged") && detailsLower.Contains("pr"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("✅", $"PR {prRef} Merged",
                $"{agentName}: {TruncateDetail(details)}", "pr", agentName);
        }

        // Detect document creation/updates
        if (detailsLower.Contains("research.md"))
        {
            RecordMilestone("📄", "Research.md Created",
                $"{agentName} produced the research document", "document", agentName);
        }
        if (detailsLower.Contains("pmspec.md"))
        {
            RecordMilestone("📋", "PMSpec.md Created",
                $"{agentName} produced the PM specification", "document", agentName);
        }
        if (detailsLower.Contains("architecture.md") && !detailsLower.Contains("marker"))
        {
            RecordMilestone("🏛️", "Architecture.md Created",
                $"{agentName} produced the architecture document", "document", agentName);
        }
        if (detailsLower.Contains("engineering plan created") || detailsLower.Contains("engineering-task"))
        {
            RecordMilestone("📐", "Engineering Tasks Created",
                $"{agentName} created engineering task issues", "document", agentName);
        }

        // Detect issue creation
        if (detailsLower.Contains("created") && detailsLower.Contains("issue") &&
            (detailsLower.Contains("user stor") || detailsLower.Contains("task")))
        {
            RecordMilestone("🎫", "User Story Issues Created",
                $"{agentName}: {TruncateDetail(details)}", "issues", agentName);
        }

        // Detect review actions
        if (detailsLower.Contains("approved") && detailsLower.Contains("pr"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("👍", $"PR {prRef} Approved",
                $"{agentName}: {TruncateDetail(details)}", "review", agentName);
        }
        if (detailsLower.Contains("changes requested") || detailsLower.Contains("requested changes"))
        {
            var prRef = ExtractPrRef(details);
            RecordMilestone("🔄", $"Changes Requested on PR {prRef}",
                $"{agentName}: {TruncateDetail(details)}", "review", agentName);
        }

        // Detect test actions
        if (detailsLower.Contains("test") && (detailsLower.Contains("created") || detailsLower.Contains("written")))
        {
            RecordMilestone("🧪", "Tests Written",
                $"{agentName}: {TruncateDetail(details)}", "test", agentName);
        }
    }

    private void RecordMilestone(string icon, string title, string? detail, string category, string? agentName = null)
    {
        var key = $"{category}:{title}";
        lock (_cacheLock)
        {
            if (!_recordedMilestoneKeys.Add(key))
                return; // Already recorded

            _milestones.Add(new ExecutionMilestone
            {
                Icon = icon,
                Title = title,
                Detail = detail,
                Category = category,
                Timestamp = DateTime.UtcNow,
                IsCompleted = true,
                AgentName = agentName
            });
        }
    }

    /// <summary>Get the execution timeline milestones, oldest first.</summary>
    public IReadOnlyList<ExecutionMilestone> GetExecutionTimeline()
    {
        lock (_cacheLock)
        {
            return _milestones.OrderBy(m => m.Timestamp).ToList();
        }
    }

    private static string ExtractPrRef(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"#(\d+)");
        return match.Success ? $"#{match.Groups[1].Value}" : "";
    }

    private static string TruncateDetail(string text) =>
        text.Length > 120 ? text[..117] + "…" : text;

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

    private AgentSnapshot ToSnapshot(IAgent agent)
    {
        var usage = _modelRegistry.UsageTracker.GetStats(agent.Identity.Id);
        var diag = agent.CurrentDiagnostic;
        return new()
        {
            Id = agent.Identity.Id,
            DisplayName = agent.Identity.DisplayName,
            Role = agent.Identity.Role,
            ModelTier = agent.Identity.ModelTier,
            Status = agent.Status,
            StatusReason = agent.StatusReason,
            CreatedAt = agent.Identity.CreatedAt,
            AssignedPullRequest = agent.Identity.AssignedPullRequest,
            Specialty = agent.Identity.Role == AgentRole.Custom
                ? agent.Identity.DisplayName
                : null,
            Capabilities = agent.Identity.Capabilities,
            ActiveModel = _modelRegistry.GetEffectiveModel(agent.Identity.Id),
            LastStatusChange = DateTime.UtcNow,
            ErrorCount = agent.RecentErrors.Count,
            DiagnosticSummary = diag?.Summary,
            DiagnosticJustification = diag?.Justification,
            DiagnosticCompliant = diag?.IsCompliant ?? true,
            DiagnosticComplianceIssue = diag?.ComplianceIssue,
            DiagnosticScenarioRef = diag?.ScenarioRef,
            EstPromptTokens = usage.PromptTokens,
            EstCompletionTokens = usage.CompletionTokens,
            AiCalls = usage.TotalCalls,
            EstimatedCost = usage.EstimatedCost
        };
    }

    /// <summary>Refreshes usage stats for all cached agents from the usage tracker.</summary>
    private void RefreshUsageStats()
    {
        lock (_cacheLock)
        {
            foreach (var (agentId, snapshot) in _agentCache.ToList())
            {
                var usage = _modelRegistry.UsageTracker.GetStats(agentId);
                _agentCache[agentId] = snapshot with
                {
                    EstPromptTokens = usage.PromptTokens,
                    EstCompletionTokens = usage.CompletionTokens,
                    AiCalls = usage.TotalCalls,
                    EstimatedCost = usage.EstimatedCost
                };
            }
        }
    }

    /// <summary>Get total estimated cost across all agents for this run.</summary>
    public decimal GetTotalEstimatedCost() => _modelRegistry.UsageTracker.GetTotalCost();

    /// <summary>Get total AI calls across all agents for this run.</summary>
    public int GetTotalAiCalls() => _modelRegistry.UsageTracker.GetAllStats().Values.Sum(s => s.TotalCalls);

    /// <summary>Send a chat message to an agent and get an AI-generated response.</summary>
    public async Task<AgentChatMessage> SendAgentChatAsync(
        string agentId, string message, CancellationToken ct = default)
    {
        IAgent? agent;
        lock (_cacheLock) { _trackedAgents.TryGetValue(agentId, out agent); }

        if (agent is null)
            return new AgentChatMessage
            {
                Role = "assistant",
                Content = "⚠️ Agent not found or no longer registered."
            };

        return await _chatService.SendMessageAsync(agent, message, ct);
    }

    /// <summary>Get the chat history for an agent.</summary>
    public IReadOnlyList<AgentChatMessage> GetAgentChatHistory(string agentId) =>
        _chatService.GetHistory(agentId);

    /// <summary>Clear the chat history for an agent.</summary>
    public void ClearAgentChat(string agentId) => _chatService.ClearHistory(agentId);

    /// <summary>Get the diagnostic justification history feed, newest first.</summary>
    public IReadOnlyList<DiagnosticHistoryEntry> GetDiagnosticHistory(
        string? agentIdFilter = null, bool? compliantFilter = null, int limit = 200)
    {
        lock (_cacheLock)
        {
            IEnumerable<DiagnosticHistoryEntry> query = _diagnosticHistory;

            if (agentIdFilter is not null)
                query = query.Where(e => e.AgentId == agentIdFilter);
            if (compliantFilter.HasValue)
                query = query.Where(e => e.IsCompliant == compliantFilter.Value);

            return query.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
        }
    }

    /// <summary>Build the overall execution health assessment.</summary>
    public ExecutionHealthAssessment GetExecutionHealthAssessment()
    {
        var snapshot = _lastHealthSnapshot ?? _healthMonitor.GetSnapshot();
        var hasDeadlock = _deadlockDetector.HasDeadlock(out var deadlockCycle);
        var currentPhase = _workflow.CurrentPhase;
        var gates = _workflow.GetCurrentGates();
        var transitionHistory = _workflow.GetTransitionHistory();

        List<AgentSnapshot> agents;
        lock (_cacheLock) { agents = _agentCache.Values.ToList(); }

        var workingCount = agents.Count(a => a.Status == AgentStatus.Working);
        var compliantCount = agents.Count(a => a.DiagnosticCompliant);
        var nonCompliantCount = agents.Count(a => !a.DiagnosticCompliant);
        var errorCount = agents.Count(a => a.ErrorCount > 0);

        // Build observations
        var observations = new List<string>();

        // Phase assessment
        observations.Add($"Workflow is in the **{FormatPhase(currentPhase)}** phase.");
        if (transitionHistory.Count > 0)
        {
            var lastTransition = transitionHistory[^1];
            var sinceTransition = DateTime.UtcNow - lastTransition.Timestamp;
            observations.Add($"Entered current phase {FormatTimeAgo(sinceTransition)} ago.");
        }

        // Gate conditions
        var metGates = gates.Count(g => g.IsMet);
        var totalGates = gates.Count;
        if (currentPhase < ProjectPhase.Completion)
            observations.Add($"Next phase gates: {metGates}/{totalGates} met.");

        // Agent health
        if (nonCompliantCount > 0)
        {
            var ncAgents = agents.Where(a => !a.DiagnosticCompliant)
                .Select(a => a.DisplayName);
            observations.Add($"⚠️ {nonCompliantCount} agent(s) report non-compliant behavior: {string.Join(", ", ncAgents)}.");
        }
        else if (agents.Count > 0)
        {
            observations.Add("All agents report compliant behavior.");
        }

        if (hasDeadlock && deadlockCycle is not null)
            observations.Add($"🔴 Deadlock detected involving: {string.Join(" → ", deadlockCycle)}.");

        if (errorCount > 0)
        {
            var errAgents = agents.Where(a => a.ErrorCount > 0)
                .Select(a => $"{a.DisplayName} ({a.ErrorCount})");
            observations.Add($"⚠️ {errorCount} agent(s) have errors: {string.Join(", ", errAgents)}.");
        }

        // Working agents
        if (workingCount > 0)
        {
            var workingNames = agents.Where(a => a.Status == AgentStatus.Working)
                .Select(a => a.DisplayName);
            observations.Add($"Currently working: {string.Join(", ", workingNames)}.");
        }
        else
        {
            observations.Add("No agents are currently working — all idle or waiting.");
        }

        // Blocked agents
        var blockedAgents = agents.Where(a => a.Status == AgentStatus.Blocked).ToList();
        if (blockedAgents.Count > 0)
        {
            var blocked = blockedAgents.Select(a => $"{a.DisplayName}: {a.StatusReason ?? "unknown"}");
            observations.Add($"🟡 Blocked agents: {string.Join("; ", blocked)}.");
        }

        // Overall status determination
        var overallStatus = "Healthy";
        if (hasDeadlock) overallStatus = "Critical";
        else if (nonCompliantCount > 0 || blockedAgents.Count > 0) overallStatus = "Warning";
        else if (errorCount > 0) overallStatus = "Caution";

        // Build phase timeline
        var allPhases = Enum.GetValues<ProjectPhase>();
        var completedTransitions = transitionHistory
            .GroupBy(t => t.OldPhase)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Timestamp).Last().Timestamp);

        var timeline = allPhases.Select(p => new PhaseTimelineEntry
        {
            Phase = FormatPhase(p),
            IsCompleted = p < currentPhase,
            IsCurrent = p == currentPhase,
            CompletedAt = completedTransitions.GetValueOrDefault(p)
        }).ToList();

        return new ExecutionHealthAssessment
        {
            OverallStatus = overallStatus,
            Phase = FormatPhase(currentPhase),
            Uptime = DateTime.UtcNow - _startedAt,
            TotalAgents = agents.Count,
            WorkingAgents = workingCount,
            CompliantAgents = compliantCount,
            NonCompliantAgents = nonCompliantCount,
            ErrorAgents = errorCount,
            HasDeadlock = hasDeadlock,
            Observations = observations,
            NextPhaseGates = gates.ToList(),
            PhaseTimeline = timeline
        };
    }

    private static string FormatPhase(ProjectPhase phase) => phase switch
    {
        ProjectPhase.EngineeringPlanning => "Engineering Planning",
        ProjectPhase.ParallelDevelopment => "Parallel Development",
        _ => phase.ToString()
    };

    private static string FormatTimeAgo(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    // --- Pull Request data for dashboard ---

    /// <summary>True when GitHub API rate limit is active — dashboard should show cached data.</summary>
    public bool IsGitHubRateLimited => _rateLimitManager.IsRateLimited;

    public string RepositoryFullName => _github.RepositoryFullName;

    public GitHubRateLimitInfo GetRateLimitInfo() => new()
    {
        Remaining = _rateLimitManager.Remaining == int.MaxValue ? 5000 : _rateLimitManager.Remaining,
        Limit = 5000,
        ResetAt = _rateLimitManager.ResetAtUtc,
        TotalApiCalls = _rateLimitManager.TotalApiCalls,
        IsRateLimited = _rateLimitManager.IsRateLimited
    };

    /// <summary>Short timeout for dashboard API calls to avoid blocking on rate limiter semaphore contention.</summary>
    private static readonly TimeSpan DashboardApiTimeout = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<AgentPullRequest>> GetPullRequestsAsync()
    {
        if (DateTime.UtcNow - _lastPrFetchUtc < PrCacheExpiry && _cachedPullRequests.Count > 0)
            return _cachedPullRequests;

        if (_rateLimitManager.IsRateLimited)
        {
            _logger.LogDebug("Skipping PR fetch — GitHub API is rate-limited");
            return _cachedPullRequests;
        }

        try
        {
            // Use a short timeout to avoid blocking the dashboard when agents saturate the rate limiter semaphore.
            // If the call times out, return whatever cache we have and schedule a background refresh.
            using var cts = new CancellationTokenSource(DashboardApiTimeout);
            var allPrs = await _github.GetAllPullRequestsAsync(cts.Token);
            _cachedPullRequests = allPrs.ToList();
            _lastPrFetchUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("PR fetch timed out (semaphore contention) — returning cached data, scheduling background refresh");
            ScheduleBackgroundRefresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch PRs for dashboard");
        }

        return _cachedPullRequests;
    }

    // --- Issue data for dashboard ---

    public async Task<IReadOnlyList<AgentIssue>> GetIssuesAsync()
    {
        if (DateTime.UtcNow - _lastIssueFetchUtc < PrCacheExpiry && _cachedIssues.Count > 0)
            return _cachedIssues;

        if (_rateLimitManager.IsRateLimited)
        {
            _logger.LogDebug("Skipping issue fetch — GitHub API is rate-limited");
            return _cachedIssues;
        }

        try
        {
            using var cts = new CancellationTokenSource(DashboardApiTimeout);
            var allIssues = await _github.GetAllIssuesAsync(cts.Token);
            _logger.LogInformation("Issue fetch: total={Total} issues from GitHub", allIssues.Count);
            _cachedIssues = allIssues.ToList();
            _lastIssueFetchUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Issue fetch timed out (semaphore contention) — returning cached data, scheduling background refresh");
            ScheduleBackgroundRefresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch issues for dashboard");
        }

        return _cachedIssues;
    }

    private int _backgroundRefreshScheduled;

    /// <summary>
    /// Schedule a background data refresh after a short delay — allows rate limiter semaphore contention to clear.
    /// </summary>
    private void ScheduleBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _backgroundRefreshScheduled, 1, 0) != 0)
            return; // Already scheduled

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));

                // Retry issues
                try
                {
                    var allIssues = await _github.GetAllIssuesAsync();
                    _cachedIssues = allIssues.ToList();
                    _lastIssueFetchUtc = DateTime.UtcNow;
                    _logger.LogInformation("Background refresh: loaded {Count} issues", _cachedIssues.Count);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Background issue refresh failed"); }

                // Retry PRs
                try
                {
                    var allPrs = await _github.GetAllPullRequestsAsync();
                    _cachedPullRequests = allPrs.ToList();
                    _lastPrFetchUtc = DateTime.UtcNow;
                    _logger.LogInformation("Background refresh: loaded {Count} PRs", _cachedPullRequests.Count);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Background PR refresh failed"); }

                NotifyStateChanged();
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundRefreshScheduled, 0);
            }
        });
    }

    // Observable event for Blazor components to subscribe to for re-rendering
    public event Action? OnChange;
    private void NotifyStateChanged() => OnChange?.Invoke();
}
