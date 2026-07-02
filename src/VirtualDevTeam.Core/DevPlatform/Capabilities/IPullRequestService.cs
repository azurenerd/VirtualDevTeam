using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Capabilities;

/// <summary>
/// Pull request operations. Maps to GitHub Pull Requests or Azure DevOps Pull Requests.
/// </summary>
public interface IPullRequestService
{
    Task<PlatformPullRequest> CreateAsync(
        string title, string body, string headBranch, string baseBranch,
        IReadOnlyList<string> labels, CancellationToken ct = default);

    Task<PlatformPullRequest?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformPullRequest>> ListOpenAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlatformPullRequest>> ListAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlatformPullRequest>> ListMergedAsync(CancellationToken ct = default);

    /// <summary>
    /// List ALL pull requests for the project, ignoring any run-scope filter.
    /// Use from display/history contexts (e.g. Timeline) — not from agent claiming logic.
    /// </summary>
    Task<IReadOnlyList<PlatformPullRequest>> ListAllForProjectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlatformPullRequest>> ListForAgentAsync(string agentName, CancellationToken ct = default);

    Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, CancellationToken ct = default);

    Task MergeAsync(int id, string? commitMessage = null, CancellationToken ct = default);
    Task CloseAsync(int id, CancellationToken ct = default);

    /// <summary>Force-set PR state without performing git operations. For admin/recovery only.</summary>
    Task SetStateAsync(int id, string state, CancellationToken ct = default) =>
        Task.CompletedTask; // Default no-op for platforms that don't support it

    // PR file inspection
    Task<IReadOnlyList<string>> GetChangedFilesAsync(int prId, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformFileDiff>> GetFileDiffsAsync(int prId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCommitMessagesAsync(int prId, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformCommitInfo>> GetCommitsWithDatesAsync(int prId, CancellationToken ct = default);

    // PR branch sync
    Task<bool> IsBehindBaseAsync(int prId, CancellationToken ct = default);
    Task<bool> UpdateBranchAsync(int prId, CancellationToken ct = default);
    Task<bool> RebaseBranchAsync(int prId, CancellationToken ct = default);

    // PR ↔ Work Item linking
    /// <summary>
    /// Link a work item to a pull request.
    /// GitHub: ensures "Closes #X" in PR body. ADO: creates ArtifactLink relation.
    /// Idempotent — safe to call multiple times for the same pair.
    /// </summary>
    Task LinkWorkItemAsync(int prId, int workItemId, CancellationToken ct = default);

    /// <summary>
    /// Get work item IDs linked to a pull request.
    /// GitHub: parses "Closes #X" from PR body. ADO: queries PR work item links.
    /// </summary>
    Task<IReadOnlyList<int>> GetLinkedWorkItemIdsAsync(int prId, CancellationToken ct = default);
}
