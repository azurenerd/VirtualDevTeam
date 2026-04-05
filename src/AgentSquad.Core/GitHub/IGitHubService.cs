namespace AgentSquad.Core.GitHub;

using AgentSquad.Core.GitHub.Models;

public interface IGitHubService
{
    // Pull Requests
    Task<AgentPullRequest> CreatePullRequestAsync(string title, string body, string headBranch, string baseBranch, string[] labels, CancellationToken ct = default);
    Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default);
    Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentPullRequest>> GetPullRequestsForAgentAsync(string agentName, CancellationToken ct = default);
    Task AddPullRequestCommentAsync(int prNumber, string comment, CancellationToken ct = default);
    Task AddPullRequestReviewAsync(int prNumber, string body, string eventType, CancellationToken ct = default);
    Task UpdatePullRequestAsync(int prNumber, string? title = null, string? body = null, string[]? labels = null, CancellationToken ct = default);
    Task MergePullRequestAsync(int prNumber, string? commitMessage = null, CancellationToken ct = default);

    // Issues
    Task<AgentIssue> CreateIssueAsync(string title, string body, string[] labels, CancellationToken ct = default);
    Task<AgentIssue?> GetIssueAsync(int number, CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetOpenIssuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(string agentName, CancellationToken ct = default);
    Task AddIssueCommentAsync(int issueNumber, string comment, CancellationToken ct = default);
    Task CloseIssueAsync(int issueNumber, CancellationToken ct = default);

    // File Management
    Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default);
    Task CreateOrUpdateFileAsync(string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default);

    // Branches
    Task CreateBranchAsync(string branchName, string fromBranch = "main", CancellationToken ct = default);
    Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default);

    // Rate Limiting
    Task<GitHubRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default);
}
