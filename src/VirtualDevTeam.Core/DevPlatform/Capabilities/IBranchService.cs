namespace VirtualDevTeam.Core.DevPlatform.Capabilities;

/// <summary>
/// Branch management operations.
/// Maps to GitHub Refs API or Azure DevOps Git Refs API.
/// </summary>
public interface IBranchService
{
    Task CreateAsync(string branchName, string? fromBranch = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string branchName, CancellationToken ct = default);
    Task DeleteAsync(string branchName, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>
    /// Atomically reset a branch to contain only the specified files.
    /// GitHub: Git Tree API. ADO: Push with tree replacement.
    /// </summary>
    Task CleanToBaselineAsync(
        IReadOnlyList<string> preserveFiles, string commitMessage,
        string? branch = null, CancellationToken ct = default);
}
