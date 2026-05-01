using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Steps;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Diagnostics;
using AgentSquad.Core.Persistence;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// Manages the in-memory cache of agent snapshots, errors, and tracked agent instances.
/// Pure data/logic service — no SignalR, no event subscriptions. The facade coordinates events.
/// </summary>
public sealed class AgentSnapshotService
{
    private readonly ModelRegistry _modelRegistry;
    private readonly AgentStateStore _stateStore;
    private readonly AgentChatService _chatService;
    private readonly IAgentTaskTracker? _taskTracker;
    private readonly ActiveLlmCallTracker? _llmCallTracker;
    private readonly ILogger<AgentSnapshotService> _logger;

    private readonly Dictionary<string, AgentSnapshot> _agentCache = new();
    private readonly Dictionary<string, List<AgentLogEntry>> _agentErrors = new();
    private readonly Dictionary<string, IAgent> _trackedAgents = new();
    private readonly object _lock = new();

    public AgentSnapshotService(
        ModelRegistry modelRegistry,
        AgentStateStore stateStore,
        AgentChatService chatService,
        ILogger<AgentSnapshotService> logger,
        IAgentTaskTracker? taskTracker = null,
        ActiveLlmCallTracker? llmCallTracker = null)
    {
        _modelRegistry = modelRegistry;
        _stateStore = stateStore;
        _chatService = chatService;
        _logger = logger;
        _taskTracker = taskTracker;
        _llmCallTracker = llmCallTracker;
    }

    public IReadOnlyList<AgentSnapshot> GetAll()
    {
        lock (_lock) { return _agentCache.Values.ToList(); }
    }

    public AgentSnapshot? Get(string agentId)
    {
        lock (_lock) { return _agentCache.GetValueOrDefault(agentId); }
    }

    public IReadOnlyList<AgentLogEntry> GetErrors(string agentId)
    {
        lock (_lock)
        {
            return _agentErrors.TryGetValue(agentId, out var errors) ? errors.ToList() : [];
        }
    }

    /// <summary>Clears tracked errors for a specific agent and updates the snapshot. Returns the agent for external notification.</summary>
    public IAgent? ClearErrors(string agentId)
    {
        lock (_lock)
        {
            if (_agentErrors.ContainsKey(agentId))
                _agentErrors[agentId].Clear();
            if (_agentCache.TryGetValue(agentId, out var snapshot))
                _agentCache[agentId] = snapshot with { ErrorCount = 0 };
        }

        _trackedAgents.TryGetValue(agentId, out var agent);
        agent?.ClearErrors();
        return agent;
    }

