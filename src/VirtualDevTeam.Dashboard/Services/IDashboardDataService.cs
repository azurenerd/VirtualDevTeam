using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.Diagnostics;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Abstraction over dashboard data access. Implemented by:
/// - DashboardDataService (in-process, used when Runner hosts dashboard)
/// - HttpDashboardDataService (HTTP client, used when dashboard runs standalone)
/// </summary>
public interface IDashboardDataService
{
    // Agent snapshots
    IReadOnlyList<AgentSnapshot> GetAllAgentSnapshots();
    AgentSnapshot? GetAgentSnapshot(string agentId);

    // Health & diagnostics
    AgentHealthSnapshot GetCurrentHealthSnapshot();
    bool HasDeadlock(out List<string>? cycle);
    ExecutionHealthAssessment GetExecutionHealthAssessment();
    IReadOnlyList<DiagnosticHistoryEntry> GetDiagnosticHistory(
        string? agentIdFilter = null, bool? compliantFilter = null, int limit = 200);

    // Agent errors & activity
    IReadOnlyList<AgentLogEntry> GetAgentErrors(string agentId);
    void ClearAgentErrors(string agentId);
    Task<IReadOnlyList<ActivityLogEntry>> GetActivityLogAsync(
        string agentId, int count = 100, CancellationToken ct = default);

    // Model management
    IReadOnlyList<string> GetAvailableModels();
    void RefreshActiveModels();
    void SetAgentModel(string agentId, string modelName);

    // Execution timeline
    IReadOnlyList<ExecutionMilestone> GetExecutionTimeline();

    // Agent chat
    Task<AgentChatMessage> SendAgentChatAsync(string agentId, string message, CancellationToken ct = default);
    IReadOnlyList<AgentChatMessage> GetAgentChatHistory(string agentId);
    void ClearAgentChat(string agentId);

    // Platform data (PRs, work items, rate limiting)
    string RepositoryDisplayName { get; }
    string PlatformName { get; }
    bool IsRateLimited { get; }
    PlatformRateLimitInfo GetRateLimitInfo();

    /// <summary>Build a web URL for a pull request on the current platform.</summary>
    string GetPullRequestUrl(int prNumber);
    /// <summary>Build a web URL for a work item on the current platform.</summary>
    string GetWorkItemUrl(int workItemId);
    Task<IReadOnlyList<PlatformPullRequest>> GetPullRequestsAsync();
    Task<IReadOnlyList<PlatformWorkItem>> GetWorkItemsAsync();

    // Cache management
    Task InvalidatePlatformCachesAsync(CancellationToken ct = default);
    void ResetCaches();

    // Cost tracking
    decimal GetTotalEstimatedCost();
    int GetTotalAiCalls();
    int GetTotalPremiumRequests();
    /// <summary>Per-agent AI usage stats for cost breakdown display.</summary>
    IReadOnlyDictionary<string, AgentUsageStats> GetAgentUsageStats();

    // Agent role description (run-scoped overrides)
    /// <summary>Get role description info for an agent (effective, override, configured, hasOverride).</summary>
    AgentRoleDescriptionInfo? GetAgentRoleDescription(string agentId);
    /// <summary>Save a run-scoped role description override for an agent.</summary>
    void SaveAgentRoleOverride(string agentId, string description);
    /// <summary>Clear a run-scoped role description override, reverting to default.</summary>
    bool ClearAgentRoleOverride(string agentId);

    // Repository file browsing
    /// <summary>Get the file tree for the effective branch. Returns flat file paths.</summary>
    Task<RepositoryFileTreeResult> GetRepositoryFileTreeAsync(string? branch = null, CancellationToken ct = default);
    /// <summary>Get file content with metadata (binary detection, truncation).</summary>
    Task<RepositoryFileContentResult?> GetFileContentWithMetadataAsync(string path, string? branch = null, CancellationToken ct = default);

    // Change notification
    event Action? OnChange;
}

/// <summary>DTO for agent role description information returned by the dashboard service.</summary>
public record AgentRoleDescriptionInfo(
    string AgentId,
    string DisplayName,
    string Role,
    string? EffectiveDescription,
    string? OverrideDescription,
    string? ConfiguredDescription,
    bool HasOverride);
