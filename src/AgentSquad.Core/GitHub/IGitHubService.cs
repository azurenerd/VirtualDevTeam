namespace AgentSquad.Core.GitHub;

using AgentSquad.Core.GitHub.Models;

public interface IGitHubService
{
    // Pull Requests
    Task<AgentPullRequest> CreatePullRequestAsync(string title, string body, string headBranch, string baseBranch, string[] labels, CancellationToken ct = default);
    Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default);
    Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentPullRequest>> GetPullRequestsForAgentAsync(string agentName, CancellationToken ct = default);
    Task<IReadOnlyList<IssueComment>> GetPullRequestCommentsAsync(int prNumber, CancellationToken ct = default);
    Task AddPullRequestCommentAsync(int prNumber, string comment, CancellationToken ct = default);
    Task AddPullRequestReviewAsync(int prNumber, string body, string eventType, CancellationToken ct = default);
    Task UpdatePullRequestAsync(int prNumber, string? title = null, string? body = null, string[]? labels = null, CancellationToken ct = default);
    Task MergePullRequestAsync(int prNumber, string? commitMessage = null, CancellationToken ct = default);
    Task ClosePullRequestAsync(int prNumber, CancellationToken ct = default);

    // Issues
    Task<AgentIssue> CreateIssueAsync(string title, string body, string[] labels, CancellationToken ct = default);
    Task<AgentIssue?> GetIssueAsync(int number, CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetOpenIssuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(string agentName, CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, string state, CancellationToken ct = default);
    Task AddIssueCommentAsync(int issueNumber, string comment, CancellationToken ct = default);
    Task UpdateIssueAsync(int issueNumber, string? title = null, string? body = null, string[]? labels = null, string? state = null, CancellationToken ct = default);
    Task UpdateIssueTitleAsync(int issueNumber, string newTitle, CancellationToken ct = default);
    Task CloseIssueAsync(int issueNumber, CancellationToken ct = default);

    // File Management
    Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default);
    Task CreateOrUpdateFileAsync(string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default);
    Task DeleteFileAsync(string path, string commitMessage, string? branch = null, CancellationToken ct = default);

    /// <summary>
    /// Commits multiple files to a branch in a single atomic commit using the Git Trees API.
    /// This avoids the one-commit-per-file limitation of the Contents API.
    /// </summary>
    Task BatchCommitFilesAsync(
        IReadOnlyList<(string Path, string Content)> files,
        string commitMessage,
        string branch,
        CancellationToken ct = default);

    // Branches
    Task CreateBranchAsync(string branchName, string fromBranch = "main", CancellationToken ct = default);
    Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default);
    Task DeleteBranchAsync(string branchName, CancellationToken ct = default);

    /// <summary>
    /// Merge the base branch (main) into a PR branch to bring it up to date.
    /// Returns true if the update succeeded or branch is already up to date,
    /// false if there are genuine merge conflicts.
    /// </summary>
    Task<bool> UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a PR branch is behind main (i.e., main has commits not in the branch).
    /// Returns true if the branch is behind and needs syncing, false if up to date.
    /// </summary>
    Task<bool> IsBranchBehindMainAsync(int prNumber, CancellationToken ct = default);

    /// <summary>
    /// Force-rebase a PR branch onto main by: reading all changed files from the branch,
    /// resetting the branch ref to main HEAD, and re-committing the files on top.
    /// This eliminates merge conflicts caused by parallel branches diverging from main.
    /// Returns true if successful, false if no files to rebase or an error occurred.
    /// WARNING: This is destructive — only use when there are genuine merge conflicts.
    /// </summary>
    Task<bool> RebaseBranchOnMainAsync(int prNumber, CancellationToken ct = default);

    // PR file inspection
    Task<IReadOnlyList<AgentPullRequest>> GetMergedPullRequestsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPullRequestChangedFilesAsync(int prNumber, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPullRequestCommitMessagesAsync(int prNumber, CancellationToken ct = default);

    // Rate Limiting
    Task<GitHubRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default);
}
