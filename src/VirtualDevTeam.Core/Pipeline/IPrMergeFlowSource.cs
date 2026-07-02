namespace VirtualDevTeam.Core.Pipeline;

/// <summary>
/// Contract for resolving a PR's merge-flow snapshot. Two implementations:
/// - InProcessPrMergeFlowResolver (Runner-side): does the actual work with direct access
///   to AgentRegistry, IAgentTaskTracker, IOptionsMonitor&lt;VirtualDevTeamConfig&gt;, etc.
/// - HttpPrMergeFlowSource (Standalone Dashboard-side): proxies to runner's
///   /api/dashboard/pr/{n}/merge-timeline endpoint.
///
/// <para>
/// Per R10 from the rubber-duck pass: the Razor component takes IPrMergeFlowSource and
/// doesn't know which implementation it gets. Standalone Dashboard works as long as the
/// Runner's API endpoint is reachable.
/// </para>
/// </summary>
public interface IPrMergeFlowSource
{
    /// <summary>
    /// Resolve the current merge-flow state for a PR. Implementations should cache
    /// per (prNumber) for ~10s to avoid hot-path API burn. Returns null when the PR
    /// is not found / not visible to this runner.
    /// </summary>
    Task<PrMergeFlowSnapshot?> GetSnapshotAsync(int prNumber, CancellationToken ct);
}
