// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using System.Net.Http.Json;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.Frameworks;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Metrics;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Dashboard.Services;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Dashboard.Host;

/// <summary>
/// Registers stub/no-op services for standalone dashboard mode.
/// Pages still inject these types but most functionality is unavailable
/// when running outside the Runner process.
/// </summary>
public static class StandaloneServiceRegistration
{
    public static IServiceCollection AddStandaloneStubs(this IServiceCollection services)
    {
        // IGitHubService — required by AgentDetail page
        services.AddSingleton<IGitHubService, NullGitHubService>();

        // BuildTestMetrics — required by Metrics page
        services.AddSingleton<AgentStateStore>();
        services.AddSingleton<BuildTestMetrics>(sp =>
            new BuildTestMetrics(sp.GetRequiredService<AgentStateStore>()));

        // VirtualDevTeamConfig needed by various services
        // Configure prompts path — resolve relative to solution root
        services.Configure<VirtualDevTeamConfig>(config =>
        {
            config.Prompts.BasePath = "../../prompts";
        });

        // PromptTemplateService — reads/writes prompt .md files on disk
        services.AddSingleton<IPromptTemplateService, PromptTemplateService>();

        // SquadReadinessChecker — needed by Configuration page for Squad status checks
        services.AddSingleton<SquadReadinessChecker>();

        // CandidateStateStore — needed by ProjectTimeline and Strategies pages
        services.AddSingleton<CandidateStateStore>(sp =>
            new CandidateStateStore(sp.GetService<AgentStateStore>()));

        // FlowTimelineTracker — used by Project Timeline for phase duration fallbacks
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.FlowTimelineTracker>();

        // SharedCloneManager — used by Worktree/InPlace workspace modes
        services.AddSingleton<VirtualDevTeam.Core.Workspace.SharedCloneManager>();

        // DevelopSettingsService — file-based, path points to Runner's directory
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DevelopSettingsService>>();
            var config = sp.GetService<IOptions<VirtualDevTeamConfig>>();
            var runnerDir = Path.GetFullPath(Path.Combine(
                sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
                "..", "VirtualDevTeam.Runner"));
            var filePath = Path.Combine(runnerDir, "develop-settings.json");
            return new DevelopSettingsService(logger, config, filePath);
        });

        // IWorkItemSearchService — proxies to Runner's /api/develop/work-items/search
        services.AddSingleton<IWorkItemSearchService, HttpWorkItemSearchProxy>();

        // IRepositoryManagementService — proxies to Runner's /api/develop/repo/create
        services.AddSingleton<IRepositoryManagementService, HttpRepositoryManagementProxy>();

        // Preview & Artifacts services (no CopilotCliProcessManager in standalone — AI detection disabled)
        services.AddSingleton<VirtualDevTeam.Core.Preview.PreviewBuildService>(sp =>
            new VirtualDevTeam.Core.Preview.PreviewBuildService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VirtualDevTeam.Core.Preview.PreviewBuildService>>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>>()));
        services.AddSingleton<VirtualDevTeam.Core.Preview.TestArtifactIndexService>();
        services.AddSingleton<VirtualDevTeam.Core.Frameworks.CandidateArtifactService>();

        // Pre-PR Clarification store — needed by Approvals page
        services.AddSingleton<VirtualDevTeam.Core.Agents.Decisions.PrePRClarificationStore>();

        // RunBranchProvider — needed by ProjectTimeline for run-scope filtering.
        // In standalone mode, RunScope stays null (no RunCoordinator) so scope
        // filters become no-ops, showing all items. Acceptable for view-only host.
        services.AddSingleton<VirtualDevTeam.Core.Configuration.RunBranchProvider>(sp =>
        {
            var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>>().Value;
            return new VirtualDevTeam.Core.Configuration.RunBranchProvider(cfg.Project.DefaultBranch);
        });
        services.AddSingleton<VirtualDevTeam.Core.Configuration.IRunBranchProvider>(sp =>
            sp.GetRequiredService<VirtualDevTeam.Core.Configuration.RunBranchProvider>());

        // T1.6: LiveFixApplicator is registered for DI symmetry with the Runner, but the
        // standalone dashboard CANNOT actually apply fixes — that requires the
        // CopilotCliProcessManager which lives only in the Runner. The Approvals page
        // proxies the approve endpoint over HTTP to the Runner, so this resolver should
        // never actually fire. Throwing on resolve gives a clear error if it does.
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.LiveFixApplicator>(_ =>
            throw new InvalidOperationException(
                "LiveFixApplicator is not available in standalone Dashboard mode. " +
                "Approve operations must be proxied to the Runner via HTTP."));

        // T1.4: FlowMonitor event bus — registered so the FlowMonitorLog page can DI-resolve
        // anything that depends on it. In standalone mode no FlowMonitor writes to it (the
        // Runner runs out-of-process), so the bus stays empty. The page subscribes directly
        // to the Runner's hub via the configured RunnerUrl rather than through this bus.
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.FlowMonitorEventBus>();

        // Pipeline assessment store — standalone Dashboard reads assessment history.
        // The assessment BackgroundService only runs in-process (Runner), so no service registration here.
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.PipelineAssessmentStore>();

        // IAgentTaskTracker — needed by ProjectTimeline for step-level sub-activities.
        // In standalone mode this is empty (no agents run), so timeline falls back to proportional splits.
        services.AddSingleton<VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker, VirtualDevTeam.Core.Agents.Steps.AgentTaskTracker>();

        // Platform capability stubs — DashboardDataService needs these for PR/work item fetching
        services.AddSingleton<IPullRequestService, NullPullRequestService>();
        services.AddSingleton<IWorkItemService, NullWorkItemService>();
        services.AddSingleton<IReviewService, NullReviewService>();
        services.AddSingleton<IPlatformInfoService, NullPlatformInfoService>();

        // Scenario registry stub — Scenarios page is read-only; no project artifacts available
        // in standalone mode so we return an empty registry that raises no events.
        services.AddSingleton<IScenarioRegistry, NullScenarioRegistry>();

        // Scenario generation service — CopilotCliProcessManager not available in standalone mode;
        // ScenarioGenerationService handles null gracefully and returns an empty list.
        // Factory registration so optional deps resolve to null instead of throwing.
        services.AddSingleton<ScenarioGenerationService>(sp => new ScenarioGenerationService(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VirtualDevTeamConfig>>(),
            sp.GetRequiredService<ILogger<ScenarioGenerationService>>(),
            sp.GetService<CopilotCliProcessManager>(),
            sp.GetService<IPromptTemplateService>()));

        return services;
    }
}

