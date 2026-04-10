namespace AgentSquad.Core.GitHub;

using AgentSquad.Core.GitHub.Models;

public interface IGitHubService
{
    // Pull Requests
    Task<AgentPullRequest> CreatePullRequestAsync(string title, string body, string headBranch, string baseBranch, string[] labels, CancellationToken ct = default);
    Task<AgentPullRequest?> GetPullRequestAsync(int number, CancellationToken ct = default);
    Task<IReadOnlyList<AgentPullRequest>> GetOpenPullRequestsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentPullRequest>> GetAllPullRequestsAsync(CancellationToken ct = default);
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
    Task<IReadOnlyList<AgentIssue>> GetAllIssuesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetIssuesForAgentAsync(string agentName, CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, CancellationToken ct = default);
    Task<IReadOnlyList<AgentIssue>> GetIssuesByLabelAsync(string label, string state, CancellationToken ct = default);
    Task AddIssueCommentAsync(int issueNumber, string comment, CancellationToken ct = default);
    Task<IReadOnlyList<IssueComment>> GetIssueCommentsAsync(int issueNumber, CancellationToken ct = default);
    Task UpdateIssueAsync(int issueNumber, string? title = null, string? body = null, string[]? labels = null, string? state = null, CancellationToken ct = default);
    Task UpdateIssueTitleAsync(int issueNumber, string newTitle, CancellationToken ct = default);
    Task CloseIssueAsync(int issueNumber, CancellationToken ct = default);

    /// <summary>
    /// Permanently delete an issue using the GitHub GraphQL API.
    /// Requires the PAT to have admin/delete permissions on the repo.
    /// Falls back to closing the issue if deletion fails.
    /// </summary>
    Task<bool> DeleteIssueAsync(int issueNumber, CancellationToken ct = default);

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

    /// <summary>
    /// Commits a binary file (e.g., PNG screenshot) to a branch using base64 encoding.
    /// Returns the raw URL to the committed file for embedding in markdown.
    /// </summary>
    Task<string?> CommitBinaryFileAsync(
        string path, byte[] content, string commitMessage, string branch,
        CancellationToken ct = default);

    // Branches
    Task CreateBranchAsync(string branchName, string fromBranch = "main", CancellationToken ct = default);
    Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default);
    Task DeleteBranchAsync(string branchName, CancellationToken ct = default);

    /// <summary>Lists all branches matching a prefix (e.g., "agent/").</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>
    /// Atomically resets the repo to contain only the specified files.
    /// Uses Git tree API for a single commit instead of deleting files one by one.
    /// </summary>
    Task CleanRepoToBaselineAsync(IReadOnlyList<string> preserveFiles, string commitMessage, string branch = "main", CancellationToken ct = default);

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

    /// <summary>
    /// Get the timestamps of all commits on a PR branch.
    /// Used by reviewers to detect whether new commits were pushed since the last review.
    /// </summary>
    Task<IReadOnlyList<(string Sha, string Message, DateTime CommittedAt)>> GetPullRequestCommitsWithDatesAsync(int prNumber, CancellationToken ct = default);

    // Sub-Issues (parent-child hierarchy)
    /// <summary>
    /// Add an issue as a sub-issue (child) of another issue using GitHub's Sub-Issues API.
    /// This creates native parent-child hierarchy visible in the GitHub UI.
    /// </summary>
    Task<bool> AddSubIssueAsync(int parentIssueNumber, long childIssueGitHubId, CancellationToken ct = default);

    /// <summary>
    /// Get all sub-issues of a parent issue. Returns both open and closed sub-issues.
    /// </summary>
    Task<IReadOnlyList<AgentIssue>> GetSubIssuesAsync(int parentIssueNumber, CancellationToken ct = default);

    // Issue Dependencies (blocked-by relationships)
    /// <summary>
    /// Add a "blocked by" dependency between two issues using GitHub's Dependencies API.
    /// The issue identified by blockedIssueNumber is blocked by the issue identified by blockingIssueGitHubId.
    /// </summary>
    Task<bool> AddIssueDependencyAsync(int blockedIssueNumber, long blockingIssueGitHubId, CancellationToken ct = default);

    // Repository Structure
    /// <summary>
    /// Gets the full file tree of the repository from a branch using the Git Trees API (recursive).
    /// Returns a list of file paths (blobs only, no directories). Used to give agents
    /// visibility into the existing repo structure before they create new files.
    /// </summary>
    Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string branch = "main", CancellationToken ct = default);

    /// <summary>
    /// Gets the file tree for a specific commit SHA (not a branch name).
    /// Used by cleanup to resolve which files existed in a baseline commit.
    /// </summary>
    Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default);

    // Rate Limiting
    Task<GitHubRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default);
}
