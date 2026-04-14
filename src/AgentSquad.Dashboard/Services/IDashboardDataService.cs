using AgentSquad.Core.Agents;
using AgentSquad.Core.Diagnostics;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Persistence;
using AgentSquad.Orchestrator;

namespace AgentSquad.Dashboard.Services;

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

    // GitHub data
    bool IsGitHubRateLimited { get; }
    GitHubRateLimitInfo GetRateLimitInfo();
    Task<IReadOnlyList<AgentPullRequest>> GetPullRequestsAsync();
    Task<IReadOnlyList<AgentIssue>> GetIssuesAsync();

    // Cache management
    void ResetCaches();

    // Cost tracking
    decimal GetTotalEstimatedCost();
    int GetTotalAiCalls();

    // Change notification
    event Action? OnChange;
}
