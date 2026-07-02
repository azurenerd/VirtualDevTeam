using VirtualDevTeam.Core.DevPlatform.Capabilities;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// <see cref="IRepositoryManagementService"/> stub for the local platform.
/// Local repos are managed externally — creation is not applicable.
/// </summary>
public sealed class LocalRepositoryManagementService : IRepositoryManagementService
{
    public Task<RepositoryCreationResult> CreateRepositoryAsync(
        string name, bool isPrivate = true, CancellationToken ct = default)
    {
        // Local platform: repos are managed externally (bare repo initialized via LocalBareRepoManager)
        return Task.FromResult(new RepositoryCreationResult(
            Success: false,
            RepositoryUrl: null,
            ErrorMessage: "Repository creation is not applicable for the local platform — repos are managed externally via LocalBareRepoManager"));
    }
}
