using System.Net.Http.Json;
using AgentSquad.Core.Agents;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.Diagnostics;
using AgentSquad.Core.Persistence;
using AgentSquad.Orchestrator;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// HTTP-based implementation of IDashboardDataService for standalone dashboard mode.
/// Calls the Runner's REST API instead of accessing in-process services.
/// </summary>
public sealed class HttpDashboardDataService : IDashboardDataService, IHostedService, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpDashboardDataService> _logger;
    private Timer? _pollTimer;

    // Local caches refreshed by polling
    private IReadOnlyList<AgentSnapshot> _agents = [];
    private AgentHealthSnapshot? _healthSnapshot;
    private IReadOnlyList<ExecutionMilestone> _milestones = [];
    private IReadOnlyList<PlatformPullRequest> _pullRequests = [];
    private IReadOnlyList<PlatformWorkItem> _issues = [];
    private IReadOnlyList<string> _models = [];
    private bool _isRateLimited;
    private PlatformRateLimitInfo _rateLimitInfo = new() { Remaining = 5000, Limit = 5000, PlatformName = "GitHub" };
    private decimal _cachedTotalCost;
    private int _cachedTotalCalls;
    private string _repoFullName = "";

    public event Action? OnChange;

    public HttpDashboardDataService(HttpClient http, ILogger<HttpDashboardDataService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _pollTimer = new Timer(async _ => await PollRunnerAsync(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _pollTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _pollTimer?.Dispose();

    private async Task PollRunnerAsync()
    {
        try
        {
            var agents = await _http.GetFromJsonAsync<List<AgentSnapshot>>("/api/dashboard/agents");
            if (agents is not null) _agents = agents;

            var rl = await _http.GetFromJsonAsync<RateLimitResponse>("/api/dashboard/platform/rate-limited");
            if (rl is not null) _isRateLimited = rl.IsRateLimited;

            var rli = await _http.GetFromJsonAsync<PlatformRateLimitInfo>("/api/dashboard/platform/rate-limit-info");
            if (rli is not null) _rateLimitInfo = rli;

            var cost = await _http.GetFromJsonAsync<CostSummaryResponse>("/api/dashboard/cost-summary");
            if (cost is not null)
            {
                _cachedTotalCost = cost.TotalCost;
                _cachedTotalCalls = cost.TotalCalls;
            }

            if (string.IsNullOrEmpty(_repoFullName))
            {
                var repo = await _http.GetFromJsonAsync<RepoInfoResponse>("/api/dashboard/repo-info");
                if (repo is not null) _repoFullName = repo.FullName;
            }

            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to poll runner API");
        }
    }

    // ── Agent snapshots ──

    public IReadOnlyList<AgentSnapshot> GetAllAgentSnapshots() => _agents;

    public AgentSnapshot? GetAgentSnapshot(string agentId) =>
        _agents.FirstOrDefault(a => a.Id == agentId);

    // ── Health & diagnostics ──

    public AgentHealthSnapshot GetCurrentHealthSnapshot()
    {
        if (_healthSnapshot is not null) return _healthSnapshot;
        try
        {
            var task = _http.GetFromJsonAsync<AgentHealthSnapshot>("/api/dashboard/health/snapshot");
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
            {
                _healthSnapshot = task.Result;
                return _healthSnapshot;
            }
        }
        catch { /* fall through */ }
        return new AgentHealthSnapshot
        {
            StatusCounts = new Dictionary<AgentStatus, int>(),
            TotalAgents = 0
        };
    }

    public bool HasDeadlock(out List<string>? cycle)
    {
        cycle = null;
        try
        {
            var task = _http.GetFromJsonAsync<DeadlockResponse>("/api/dashboard/health/deadlock");
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
            {
                cycle = task.Result.Cycle;
                return task.Result.HasDeadlock;
            }
        }
        catch { /* fall through */ }
        return false;
    }

    public ExecutionHealthAssessment GetExecutionHealthAssessment()
    {
        try
        {
            var task = _http.GetFromJsonAsync<ExecutionHealthAssessment>("/api/dashboard/health/assessment");
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
                return task.Result;
        }
        catch { /* fall through */ }
        return new ExecutionHealthAssessment
        {
            OverallStatus = "Unknown", Phase = "Unknown", Uptime = TimeSpan.Zero,
            TotalAgents = 0, WorkingAgents = 0, CompliantAgents = 0,
            NonCompliantAgents = 0, ErrorAgents = 0, HasDeadlock = false,
            Observations = [], NextPhaseGates = [], PhaseTimeline = []
        };
    }

    public IReadOnlyList<DiagnosticHistoryEntry> GetDiagnosticHistory(
        string? agentIdFilter = null, bool? compliantFilter = null, int limit = 200)
    {
        try
        {
            var url = $"/api/dashboard/health/diagnostics?limit={limit}";
            if (agentIdFilter is not null) url += $"&agentId={agentIdFilter}";
            if (compliantFilter.HasValue) url += $"&compliant={compliantFilter.Value}";
            var task = _http.GetFromJsonAsync<List<DiagnosticHistoryEntry>>(url);
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
                return task.Result;
        }
        catch { /* fall through */ }
        return [];
    }

    // ── Agent errors & activity ──

    public IReadOnlyList<AgentLogEntry> GetAgentErrors(string agentId)
    {
        try
        {
            var task = _http.GetFromJsonAsync<List<AgentLogEntry>>($"/api/dashboard/agents/{agentId}/errors");
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
                return task.Result;
        }
        catch { /* fall through */ }
        return [];
    }

    public void ClearAgentErrors(string agentId)
    {
        try { _http.PostAsync($"/api/dashboard/agents/{agentId}/errors/clear", null).Wait(TimeSpan.FromSeconds(3)); }
        catch { /* best effort */ }
    }

    public async Task<IReadOnlyList<ActivityLogEntry>> GetActivityLogAsync(
        string agentId, int count = 100, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<ActivityLogEntry>>(
                $"/api/dashboard/agents/{agentId}/activity", ct);
            return result ?? [];
        }
        catch { return []; }
    }

    // ── Model management ──

    public IReadOnlyList<string> GetAvailableModels()
    {
        if (_models.Count > 0) return _models;
        try
        {
            var task = _http.GetFromJsonAsync<List<string>>("/api/dashboard/models");
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
            {
                _models = task.Result;
                return _models;
            }
        }
        catch { /* fall through */ }
        return [];
    }

    public void RefreshActiveModels()
    {
        try { _http.PostAsync("/api/dashboard/models/refresh", null).Wait(TimeSpan.FromSeconds(3)); }
        catch { /* best effort */ }
    }

    public void SetAgentModel(string agentId, string modelName)
    {
        try
        {
            _http.PostAsJsonAsync($"/api/dashboard/agents/{agentId}/model",
                new { ModelName = modelName }).Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* best effort */ }
    }

    // ── Execution timeline ──

    public IReadOnlyList<ExecutionMilestone> GetExecutionTimeline()
    {
        try
        {
            var task = _http.GetFromJsonAsync<List<ExecutionMilestone>>("/api/dashboard/timeline");
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
            {
                _milestones = task.Result;
                return _milestones;
            }
        }
        catch { /* fall through */ }
        return _milestones;
    }

    // ── Agent chat ──

    public async Task<AgentChatMessage> SendAgentChatAsync(
        string agentId, string message, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"/api/dashboard/agents/{agentId}/chat",
                new { Message = message }, ct);
            var result = await resp.Content.ReadFromJsonAsync<AgentChatMessage>(cancellationToken: ct);
            if (result is not null) return result;
        }
        catch { /* fall through */ }
        return new AgentChatMessage
        {
            Role = "assistant",
            Content = "[Connection error — runner may be unavailable]",
            Timestamp = DateTime.UtcNow
        };
    }

    public IReadOnlyList<AgentChatMessage> GetAgentChatHistory(string agentId)
    {
        try
        {
            var task = _http.GetFromJsonAsync<List<AgentChatMessage>>(
                $"/api/dashboard/agents/{agentId}/chat-history");
            task.Wait(TimeSpan.FromSeconds(3));
            if (task.IsCompletedSuccessfully && task.Result is not null)
                return task.Result;
        }
        catch { /* fall through */ }
        return [];
    }

    public void ClearAgentChat(string agentId)
    {
        try { _http.PostAsync($"/api/dashboard/agents/{agentId}/chat/clear", null).Wait(TimeSpan.FromSeconds(3)); }
        catch { /* best effort */ }
    }

    // ── Platform data ──

    public bool IsRateLimited => _isRateLimited;
    public string RepositoryDisplayName => _repoFullName;
    public string PlatformName => _rateLimitInfo.PlatformName ?? "GitHub";

    public PlatformRateLimitInfo GetRateLimitInfo() => _rateLimitInfo;

    public async Task<IReadOnlyList<PlatformPullRequest>> GetPullRequestsAsync()
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<PlatformPullRequest>>("/api/dashboard/platform/pull-requests");
            if (result is not null) { _pullRequests = result; return _pullRequests; }
        }
        catch { /* fall through */ }
        return _pullRequests;
    }

    public async Task<IReadOnlyList<PlatformWorkItem>> GetWorkItemsAsync()
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<PlatformWorkItem>>("/api/dashboard/platform/work-items");
            if (result is not null) { _issues = result; return _issues; }
        }
        catch { /* fall through */ }
        return _issues;
    }

    // ── Cache management ──

    public void ResetCaches()
    {
        try { _http.PostAsync("/api/dashboard/reset", null).Wait(TimeSpan.FromSeconds(3)); }
        catch { /* best effort */ }
    }

    public decimal GetTotalEstimatedCost()
    {
        return _cachedTotalCost;
    }

    public int GetTotalAiCalls()
    {
        return _cachedTotalCalls;
    }

    // DTOs for deserialization
    private record DeadlockResponse(bool HasDeadlock, List<string>? Cycle);
    private record RateLimitResponse(bool IsRateLimited);
    private record CostSummaryResponse(decimal TotalCost, int TotalCalls);
    private record RepoInfoResponse(string FullName);
}