/// <summary>
/// No-op GitHub service for standalone dashboard mode.
/// File reads are proxied through the Runner API; write methods are no-ops.
/// </summary>
file sealed class NullGitHubService : IGitHubService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NullGitHubService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string RepositoryFullName => "standalone/not-connected";
    public bool IsConfigured => false;

    // Pull Requests
    public Task<AgentPullRequest> CreatePullRequestAsync(string title, string body, string headBranch, string baseBranch, string[] labels, CancellationToken ct = default)
        => throw new NotSupportedException("GitHub operations not available in standalone mode");
    public Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default)
        => Task.FromResult<AgentPullRequest?>(null);
    public Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentPullRequest>>([]);
    public Task<IReadOnlyList<AgentPullRequest>> GetAllPullRequestsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentPullRequest>>([]);
    public Task<IReadOnlyList<AgentPullRequest>> GetAllPullRequestsUnfilteredAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentPullRequest>>([]);
    public Task<IReadOnlyList<AgentPullRequest>> GetPullRequestsForAgentAsync(string agentName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentPullRequest>>([]);
    public Task<IReadOnlyList<AgentPullRequest>> GetMergedPullRequestsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentPullRequest>>([]);
    public Task<IReadOnlyList<IssueComment>> GetPullRequestCommentsAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IssueComment>>([]);
    public Task AddPullRequestCommentAsync(int prNumber, string comment, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task AddPullRequestReviewAsync(int prNumber, string body, string eventType, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task UpdatePullRequestAsync(int prNumber, string? title = null, string? body = null, string[]? labels = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task MergePullRequestAsync(int prNumber, string? commitMessage = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task ClosePullRequestAsync(int prNumber, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<bool> IsBranchBehindMainAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<bool> RebaseBranchOnMainAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetPullRequestCommitMessagesAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<PullRequestFileDiff>> GetPullRequestFilesWithPatchAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PullRequestFileDiff>>([]);
    public Task CreatePullRequestReviewWithCommentsAsync(int prNumber, string body, string eventType, IReadOnlyList<InlineReviewComment> comments, string? commitId = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<ReviewThread>> GetPullRequestReviewThreadsAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReviewThread>>([]);
    public Task ReplyAndResolveReviewThreadAsync(int prNumber, long commentId, string nodeId, string replyBody, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task ReplyToReviewCommentAsync(int prNumber, long commentId, string replyBody, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<(string Sha, string Message, DateTime CommittedAt)>> GetPullRequestCommitsWithDatesAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string Sha, string Message, DateTime CommittedAt)>>([]);

    // Issues
    public Task<AgentIssue> CreateIssueAsync(string title, string body, string[] labels, CancellationToken ct = default)
        => throw new NotSupportedException("GitHub operations not available in standalone mode");
    public Task<AgentIssue?> GetIssueAsync(int number, CancellationToken ct = default)
        => Task.FromResult<AgentIssue?>(null);
    public Task<IReadOnlyList<AgentIssue>> GetOpenIssuesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentIssue>>([]);
    public Task<IReadOnlyList<AgentIssue>> GetAllIssuesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentIssue>>([]);
    public Task<IReadOnlyList<AgentIssue>> GetAllIssuesUnfilteredAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentIssue>>([]);
    public Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(string agentName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentIssue>>([]);
    public Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentIssue>>([]);
    public Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, string state, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentIssue>>([]);
    public Task AddIssueCommentAsync(int issueNumber, string comment, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(int issueNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IssueComment>>([]);
    public Task UpdateIssueAsync(int issueNumber, string? title = null, string? body = null, string[]? labels = null, string? state = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task UpdateIssueTitleAsync(int issueNumber, string newTitle, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task CloseIssueAsync(int issueNumber, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> DeleteIssueAsync(int issueNumber, CancellationToken ct = default)
        => Task.FromResult(false);

    // Sub-Issues & Dependencies
    public Task<bool> AddSubIssueAsync(int parentIssueNumber, long childIssueGitHubId, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<IReadOnlyList<AgentIssue>> GetSubIssuesAsync(int parentIssueNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentIssue>>([]);
    public Task<bool> AddIssueDependencyAsync(int blockedIssueNumber, long blockingIssueGitHubId, CancellationToken ct = default)
        => Task.FromResult(false);

    // File Management
    public async Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("RunnerApi");
            var encoded = Uri.EscapeDataString(path);
            var response = await client.GetAsync($"/api/dashboard/github/file?path={encoded}", ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadFromJsonAsync<FileContentResponse>(ct);
            return json?.Content;
        }
        catch { return null; }
    }
    public Task<byte[]?> GetFileBytesAsync(string path, string? branch = null, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);
    public Task CreateOrUpdateFileAsync(string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task DeleteFileAsync(string path, string commitMessage, string? branch = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task BatchCommitFilesAsync(IReadOnlyList<(string Path, string Content)> files, string commitMessage, string branch, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<string?> CommitBinaryFileAsync(string path, byte[] content, string commitMessage, string branch, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<string?> UploadImageAsReleaseAssetAsync(string filename, byte[] content, string contentType = "image/png", CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    // Branches
    public Task CreateBranchAsync(string branchName, string fromBranch = "main", CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task DeleteBranchAsync(string branchName, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListBranchesAsync(string? prefix = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task CleanRepoToBaselineAsync(IReadOnlyList<string> preserveFiles, string commitMessage, string branch = "main", CancellationToken ct = default)
        => Task.CompletedTask;

    // Repository Structure
    public Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string branch = "main", CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    // Rate Limiting
    public Task<GitHubRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default)
        => Task.FromResult(new GitHubRateLimitInfo { Remaining = 0, Limit = 0, ResetAt = DateTime.UtcNow });
}

file record FileContentResponse(string? Content);

/// <summary>
/// HTTP-proxying work item search for standalone dashboard.
/// Delegates to Runner's /api/develop/work-items/search endpoint.
/// </summary>
file sealed class HttpWorkItemSearchProxy : IWorkItemSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpWorkItemSearchProxy(IHttpClientFactory httpClientFactory) =>
        _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<WorkItemSearchResult>> SearchAsync(
        string query, int maxResults = 10, CancellationToken ct = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("RunnerApi");
            var encoded = Uri.EscapeDataString(query);
            var results = await client.GetFromJsonAsync<List<WorkItemSearchResult>>(
                $"/api/develop/work-items/search?q={encoded}", ct);
            return results ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// HTTP-proxying repo creation for standalone dashboard.
/// Delegates to Runner's /api/develop/repo/create endpoint.
/// </summary>
file sealed class HttpRepositoryManagementProxy : IRepositoryManagementService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpRepositoryManagementProxy(IHttpClientFactory httpClientFactory) =>
        _httpClientFactory = httpClientFactory;

    public async Task<RepositoryCreationResult> CreateRepositoryAsync(
        string name, bool isPrivate = true, CancellationToken ct = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("RunnerApi");
            var payload = new { name, isPrivate };
            var response = await client.PostAsJsonAsync("/api/develop/repo/create", payload, ct);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<RepositoryCreationResult>(ct);
                return result ?? new RepositoryCreationResult(false, null, "Empty response");
            }
            return new RepositoryCreationResult(false, null, $"Runner returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new RepositoryCreationResult(false, null, $"Runner unreachable: {ex.Message}");
        }
    }
}

/// <summary>
/// No-op pull request service for standalone dashboard mode.
/// Returns empty collections — real data comes from the Runner process.
/// </summary>
file sealed class NullPullRequestService : IPullRequestService
{
    public Task<PlatformPullRequest> CreateAsync(string title, string body, string headBranch, string baseBranch, IReadOnlyList<string> labels, CancellationToken ct = default)
        => throw new NotSupportedException("PR operations not available in standalone mode");
    public Task<PlatformPullRequest?> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult<PlatformPullRequest?>(null);
    public Task<IReadOnlyList<PlatformPullRequest>> ListOpenAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformPullRequest>>([]);
    public Task<IReadOnlyList<PlatformPullRequest>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformPullRequest>>([]);
    public Task<IReadOnlyList<PlatformPullRequest>> ListAllForProjectAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformPullRequest>>([]);
    public Task<IReadOnlyList<PlatformPullRequest>> ListMergedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformPullRequest>>([]);
    public Task<IReadOnlyList<PlatformPullRequest>> ListForAgentAsync(string agentName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformPullRequest>>([]);
    public Task UpdateAsync(int id, string? title = null, string? body = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task MergeAsync(int id, string? commitMessage = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task CloseAsync(int id, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<string>> GetChangedFilesAsync(int prId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<PlatformFileDiff>> GetFileDiffsAsync(int prId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformFileDiff>>([]);
    public Task<IReadOnlyList<string>> GetCommitMessagesAsync(int prId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<PlatformCommitInfo>> GetCommitsWithDatesAsync(int prId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformCommitInfo>>([]);
    public Task<bool> IsBehindBaseAsync(int prId, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<bool> UpdateBranchAsync(int prId, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<bool> RebaseBranchAsync(int prId, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task LinkWorkItemAsync(int prId, int workItemId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<int>> GetLinkedWorkItemIdsAsync(int prId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<int>>([]);
}

/// <summary>
/// No-op work item service for standalone dashboard mode.
/// </summary>
file sealed class NullWorkItemService : IWorkItemService
{
    public Task<PlatformWorkItem> CreateAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default)
        => throw new NotSupportedException("Work item operations not available in standalone mode");
    public Task<PlatformWorkItem?> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult<PlatformWorkItem?>(null);
    public Task<IReadOnlyList<PlatformWorkItem>> ListOpenAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);
    public Task<IReadOnlyList<PlatformWorkItem>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);
    public Task<IReadOnlyList<PlatformWorkItem>> ListAllForProjectAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);
    public Task<IReadOnlyList<PlatformWorkItem>> ListForAgentAsync(string agentName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);
    public Task<IReadOnlyList<PlatformWorkItem>> ListByLabelAsync(string label, string? state = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);
    public Task UpdateAsync(int id, string? title = null, string? body = null, IReadOnlyList<string>? labels = null, string? state = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task UpdateTitleAsync(int id, string newTitle, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task CloseAsync(int id, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task AddCommentAsync(int id, string comment, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformComment>>([]);
    public Task<bool> AddChildAsync(int parentId, long childPlatformId, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task<IReadOnlyList<PlatformWorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformWorkItem>>([]);
    public Task<bool> AddDependencyAsync(int blockedId, long blockingPlatformId, CancellationToken ct = default)
        => Task.FromResult(false);
}

/// <summary>
/// No-op scenario registry for standalone dashboard mode.
/// Project artifact files are not accessible outside the Runner process.
/// The Scenarios page will show an empty state.
/// </summary>
file sealed class NullScenarioRegistry : IScenarioRegistry
{
    public IReadOnlyList<Scenario> Current => Array.Empty<Scenario>();
    public IReadOnlyList<Scenario> Critical => Array.Empty<Scenario>();
    public bool LastLoadHadDrift => false;

    // Event with empty add/remove so subscribers compile and run without errors.
    public event EventHandler<ScenarioRegistryChangedEventArgs>? Changed { add { } remove { } }

    public Scenario? FindById(string id) => null;

    public Task<IReadOnlyList<Scenario>> LoadAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Scenario>>(Array.Empty<Scenario>());

    public Task WriteSidecarAsync(IReadOnlyList<Scenario> scenarios, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdateVerificationStatusAsync(string scenarioId, VerificationStatus status,
        string? reason = null, string? evidenceUrl = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdateImplementingTasksAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> scenarioIdToTasks, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> ValidateNoOrphans(CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>
/// No-op review service for standalone dashboard mode.
/// </summary>
file sealed class NullReviewService : IReviewService
{
    public Task AddCommentAsync(int prId, string comment, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int prId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformComment>>([]);
    public Task AddReviewAsync(int prId, string body, string eventType, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task CreateReviewWithInlineCommentsAsync(int prId, string body, string eventType, IReadOnlyList<PlatformInlineComment> comments, string? commitId = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<PlatformReviewThread>> GetThreadsAsync(int prId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlatformReviewThread>>([]);
    public Task ResolveThreadAsync(int prId, string threadId, string replyBody, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task ReplyToThreadAsync(int prId, string threadId, string replyBody, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// No-op platform info service for standalone dashboard mode.
/// </summary>
file sealed class NullPlatformInfoService : IPlatformInfoService
{
    public string PlatformName => "Standalone";
    public string RepositoryDisplayName => "standalone/not-connected";
    public PlatformCapabilities Capabilities => new();
    public Task<PlatformRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default)
        => Task.FromResult(new PlatformRateLimitInfo
        {
            Remaining = 0,
            Limit = 0,
            ResetAt = DateTime.UtcNow,
            PlatformName = "Standalone"
        });
}
