using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Metrics;
using AgentSquad.Core.Persistence;
using AgentSquad.Dashboard.Services;
using AgentSquad.Orchestrator;

namespace AgentSquad.Dashboard.Host;

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

        // AgentSquadConfig needed by various services
        services.Configure<AgentSquadConfig>(_ => { });

        return services;
    }
}

/// <summary>
/// No-op GitHub service for standalone dashboard mode.
/// Read methods return empty results; write methods are no-ops.
/// </summary>
file sealed class NullGitHubService : IGitHubService
{
    public string RepositoryFullName => "standalone/not-connected";

    // Pull Requests
    public Task<AgentPullRequest> CreatePullRequestAsync(string title, string body, string headBranch, string baseBranch, string[] labels, CancellationToken ct = default)
        => throw new NotSupportedException("GitHub operations not available in standalone mode");
    public Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default)
        => Task.FromResult<AgentPullRequest?>(null);
    public Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AgentPullRequest>>([]);
    public Task<IReadOnlyList<AgentPullRequest>> GetAllPullRequestsAsync(CancellationToken ct = default)
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
    public Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task CreateOrUpdateFileAsync(string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task DeleteFileAsync(string path, string commitMessage, string? branch = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task BatchCommitFilesAsync(IReadOnlyList<(string Path, string Content)> files, string commitMessage, string branch, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<string?> CommitBinaryFileAsync(string path, byte[] content, string commitMessage, string branch, CancellationToken ct = default)
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
