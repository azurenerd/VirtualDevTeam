using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// No-op implementation used when NOT in LDP mode. Throws if called —
/// submission only makes sense in Local Dev Mode.
/// </summary>
internal sealed class NoOpFinalSubmissionService : IFinalSubmissionService
{
    public Task<PlatformPullRequest> SubmitFinalPRAsync(
        string branchName, string title, string body, string baseBranch,
        CancellationToken ct = default)
        => throw new InvalidOperationException("IFinalSubmissionService is only available in Local Dev Mode");

    public Task<PlatformPullRequest?> GetExistingSubmissionAsync(CancellationToken ct = default)
        => Task.FromResult<PlatformPullRequest?>(null);
}
