namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Abstraction over how an agent interacts with a git workspace.
/// Two implementations: <see cref="LocalWorkspace"/> (clone-based)
/// and <see cref="WorktreeWorkspace"/> (worktree-based for Worktree/InPlace modes).
/// </summary>
public interface IAgentWorkspace
{
    /// <summary>Absolute path to the agent's working directory (repo root).</summary>
    string RepoPath { get; }

    /// <summary>Which workspace mode is active.</summary>
    WorkspaceMode Mode { get; }

    /// <summary>
    /// Initialize the workspace — clone, create worktree, or validate existing.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Fetch + rebase on the default branch to stay current.</summary>
    Task SyncWithMainAsync(CancellationToken ct = default);

    /// <summary>Create a new local branch from the current HEAD.</summary>
    Task CreateBranchAsync(string branchName, CancellationToken ct = default);

    /// <summary>Checkout an existing branch.</summary>
    Task CheckoutBranchAsync(string branchName, CancellationToken ct = default);

    /// <summary>Merge the default branch into the current branch. Returns true if successful.</summary>
    Task<bool> MergeMainIntoBranchAsync(CancellationToken ct = default);

    /// <summary>Write content to a file relative to <see cref="RepoPath"/>.</summary>
    Task WriteFileAsync(string relativePath, string content, CancellationToken ct = default);

    /// <summary>Read content from a file relative to <see cref="RepoPath"/>.</summary>
    Task<string> ReadFileAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Stage all changes and commit with the given message.</summary>
    Task CommitAsync(string message, CancellationToken ct = default);

    /// <summary>Push the branch to the remote.</summary>
    Task PushAsync(string branchName, CancellationToken ct = default);

    /// <summary>Force-push the branch to the remote.</summary>
    Task ForcePushAsync(string branchName, CancellationToken ct = default);

    /// <summary>Pull with rebase from the remote branch. Returns true if successful.</summary>
    Task<bool> PullRebaseAsync(string branchName, CancellationToken ct = default);

    /// <summary>Get the name of the currently checked-out branch.</summary>
    Task<string> GetCurrentBranchAsync(CancellationToken ct = default);

    /// <summary>Get the SHA of a ref (default HEAD).</summary>
    Task<string> GetHeadShaAsync(string @ref = "HEAD", CancellationToken ct = default);

    /// <summary>Get the remote tracking SHA for a branch.</summary>
    Task<string> GetRemoteShaAsync(string branchName, CancellationToken ct = default);

    /// <summary>Get the git status output.</summary>
    Task<string> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Get list of files with uncommitted changes.</summary>
    Task<List<string>> GetChangedFilePathsAsync(CancellationToken ct = default);

    /// <summary>Get list of files changed vs the default branch.</summary>
    Task<List<string>> GetDiffFileListVsMainAsync(CancellationToken ct = default);

    /// <summary>Revert specific files to their committed state.</summary>
    Task RevertFilesAsync(IEnumerable<string> relativePaths, CancellationToken ct = default);

    /// <summary>Discard all uncommitted changes.</summary>
    Task RevertUncommittedChangesAsync(CancellationToken ct = default);

    /// <summary>Clean up resources (delete clone/worktree if transient).</summary>
    Task CleanupAsync();

    /// <summary>
    /// Nuclear recovery — destroy and recreate the workspace from scratch.
    /// In Clone mode: delete + re-clone. In Worktree mode: remove + re-add worktree (~2s).
    /// </summary>
    Task NukeAndRecloneAsync(string branchName, CancellationToken ct = default);
}
