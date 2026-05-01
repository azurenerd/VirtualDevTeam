using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.Diagnostics;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Persistence;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Orchestrator;
using Microsoft.AspNetCore.SignalR;

namespace AgentSquad.Dashboard.Services;

// ── Record types used by the dashboard ──

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

    /// <summary>Current parent task name (e.g., "PM Specification", "Architecture Design").</summary>
    public string? CurrentTaskName { get; init; }

    /// <summary>Current task tracker step name (e.g., "Generate PM Spec", "Review PR #5").</summary>
    public string? CurrentStepName { get; init; }

    /// <summary>Current task tracker step description for tooltip.</summary>
    public string? CurrentStepDescription { get; init; }

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

/// <summary>
/// Facade over dashboard sub-services. Owns event subscriptions, SignalR broadcasting,
/// the 10-second polling loop, and platform data (PRs, work items, repo browsing).
/// Delegates agent snapshot, diagnostic, and timeline data to focused sub-services.
/// </summary>
public sealed class DashboardDataService : BackgroundService, IDashboardDataService
{
    private readonly AgentSnapshotService _snapshots;
    private readonly DiagnosticSummaryService _diagnostics;
    private readonly ExecutionTimelineService _timeline;
    private readonly AgentRegistry _registry;
    private readonly WorkflowStateMachine _workflow;
    private readonly AgentStateStore _stateStore;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly IPullRequestService _pullRequestService;
    private readonly IWorkItemService _workItemService;
    private readonly IPlatformInfoService _platformInfo;
    private readonly IPlatformHostContext _platformHost;
    private readonly RateLimitManager _rateLimitManager;
    private readonly IRepositoryContentService? _repositoryContentService;
    private readonly IRunBranchProvider? _branchProvider;
    private readonly AgentSquad.Core.AI.RoleContextProvider? _roleContextProvider;
    private readonly ILogger<DashboardDataService> _logger;

    private IReadOnlyList<PlatformPullRequest> _cachedPullRequests = Array.Empty<PlatformPullRequest>();
    private DateTime _lastPrFetchUtc = DateTime.MinValue;
    private IReadOnlyList<PlatformWorkItem> _cachedIssues = Array.Empty<PlatformWorkItem>();
    private DateTime _lastIssueFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan PrCacheExpiry = TimeSpan.FromSeconds(30);

    public DashboardDataService(
        AgentSnapshotService snapshots,
        DiagnosticSummaryService diagnostics,
        ExecutionTimelineService timeline,
        AgentRegistry registry,
        WorkflowStateMachine workflow,
        AgentStateStore stateStore,
        IHubContext<AgentHub> hubContext,
        IPullRequestService pullRequestService,
        IWorkItemService workItemService,
        IPlatformInfoService platformInfo,
        IPlatformHostContext platformHost,
        RateLimitManager rateLimitManager,
        ILogger<DashboardDataService> logger,
        IRepositoryContentService? repositoryContentService = null,
        IRunBranchProvider? branchProvider = null,
        AgentSquad.Core.AI.RoleContextProvider? roleContextProvider = null)
    {
        _snapshots = snapshots;
        _diagnostics = diagnostics;
        _timeline = timeline;
        _registry = registry;
        _workflow = workflow;
        _stateStore = stateStore;
        _hubContext = hubContext;
        _pullRequestService = pullRequestService;
        _workItemService = workItemService;
        _platformInfo = platformInfo;
        _platformHost = platformHost;
        _rateLimitManager = rateLimitManager;
        _logger = logger;
        _repositoryContentService = repositoryContentService;
        _branchProvider = branchProvider;
        _roleContextProvider = roleContextProvider;
    }

    // ── BackgroundService lifecycle ──

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _registry.AgentRegistered += OnAgentRegistered;
        _registry.AgentUnregistered += OnAgentUnregistered;
        _registry.AgentStatusChanged += OnAgentStatusChanged;
        _workflow.PhaseChanged += OnPhaseChanged;

        _timeline.RecordMilestone("🚀", "Session Started", "AgentSquad pipeline initialized", "phase");
        SeedCache();

