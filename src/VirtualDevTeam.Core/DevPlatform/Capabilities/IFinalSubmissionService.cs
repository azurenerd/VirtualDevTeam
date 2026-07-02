using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Capabilities;

/// <summary>
/// Publishes the final integration result from Local Dev Mode to the real platform
/// (GitHub or ADO). Creates one clean PR with all agent work squashed/merged.
/// The PR is NOT merged — a human reviews and merges it.
/// </summary>
public interface IFinalSubmissionService
{
    /// <summary>
    /// Push local work to the real platform and create a PR for human review.
    /// Returns the created PR. Idempotent — reuses existing PR if one was already created.
    /// </summary>
    Task<PlatformPullRequest> SubmitFinalPRAsync(
        string branchName,
        string title,
        string body,
        string baseBranch,
        CancellationToken ct = default);

    /// <summary>Whether a final PR has already been submitted for this run.</summary>
    Task<PlatformPullRequest?> GetExistingSubmissionAsync(CancellationToken ct = default);
}
