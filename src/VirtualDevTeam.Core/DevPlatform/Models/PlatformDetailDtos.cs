namespace VirtualDevTeam.Core.DevPlatform.Models;

/// <summary>
/// Aggregate payload returned by <c>GET /api/dashboard/platform/pull-request/{n}</c>.
/// Bundles the platform-neutral PR record with its conversation comments, review threads,
/// and changed-file paths so the dashboard's <c>PullRequestDetail.razor</c> can render the
/// full detail view in a single round-trip.
///
/// <para>
/// All collections default to empty when the corresponding capability call fails — the
/// endpoint catches per-call exceptions so a partial-platform-outage still renders the
/// header + body (better than a hard 500).
/// </para>
/// </summary>
public sealed record PullRequestDetailDto(
    PlatformPullRequest Pr,
    IReadOnlyList<PlatformComment> Comments,
    IReadOnlyList<PlatformReviewThread> ReviewThreads,
    IReadOnlyList<string> ChangedFiles);

/// <summary>
/// Aggregate payload returned by <c>GET /api/dashboard/platform/work-item/{n}</c>.
/// Bundles the platform-neutral work-item record with its comments for
/// <c>IssueDetail.razor</c>. Same partial-failure tolerance as <see cref="PullRequestDetailDto"/>.
/// </summary>
public sealed record WorkItemDetailDto(
    PlatformWorkItem WorkItem,
    IReadOnlyList<PlatformComment> Comments);