        _logger.LogInformation("Dashboard data service started");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PushHealthSnapshot(stoppingToken);
                if (_registry.GetAllAgents().Count == 0)
                    _snapshots.SeedFromDatabase();
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

            // Unsubscribe from all live agent events to avoid leaks
            foreach (var agent in _registry.GetAllAgents())
                UnsubscribeFromAgentEvents(agent);
        }
    }

    private void SeedCache()
    {
        var agents = _registry.GetAllAgents();
        _snapshots.SeedFromRegistry(agents);

        // Subscribe to per-agent events for live agents
        foreach (var agent in agents)
            SubscribeToAgentEvents(agent);
    }

    // ── IDashboardDataService: Agent snapshots (delegate to AgentSnapshotService) ──

    public IReadOnlyList<AgentSnapshot> GetAllAgentSnapshots() => _snapshots.GetAll();
    public AgentSnapshot? GetAgentSnapshot(string agentId) => _snapshots.Get(agentId);

    public IReadOnlyList<AgentLogEntry> GetAgentErrors(string agentId) => _snapshots.GetErrors(agentId);

    public void ClearAgentErrors(string agentId)
    {
        _snapshots.ClearErrors(agentId);
        _ = _hubContext.Clients.All.SendAsync("AgentErrorsCleared", agentId);
        NotifyStateChanged();
    }

    public Task<IReadOnlyList<ActivityLogEntry>> GetActivityLogAsync(
        string agentId, int count = 100, CancellationToken ct = default)
        => _snapshots.GetActivityLogAsync(agentId, count, ct);

    // ── IDashboardDataService: Model management (delegate to AgentSnapshotService) ──

    public IReadOnlyList<string> GetAvailableModels() => _snapshots.GetAvailableModels();

    public void RefreshActiveModels()
    {
        _snapshots.RefreshActiveModels();
        _ = _hubContext.Clients.All.SendAsync("ModelsRefreshed");
        NotifyStateChanged();
    }

    public void SetAgentModel(string agentId, string modelName)
    {
        _snapshots.SetAgentModel(agentId, modelName);
        _ = _hubContext.Clients.All.SendAsync("AgentModelChanged", new { AgentId = agentId, Model = modelName });
        NotifyStateChanged();
    }

    // ── IDashboardDataService: Health & diagnostics (delegate to DiagnosticSummaryService) ──

    public AgentHealthSnapshot GetCurrentHealthSnapshot() => _diagnostics.GetCurrentHealthSnapshot();
    public bool HasDeadlock(out List<string>? cycle) => _diagnostics.HasDeadlock(out cycle);
    public ExecutionHealthAssessment GetExecutionHealthAssessment() => _diagnostics.GetExecutionHealthAssessment();

    public IReadOnlyList<DiagnosticHistoryEntry> GetDiagnosticHistory(
        string? agentIdFilter = null, bool? compliantFilter = null, int limit = 200)
        => _diagnostics.GetDiagnosticHistory(agentIdFilter, compliantFilter, limit);

    // ── IDashboardDataService: Timeline (delegate to ExecutionTimelineService) ──

    public IReadOnlyList<ExecutionMilestone> GetExecutionTimeline() => _timeline.GetExecutionTimeline();

    // ── IDashboardDataService: Agent chat (delegate to AgentSnapshotService) ──

    public Task<AgentChatMessage> SendAgentChatAsync(
        string agentId, string message, CancellationToken ct = default)
        => _snapshots.SendAgentChatAsync(agentId, message, ct);

    public IReadOnlyList<AgentChatMessage> GetAgentChatHistory(string agentId) =>
        _snapshots.GetAgentChatHistory(agentId);

    public void ClearAgentChat(string agentId) => _snapshots.ClearAgentChat(agentId);

    // ── IDashboardDataService: Cost tracking (delegate to AgentSnapshotService) ──

    public decimal GetTotalEstimatedCost() => _snapshots.GetTotalEstimatedCost();
    public int GetTotalAiCalls() => _snapshots.GetTotalAiCalls();

    // ── IDashboardDataService: Cache management ──

    public void ResetCaches()
    {
        _snapshots.ResetCaches();
        _diagnostics.ResetCaches();
        _timeline.ResetCaches();
        _cachedPullRequests = Array.Empty<PlatformPullRequest>();
        _cachedIssues = Array.Empty<PlatformWorkItem>();
        _lastPrFetchUtc = DateTime.MinValue;
        _lastIssueFetchUtc = DateTime.MinValue;

        _timeline.RecordMilestone("🔄", "Project Reset", "Repository cleaned and agents restarted", "phase");
        _logger.LogInformation("Dashboard caches reset");
    }

    // ── IDashboardDataService: Agent Role Description ──

    public AgentRoleDescriptionInfo? GetAgentRoleDescription(string agentId)
    {
        if (_roleContextProvider is null) return null;

        var agent = _registry.GetAgent(agentId);
        if (agent is null) return null;

        var identity = agent.Identity;
        var customName = ResolveCustomAgentName(identity);

        var hasOverride = _roleContextProvider.TryGetRoleDescriptionOverride(identity.Role, customName, out var overrideText);
        var configuredDescription = _roleContextProvider.GetConfiguredRoleDescription(identity.Role, customName);
        var effectiveDescription = hasOverride ? overrideText : configuredDescription;

        return new AgentRoleDescriptionInfo(
            identity.Id, identity.DisplayName, identity.Role.ToString(),
            effectiveDescription, overrideText, configuredDescription, hasOverride);
    }

    public void SaveAgentRoleOverride(string agentId, string description)
    {
        if (_roleContextProvider is null) return;

        var agent = _registry.GetAgent(agentId);
        if (agent is null) return;

        var identity = agent.Identity;
        var customName = ResolveCustomAgentName(identity);

        _roleContextProvider.SetRoleDescriptionOverride(identity.Role, description, customName);
        _roleContextProvider.PersistOverride(_stateStore, identity.Role, customName, description);
    }

    public bool ClearAgentRoleOverride(string agentId)
    {
        if (_roleContextProvider is null) return false;

        var agent = _registry.GetAgent(agentId);
        if (agent is null) return false;

        var identity = agent.Identity;
        var customName = ResolveCustomAgentName(identity);

        var cleared = _roleContextProvider.ClearRoleDescriptionOverride(identity.Role, customName);
        _roleContextProvider.ClearPersistedOverride(_stateStore, identity.Role, customName);
        return cleared;
    }

    /// <summary>
    /// Resolves the stable custom agent name for cache key derivation.
    /// For Custom role agents, uses CustomAgentName. For SME agents with a
    /// display name different from the role, uses DisplayName.
    /// </summary>
    private static string? ResolveCustomAgentName(AgentIdentity identity)
    {
        // Use CustomAgentName (same key the agent uses when calling GetRoleSystemContext)
        // For Custom agents: CustomAgentName is the custom name
        // For SME agents: CustomAgentName is "sme:{definitionId}"
        // For built-in agents: CustomAgentName is null
        if (!string.IsNullOrWhiteSpace(identity.CustomAgentName))
            return identity.CustomAgentName;
        return null;
    }

    // ── Event handlers (facade owns all event subscriptions) ──

    private void SubscribeToAgentEvents(IAgent agent)
    {
        agent.ErrorsChanged += OnAgentErrorsChanged;
        agent.ActivityLogged += OnAgentActivityLogged;
        agent.DiagnosticChanged += OnAgentDiagnosticChanged;
    }

    private void UnsubscribeFromAgentEvents(IAgent agent)
    {
        agent.ErrorsChanged -= OnAgentErrorsChanged;
        agent.ActivityLogged -= OnAgentActivityLogged;
        agent.DiagnosticChanged -= OnAgentDiagnosticChanged;
    }

    private void OnAgentRegistered(object? sender, AgentRegistryChangedEventArgs e)
    {
        var snapshot = _snapshots.HandleAgentRegistered(e.Agent);
        SubscribeToAgentEvents(e.Agent);

        _ = _hubContext.Clients.All.SendAsync("AgentRegistered", snapshot);
        NotifyStateChanged();
    }

    private void OnAgentUnregistered(object? sender, AgentRegistryChangedEventArgs e)
    {
        UnsubscribeFromAgentEvents(e.Agent);
        _snapshots.HandleAgentUnregistered(e.Agent.Identity.Id);

        _ = _hubContext.Clients.All.SendAsync("AgentUnregistered", e.Agent.Identity.Id);
        NotifyStateChanged();
    }

    private void OnAgentStatusChanged(object? sender, AgentStatusChangedEventArgs e)
    {
        _snapshots.HandleStatusChanged(e);

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
            var agentName = _snapshots.GetAgentDisplayName(e.Agent.Id);
            _timeline.DetectActivityMilestone(new AgentActivityEventArgs
            {
                AgentId = e.Agent.Id,
                EventType = "status",
                Details = e.Reason
            }, agentName);
        }

        NotifyStateChanged();
    }

    private void OnAgentErrorsChanged(object? sender, EventArgs e)
    {
        if (sender is not IAgent agent) return;
        _snapshots.HandleErrorsChanged(agent);

        _ = _hubContext.Clients.All.SendAsync("AgentErrorsUpdated", new
        {
            AgentId = agent.Identity.Id,
            ErrorCount = agent.RecentErrors.Count
        });
        NotifyStateChanged();
    }

    private void OnAgentDiagnosticChanged(object? sender, DiagnosticChangedEventArgs e)
    {
        var displayName = _snapshots.HandleDiagnosticChanged(e);
        if (displayName is not null)
        {
            var snapshot = _snapshots.Get(e.AgentId);
            if (snapshot is not null)
                _diagnostics.RecordDiagnostic(e, displayName, snapshot.Role);
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

            var agentName = _snapshots.GetAgentDisplayName(e.AgentId);
            _timeline.DetectActivityMilestone(e, agentName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist activity log for agent {AgentId}", e.AgentId);
        }
    }

    private void OnPhaseChanged(object? sender, PhaseTransitionEventArgs e) =>
        _timeline.HandlePhaseChanged(e);

    // ── Periodic health push ──

    private async Task PushHealthSnapshot(CancellationToken ct)
    {
        try
        {
            var healthSnapshot = _diagnostics.RefreshHealthSnapshot();
            _snapshots.RefreshUsageStats();
            _snapshots.RefreshTaskSteps();
            // Always notify — health, usage stats, or task steps may have changed
            NotifyStateChanged();
            await _hubContext.Clients.All.SendAsync("HealthUpdate", healthSnapshot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push health snapshot");
        }
    }

    // ── Platform data (PRs, work items, rate limiting, repo browsing) ──

    public bool IsRateLimited => _rateLimitManager.IsRateLimited;

    public string RepositoryDisplayName => _platformInfo.RepositoryDisplayName;
    public string PlatformName => _platformHost is Core.DevPlatform.Providers.AzureDevOps.AdoHostContext
        ? "Azure DevOps" : "GitHub";

    public string GetPullRequestUrl(int prNumber) => _platformHost.GetPullRequestWebUrl(prNumber);
    public string GetWorkItemUrl(int workItemId) => _platformHost.GetWorkItemWebUrl(workItemId);

    public PlatformRateLimitInfo GetRateLimitInfo() => new()
    {
        Remaining = _rateLimitManager.Remaining == int.MaxValue ? 5000 : _rateLimitManager.Remaining,
        Limit = 5000,
        ResetAt = _rateLimitManager.ResetAtUtc,
        TotalApiCalls = _rateLimitManager.TotalApiCalls,
        IsRateLimited = _rateLimitManager.IsRateLimited,
        PlatformName = PlatformName
    };

    private static readonly TimeSpan DashboardApiTimeout = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<PlatformPullRequest>> GetPullRequestsAsync()
    {
        if (DateTime.UtcNow - _lastPrFetchUtc < PrCacheExpiry && _cachedPullRequests.Count > 0)
            return _cachedPullRequests;

        if (_rateLimitManager.IsRateLimited)
        {
            _logger.LogDebug("Skipping PR fetch — platform API is rate-limited");
            return _cachedPullRequests;
        }

        try
        {
            using var cts = new CancellationTokenSource(DashboardApiTimeout);
            var allPrs = await _pullRequestService.ListAllAsync(cts.Token);
            if (allPrs != null)
            {
                _cachedPullRequests = allPrs.ToList();
                _lastPrFetchUtc = DateTime.UtcNow;
            }
            else
            {
                _logger.LogWarning("PR fetch returned null — keeping cached data");
            }
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

    public async Task<IReadOnlyList<PlatformWorkItem>> GetWorkItemsAsync()
    {
        if (DateTime.UtcNow - _lastIssueFetchUtc < PrCacheExpiry && _cachedIssues.Count > 0)
            return _cachedIssues;

        if (_rateLimitManager.IsRateLimited)
        {
            _logger.LogDebug("Skipping work item fetch — platform API is rate-limited");
            return _cachedIssues;
        }

        try
        {
            using var cts = new CancellationTokenSource(DashboardApiTimeout);
            var allItems = await _workItemService.ListAllAsync(cts.Token);
            if (allItems != null)
            {
                _logger.LogInformation("Work item fetch: total={Total} items from platform", allItems.Count);
                _cachedIssues = allItems.ToList();
                _lastIssueFetchUtc = DateTime.UtcNow;
            }
            else
            {
                _logger.LogWarning("Work item fetch returned null — keeping cached data");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Work item fetch timed out (semaphore contention) — returning cached data, scheduling background refresh");
            ScheduleBackgroundRefresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch work items for dashboard");
        }

        return _cachedIssues;
    }

    private int _backgroundRefreshScheduled;

    private void ScheduleBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _backgroundRefreshScheduled, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));

                try
                {
                    var allItems = await _workItemService.ListAllAsync();
                    if (allItems != null)
                    {
                        _cachedIssues = allItems.ToList();
                        _lastIssueFetchUtc = DateTime.UtcNow;
                        _logger.LogInformation("Background refresh: loaded {Count} work items", _cachedIssues.Count);
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Background work item refresh failed"); }

                try
                {
                    var allPrs = await _pullRequestService.ListAllAsync();
                    if (allPrs != null)
                    {
                        _cachedPullRequests = allPrs.ToList();
                        _lastPrFetchUtc = DateTime.UtcNow;
                        _logger.LogInformation("Background refresh: loaded {Count} PRs", _cachedPullRequests.Count);
                    }
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

    // ── Repository file browsing ──

    public async Task<RepositoryFileTreeResult> GetRepositoryFileTreeAsync(string? branch = null, CancellationToken ct = default)
    {
        var effectiveBranch = branch ?? _branchProvider?.EffectiveBranch ?? "main";
        if (_repositoryContentService is null)
            return new RepositoryFileTreeResult { Branch = effectiveBranch, Files = Array.Empty<string>() };

        var files = await _repositoryContentService.GetRepositoryTreeAsync(effectiveBranch, ct);
        return new RepositoryFileTreeResult { Branch = effectiveBranch, Files = files };
    }

    public async Task<RepositoryFileContentResult?> GetFileContentWithMetadataAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        var effectiveBranch = branch ?? _branchProvider?.EffectiveBranch ?? "main";
        if (_repositoryContentService is null)
            return null;

        if (RepositoryFileContentResult.IsBinaryPath(path))
        {
            return new RepositoryFileContentResult
            {
                Path = path,
                IsBinary = true,
                Content = null,
                ContentType = RepositoryFileContentResult.InferContentType(path)
            };
        }

        var content = await _repositoryContentService.GetFileContentAsync(path, effectiveBranch, ct);
        if (content is null) return null;

        const int maxDisplayBytes = 100 * 1024;
        var wasTruncated = content.Length > maxDisplayBytes;
        var displayContent = wasTruncated ? content[..maxDisplayBytes] : content;

        return new RepositoryFileContentResult
        {
            Path = path,
            IsBinary = false,
            SizeBytes = content.Length,
            Content = displayContent,
            WasTruncated = wasTruncated,
            ContentType = RepositoryFileContentResult.InferContentType(path)
        };
    }

    // ── Change notification for Blazor components ──

    public event Action? OnChange;
    private void NotifyStateChanged() => OnChange?.Invoke();
}
