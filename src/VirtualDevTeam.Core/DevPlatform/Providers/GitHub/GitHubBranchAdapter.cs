// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Adapts <see cref="IGitHubService"/> branch operations to <see cref="IBranchService"/>.
/// </summary>
public sealed class GitHubBranchAdapter : IBranchService
{
    private readonly IGitHubService _github;

    public GitHubBranchAdapter(IGitHubService github)
    {
        ArgumentNullException.ThrowIfNull(github);
        _github = github;
    }

    public Task CreateAsync(string branchName, string? fromBranch = null, CancellationToken ct = default)
        => _github.CreateBranchAsync(branchName, fromBranch ?? "main", ct);

    public Task<bool> ExistsAsync(string branchName, CancellationToken ct = default)
        => _github.BranchExistsAsync(branchName, ct);

    public Task DeleteAsync(string branchName, CancellationToken ct = default)
        => _github.DeleteBranchAsync(branchName, ct);

    public Task<IReadOnlyList<string>> ListAsync(string? prefix = null, CancellationToken ct = default)
        => _github.ListBranchesAsync(prefix, ct);

    public Task CleanToBaselineAsync(
        IReadOnlyList<string> preserveFiles, string commitMessage,
        string? branch = null, CancellationToken ct = default)
        => _github.CleanRepoToBaselineAsync(preserveFiles, commitMessage, branch ?? "main", ct);
}
