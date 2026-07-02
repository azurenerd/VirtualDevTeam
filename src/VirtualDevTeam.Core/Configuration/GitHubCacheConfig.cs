namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Controls the in-memory read cache inside <c>GitHubService</c>.
/// Operators can tune or disable caching without touching code.
/// </summary>
public sealed class GitHubCacheConfig
{
    /// <summary>
    /// Master toggle. Set to <c>false</c> to bypass caching entirely (useful for debugging
    /// or for repos where eventual consistency is not acceptable).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long (seconds) to cache open-list results:
    /// <c>GetOpenIssuesAsync</c>, <c>GetOpenPullRequestsAsync</c>,
    /// <c>GetAllIssuesAsync</c>, <c>GetAllPullRequestsAsync</c>, <c>GetMergedPullRequestsAsync</c>.
    /// Default: 15 s. Tune up for low-mutation runs; tune down if agents must see changes faster.
    /// </summary>
    public int ListOpenTtlSeconds { get; set; } = 15;

    /// <summary>
    /// How long (seconds) to cache per-resource results:
    /// <c>GetIssueCommentsAsync</c>, <c>GetPullRequestCommentsAsync</c>.
    /// Default: 30 s.
    /// </summary>
    public int GetByNumberTtlSeconds { get; set; } = 30;

    /// <summary>
    /// How long (seconds) to cache label-filtered issue lists:
    /// <c>GetIssuesByLabelAsync</c> (both overloads).
    /// Default: 15 s.
    /// </summary>
    public int ListByLabelTtlSeconds { get; set; } = 15;
}
