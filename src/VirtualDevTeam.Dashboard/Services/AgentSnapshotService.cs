using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Steps;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.Diagnostics;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Dashboard.Services;

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
    private readonly IPlatformHostContext? _platformHost;
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
        ActiveLlmCallTracker? llmCallTracker = null,
        IPlatformHostContext? platformHost = null)
    {
        _modelRegistry = modelRegistry;
        _stateStore = stateStore;
        _chatService = chatService;
        _logger = logger;
        _taskTracker = taskTracker;
        _llmCallTracker = llmCallTracker;
        _platformHost = platformHost;
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
        try
        {
            var statusEvents = await _stateStore.GetRecentActivityAsync(agentId, count, ct);

            // 2026-05-12 fix for agent-detail-consolidated-activity-log: merge step events
            // (BeginStep / CompleteStep / RecordSubStep / RecordLlmCall) into the activity
            // stream so the UI shows actual work activities, not just status transitions.
            // Step events come from the in-memory IAgentTaskTracker (no persistence yet);
            // we map them onto the same ActivityLogEntry shape with EventType="task".
            // Negative IDs distinguish synthesized step entries from persisted DB rows.
            if (_taskTracker is null)
                return statusEvents;

            var stepEvents = new List<ActivityLogEntry>();
            try
            {
                var groups = _taskTracker.GetGroupedSteps(agentId);
                long syntheticId = -1;
                foreach (var group in groups)
                {
                    foreach (var step in group.Steps)
                    {
                        if (!step.StartedAt.HasValue) continue;
                        var startedAt = step.StartedAt.Value;
                        // Begin event
                        stepEvents.Add(new ActivityLogEntry(
                            syntheticId--,
                            agentId,
                            startedAt,
                            "task",
                            $"▶ {step.Name}{(string.IsNullOrEmpty(step.Description) ? "" : ": " + step.Description)}"));

                        // Completion event (only if completed/failed/skipped — in-progress steps don't get a second entry)
                        if (step.CompletedAt is { } endAt && step.Status is AgentTaskStepStatus.Completed
                            or AgentTaskStepStatus.Failed or AgentTaskStepStatus.Skipped)
                        {
                            var icon = step.Status switch
                            {
                                AgentTaskStepStatus.Completed => "✓",
                                AgentTaskStepStatus.Failed => "✗",
                                AgentTaskStepStatus.Skipped => "↷",
                                _ => "·",
                            };
                            var elapsed = endAt - startedAt;
                            var llmNote = step.LlmCallCount > 0
                                ? $" — {step.LlmCallCount} LLM call{(step.LlmCallCount == 1 ? "" : "s")}, ${step.EstimatedCost:0.00}"
                                : "";
                            stepEvents.Add(new ActivityLogEntry(
                                syntheticId--,
                                agentId,
                                endAt,
                                "task",
                                $"{icon} {step.Name} ({FormatElapsed(elapsed)}{llmNote})"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fold task-tracker steps into activity log for {AgentId}", agentId);
            }

            // Merge sorted by timestamp DESC, cap at requested count.
            return statusEvents
                .Concat(stepEvents)
                .OrderByDescending(e => e.Timestamp)
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve activity log for agent {AgentId}", agentId);
            return [];
        }
    }

    private static string FormatElapsed(TimeSpan ts) =>
        ts.TotalSeconds < 60 ? $"{ts.TotalSeconds:F0}s"
        : ts.TotalMinutes < 60 ? $"{(int)ts.TotalMinutes}m{ts.Seconds:00}s"
        : $"{(int)ts.TotalHours}h{ts.Minutes:00}m";

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
                CurrentStepDescription = currentStep?.Description,
                CurrentPrNumber = ResolveAgentPrNumber(e.Agent.Id),
                CurrentPrUrl = ResolveAgentPrUrl(e.Agent.Id),
                BlockedReason = ResolveAgentBlockedReason(e.Agent.Id)
            };
        }
    }

    private int? ResolveAgentPrNumber(string agentId)
    {
        _trackedAgents.TryGetValue(agentId, out var agent);
        return agent?.CurrentPrNumber;
    }

    private string? ResolveAgentPrUrl(string agentId)
    {
        var n = ResolveAgentPrNumber(agentId);
        return n is null ? null : _platformHost?.GetPullRequestWebUrl(n.Value);
    }

    private BlockedReason? ResolveAgentBlockedReason(string agentId)
    {
        _trackedAgents.TryGetValue(agentId, out var agent);
        return agent?.CurrentBlockedReason;
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

                // Also pull live PR/BlockedReason from the tracked agent. These can change
                // without a status transition (e.g., engineer sets CurrentPrNumber after
                // opening a PR while remaining in Working state), so the periodic refresh
                // is our backstop for keeping the dashboard fields current.
                var livePrNumber = ResolveAgentPrNumber(agentId);
                var livePrUrl = ResolveAgentPrUrl(agentId);
                var liveBlocked = ResolveAgentBlockedReason(agentId);

                if (snapshot.CurrentStepName != stepName || snapshot.CurrentStepDescription != stepDesc
                    || snapshot.CurrentTaskName != taskName
                    || snapshot.CurrentPrNumber != livePrNumber
                    || snapshot.CurrentPrUrl != livePrUrl
                    || !ReferenceEquals(snapshot.BlockedReason, liveBlocked))
                {
                    _agentCache[agentId] = snapshot with
                    {
                        CurrentTaskName = taskName,
                        CurrentStepName = stepName,
                        CurrentStepDescription = stepDesc,
                        CurrentPrNumber = livePrNumber,
                        CurrentPrUrl = livePrUrl,
                        BlockedReason = liveBlocked
                    };
                    changed = true;
                }
            }
            return changed;
        }
    }

    public decimal GetTotalEstimatedCost() => _modelRegistry.UsageTracker.GetTotalCost();
    public int GetTotalAiCalls() => _modelRegistry.UsageTracker.GetAllStats().Values.Sum(s => s.TotalCalls);
    public int GetTotalPremiumRequests() => _modelRegistry.UsageTracker.GetAllStats().Values.Sum(s => s.PremiumRequests);
    public IReadOnlyDictionary<string, AgentUsageStats> GetAgentUsageStats() => _modelRegistry.UsageTracker.GetAllStats();

    /// <summary>Refresh LLM call overlay for a specific agent when a call starts/completes.</summary>
    public bool RefreshLlmCallState(string agentId)
    {
        lock (_lock)
        {
            if (!_agentCache.TryGetValue(agentId, out var snapshot)) return false;

            var activeCall = _llmCallTracker?.GetActiveCall(agentId);
            TimeSpan? llmElapsed = activeCall is not null ? DateTime.UtcNow - activeCall.StartedAt : null;

            // Only update the LLM elapsed time — don't override the agent's actual status.
            // The UI already shows LLM activity via the separate LlmCallElapsedTime indicator (🤖 AI badge).
            var changed = snapshot.LlmCallElapsedTime != llmElapsed;

            if (changed)
            {
                _agentCache[agentId] = snapshot with
                {
                    LlmCallElapsedTime = llmElapsed
                };
            }
            return changed;
        }
    }

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
            return _agentCache.TryGetValue(agentId, out var snap) ? snap.DisplayName : FormatAgentId(agentId);
        }
    }

    /// <summary>
    /// Formats a raw agent ID into a human-readable display name by stripping the GUID suffix
    /// and title-casing the segments. Use this as a fallback when the agent isn't in the cache.
    /// E.g. "softwareengineer-83e4d82f8e0547feb9821d17a7dde5d3" → "Softwareengineer"
    ///      "software-engineer-1" → "Software Engineer 1"
    /// </summary>
    public static string FormatAgentId(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return agentId;

        // Strip GUID suffix (hex sequences of 8+ chars at the end, possibly with dashes)
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            agentId, @"-[0-9a-f]{8}[0-9a-f-]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Title-case each segment
        return string.Join(' ', cleaned.Split('-').Select(w =>
            w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
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

        // post-mon-stale-step-idle: when the agent is not Working, by definition no step is
        // actively in progress. The tracker may still hold a step record that wasn't explicitly
        // CompleteStep'd (common in error-recovery paths), but surfacing it as the "current
        // step" of an idle/error/blocked agent is misleading. Suppress at the snapshot layer —
        // the underlying tracker still has the history for the timeline view.
        if (agent.Status != AgentStatus.Working)
        {
            currentStep = null;
            taskName = null;
        }

        var effectiveStatus = agent.Status;
        var effectiveReason = agent.StatusReason;
        var activeCall = _llmCallTracker?.GetActiveCall(agent.Identity.Id);
        TimeSpan? llmElapsed = null;
        if (activeCall is not null)
        {
            llmElapsed = DateTime.UtcNow - activeCall.StartedAt;
            // Don't override the agent's actual status — the UI already shows
            // LLM activity via the separate LlmCallElapsedTime indicator (🤖 AI badge).
        }

        // Strip step counter patterns (e.g., "(1/3)", "step 2/4") from status text
        effectiveReason = StripStepCounterPatterns(effectiveReason);

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
            CurrentPrNumber = agent.CurrentPrNumber,
            CurrentPrUrl = agent.CurrentPrNumber is { } pr ? _platformHost?.GetPullRequestWebUrl(pr) : null,
            BlockedReason = agent.CurrentBlockedReason,
            LlmCallElapsedTime = llmElapsed,
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
        if (agentId.StartsWith("securityauditor", StringComparison.OrdinalIgnoreCase)) return AgentRole.SecurityAuditor;
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
            AgentRole.SecurityAuditor => "Security Auditor",
            _ => agentId
        };
        return indexInRole > 0 ? $"{baseName} {indexInRole}" : baseName;
    }

    /// <summary>
    /// Strips step counter patterns like "(1/3)", "step 2/4", "(step 1 of 5)" from status text
    /// to reduce noise in the dashboard display.
    /// </summary>
    private static string? StripStepCounterPatterns(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        // Remove patterns: (1/3), (2/4), step 1/3, Step 2 of 5, (step 1 of 3)
        var result = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\(?\s*[Ss]tep\s+\d+\s*(/\s*\d+|of\s+\d+)\s*\)?|\(\d+/\d+\)",
            "");
        return result.Trim();
    }
}