    public async Task<IReadOnlyList<ActivityLogEntry>> GetActivityLogAsync(
        string agentId, int count = 100, CancellationToken ct = default)
    {
        try { return await _stateStore.GetRecentActivityAsync(agentId, count, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve activity log for agent {AgentId}", agentId);
            return [];
        }
    }

    public IReadOnlyList<string> GetAvailableModels() => ModelRegistry.AvailableCopilotModels;

    /// <summary>Refresh active model for all cached agents from ModelRegistry.</summary>
    public void RefreshActiveModels()
    {
        lock (_lock)
        {
            foreach (var (agentId, snapshot) in _agentCache.ToList())
            {
                var effectiveModel = _modelRegistry.GetEffectiveModel(agentId);
                if (snapshot.ActiveModel != effectiveModel)
                    _agentCache[agentId] = snapshot with { ActiveModel = effectiveModel };
            }
        }
    }

    /// <summary>Change the model for a specific agent at runtime.</summary>
    public void SetAgentModel(string agentId, string modelName)
    {
        _modelRegistry.SetAgentModelOverride(agentId, modelName);
        lock (_lock)
        {
            if (_agentCache.TryGetValue(agentId, out var snapshot))
                _agentCache[agentId] = snapshot with { ActiveModel = modelName };
        }
    }

    /// <summary>Seed from live registry agents.</summary>
    public void SeedFromRegistry(IReadOnlyList<IAgent> agents)
    {
        lock (_lock)
        {
            foreach (var agent in agents)
            {
                _agentCache[agent.Identity.Id] = ToSnapshot(agent);
                _trackedAgents[agent.Identity.Id] = agent;
            }
        }

        if (agents.Count == 0)
            SeedFromDatabase();
    }

    /// <summary>Populate cache from DB when registry is empty (standalone mode).</summary>
    public void SeedFromDatabase()
    {
        try
        {
            var usageMap = _stateStore.LoadAllAiUsage();
            var activityMap = _stateStore.GetLatestActivityPerAgent();
            var bootUtc = _stateStore.GetLastBootUtc();

            var allAgentIds = new HashSet<string>(usageMap.Keys);
            foreach (var id in activityMap.Keys) allAgentIds.Add(id);
            if (allAgentIds.Count == 0) return;

            var activeIds = allAgentIds
                .Where(id => activityMap.TryGetValue(id, out var a) && a.Timestamp >= bootUtc)
                .OrderBy(id => id)
                .ToList();

            var roleCounters = new Dictionary<AgentRole, int>();

            lock (_lock)
            {
                var staleIds = _agentCache.Keys
                    .Where(k => !activeIds.Contains(k) && !_trackedAgents.ContainsKey(k)).ToList();
                foreach (var id in staleIds) _agentCache.Remove(id);

                foreach (var agentId in activeIds)
                {
                    var usage = usageMap.GetValueOrDefault(agentId);
                    activityMap.TryGetValue(agentId, out var activity);
                    var role = InferRole(agentId);

                    roleCounters.TryGetValue(role, out var idx);
                    roleCounters[role] = idx + 1;

                    var inferredStatus = AgentStatus.Online;
                    var statusReason = activity.Details ?? "";
                    if (activity.EventType == "status" && !string.IsNullOrEmpty(activity.Details))
                    {
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seed agent cache from database");
        }
    }

    /// <summary>Handle agent registration — add to cache and track.</summary>
    public AgentSnapshot HandleAgentRegistered(IAgent agent)
    {
        var snapshot = ToSnapshot(agent);
        lock (_lock)
        {
            _agentCache[agent.Identity.Id] = snapshot;
            _trackedAgents[agent.Identity.Id] = agent;
        }
        _ = _stateStore.LogActivityAsync(agent.Identity.Id, "system",
            $"Agent registered: {agent.Identity.DisplayName} ({agent.Identity.Role})");
        return snapshot;
    }

    /// <summary>Handle agent unregistration — remove from cache.</summary>
    public void HandleAgentUnregistered(string agentId)
    {
        lock (_lock)
        {
            _agentCache.Remove(agentId);
            _agentErrors.Remove(agentId);
            _trackedAgents.Remove(agentId);
        }
    }

    /// <summary>Handle status change — update snapshot cache.</summary>
    public void HandleStatusChanged(AgentStatusChangedEventArgs e)
    {
        lock (_lock)
        {
            if (!_agentCache.TryGetValue(e.Agent.Id, out var cached)) return;

            var statusChangeTime = e.OldStatus != e.NewStatus
                ? DateTime.UtcNow
                : cached.LastStatusChange;

            AgentTaskStep? currentStep = null;
            string? taskName = null;
            if (e.NewStatus == AgentStatus.Working)
            {
                currentStep = _taskTracker?.GetCurrentStep(e.Agent.Id);
                if (currentStep is not null && _taskTracker is not null)
                {
                    taskName = _taskTracker.GetGroupedSteps(e.Agent.Id)
                        .FirstOrDefault(g => g.TaskId == currentStep.TaskId)?.DisplayName;
                }
            }

            _agentCache[e.Agent.Id] = cached with
            {
                Status = e.NewStatus,
                StatusReason = e.Reason,
                LastStatusChange = statusChangeTime,
                AssignedPullRequest = e.Agent.AssignedPullRequest,
                ActiveModel = _modelRegistry.GetEffectiveModel(e.Agent.Id),
                CurrentTaskName = taskName,
                CurrentStepName = currentStep?.Name,
                CurrentStepDescription = currentStep?.Description
            };
        }
    }

    /// <summary>Handle errors changed — update error cache and snapshot.</summary>
    public void HandleErrorsChanged(IAgent agent)
    {
        var agentId = agent.Identity.Id;
        var currentErrors = agent.RecentErrors;

        lock (_lock)
        {
            _agentErrors[agentId] = currentErrors.ToList();
            if (_agentCache.TryGetValue(agentId, out var snapshot))
                _agentCache[agentId] = snapshot with { ErrorCount = currentErrors.Count };
        }
    }

    /// <summary>Handle diagnostic changed — update snapshot cache. Returns display name for external use.</summary>
    public string? HandleDiagnosticChanged(DiagnosticChangedEventArgs e)
    {
        lock (_lock)
        {
            if (!_agentCache.TryGetValue(e.AgentId, out var snapshot)) return null;

            _agentCache[e.AgentId] = snapshot with
            {
                DiagnosticSummary = e.Diagnostic.Summary,
                DiagnosticJustification = e.Diagnostic.Justification,
                DiagnosticCompliant = e.Diagnostic.IsCompliant,
                DiagnosticComplianceIssue = e.Diagnostic.ComplianceIssue,
                DiagnosticScenarioRef = e.Diagnostic.ScenarioRef
            };

            return snapshot.DisplayName;
        }
    }

    /// <summary>Refresh usage stats for all cached agents from the usage tracker.</summary>
    public void RefreshUsageStats()
    {
        lock (_lock)
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

    /// <summary>Refresh current task step info for all cached agents.</summary>
    public bool RefreshTaskSteps()
    {
        if (_taskTracker is null) return false;

        lock (_lock)
        {
            var changed = false;
            foreach (var (agentId, snapshot) in _agentCache.ToList())
            {
                AgentTaskStep? step = null;
                string? stepName = null;
                string? stepDesc = null;
                string? taskName = null;

                if (snapshot.Status == AgentStatus.Working)
                {
                    step = _taskTracker.GetCurrentStep(agentId);
                    stepName = step?.Name;
                    stepDesc = step?.Description;
                    if (step is not null)
                    {
                        taskName = _taskTracker.GetGroupedSteps(agentId)
                            .FirstOrDefault(g => g.TaskId == step.TaskId)?.DisplayName;
                    }
                }

                if (snapshot.CurrentStepName != stepName || snapshot.CurrentStepDescription != stepDesc
                    || snapshot.CurrentTaskName != taskName)
                {
                    _agentCache[agentId] = snapshot with
                    {
                        CurrentTaskName = taskName,
                        CurrentStepName = stepName,
                        CurrentStepDescription = stepDesc
                    };
                    changed = true;
                }
            }
            return changed;
        }
    }

    public decimal GetTotalEstimatedCost() => _modelRegistry.UsageTracker.GetTotalCost();
    public int GetTotalAiCalls() => _modelRegistry.UsageTracker.GetAllStats().Values.Sum(s => s.TotalCalls);

    public async Task<AgentChatMessage> SendAgentChatAsync(
        string agentId, string message, CancellationToken ct = default)
    {
        IAgent? agent;
        lock (_lock) { _trackedAgents.TryGetValue(agentId, out agent); }

        if (agent is null)
            return new AgentChatMessage { Role = "assistant", Content = "⚠️ Agent not found or no longer registered." };

        return await _chatService.SendMessageAsync(agent, message, ct);
    }

    public IReadOnlyList<AgentChatMessage> GetAgentChatHistory(string agentId) =>
        _chatService.GetHistory(agentId);

    public void ClearAgentChat(string agentId) => _chatService.ClearHistory(agentId);

    /// <summary>Resolve agent display name from cache for milestone detection.</summary>
    public string GetAgentDisplayName(string agentId)
    {
        lock (_lock)
        {
            return _agentCache.TryGetValue(agentId, out var snap) ? snap.DisplayName : agentId;
        }
    }

    /// <summary>Clear all cached data. Called by facade during project reset.</summary>
    public void ResetCaches()
    {
        lock (_lock)
        {
            _agentCache.Clear();
            _agentErrors.Clear();
            _trackedAgents.Clear();
        }
    }

    /// <summary>Returns tracked agent instances that need per-agent event subscriptions.</summary>
    public IReadOnlyDictionary<string, IAgent> GetTrackedAgents()
    {
        lock (_lock) { return new Dictionary<string, IAgent>(_trackedAgents); }
    }

    private AgentSnapshot ToSnapshot(IAgent agent)
    {
        var usage = _modelRegistry.UsageTracker.GetStats(agent.Identity.Id);
        var diag = agent.CurrentDiagnostic;
        var currentStep = _taskTracker?.GetCurrentStep(agent.Identity.Id);
        string? taskName = null;
        if (currentStep is not null && _taskTracker is not null)
        {
            taskName = _taskTracker.GetGroupedSteps(agent.Identity.Id)
                .FirstOrDefault(g => g.TaskId == currentStep.TaskId)?.DisplayName;
        }

        var effectiveStatus = agent.Status;
        var effectiveReason = agent.StatusReason;
        var activeCall = _llmCallTracker?.GetActiveCall(agent.Identity.Id);
        if (activeCall is not null && effectiveStatus is not AgentStatus.Working)
        {
            effectiveStatus = AgentStatus.Working;
            effectiveReason = $"AI call in progress ({activeCall.ModelName})";
        }

        return new()
        {
            Id = agent.Identity.Id,
            DisplayName = agent.Identity.DisplayName,
            Role = agent.Identity.Role,
            ModelTier = agent.Identity.ModelTier,
            Status = effectiveStatus,
            StatusReason = effectiveReason,
            CreatedAt = agent.Identity.CreatedAt,
            AssignedPullRequest = agent.Identity.AssignedPullRequest,
            Specialty = agent.Identity.Role == AgentRole.Custom ? agent.Identity.DisplayName : null,
            Capabilities = agent.Identity.Capabilities,
            ActiveModel = _modelRegistry.GetEffectiveModel(agent.Identity.Id),
            LastStatusChange = DateTime.UtcNow,
            ErrorCount = agent.RecentErrors.Count,
            CurrentTaskName = taskName,
            CurrentStepName = currentStep?.Name,
            CurrentStepDescription = currentStep?.Description,
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

    internal static AgentRole InferRole(string agentId)
    {
        if (agentId.StartsWith("programmanager", StringComparison.OrdinalIgnoreCase)) return AgentRole.ProgramManager;
        if (agentId.StartsWith("researcher", StringComparison.OrdinalIgnoreCase)) return AgentRole.Researcher;
        if (agentId.StartsWith("architect", StringComparison.OrdinalIgnoreCase)) return AgentRole.Architect;
        if (agentId.StartsWith("softwareengineer", StringComparison.OrdinalIgnoreCase)) return AgentRole.SoftwareEngineer;
        if (agentId.StartsWith("testengineer", StringComparison.OrdinalIgnoreCase)) return AgentRole.TestEngineer;
        return AgentRole.SoftwareEngineer;
    }

    internal static string FormatDisplayName(string agentId, AgentRole role, int indexInRole)
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
}
