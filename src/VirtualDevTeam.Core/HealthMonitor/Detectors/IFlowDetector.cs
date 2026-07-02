namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// A FlowMonitor detector inspects the current state of agents, workflow phase,
/// and platform resources, and returns 0..N findings describing anomalies.
///
/// Detectors are stateless modules — any state they need (e.g., last-seen timestamps)
/// is provided by the FlowMonitorService via <see cref="DetectorContext"/>. This keeps
/// the service in control of dedup + persistence rather than each detector reinventing it.
/// </summary>
public interface IFlowDetector
{
    /// <summary>Stable id used for logging, persistence, config gating, and dedup.</summary>
    string DetectorId { get; }

    /// <summary>Run a detection pass. Must complete quickly (≤2s) and never throw.</summary>
    Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct);
}

/// <summary>
/// Per-tick context handed to each detector. Wraps the read-only views detectors
/// need so they don't reach for DI'd services directly. Lets us mock state in tests.
///
/// **Cost model (T1.1)**: Agent + signal data is computed eagerly (cheap, in-process).
/// Platform views (PRs, work items, reviews, commits) are loaded LAZILY through
/// <see cref="Platform"/> — first call hits the API, subsequent calls within the same
/// tick return the cached result. Detectors that don't need platform data pay zero cost.
/// </summary>
public sealed class DetectorContext
{
    public required DateTimeOffset Now { get; init; }
    public required IReadOnlyList<AgentStateView> Agents { get; init; }
    public required string CurrentPhase { get; init; }
    public required IReadOnlyList<string> WorkflowSignals { get; init; }
    public required string EffectiveBranch { get; init; }

    /// <summary>
    /// Lazy, fault-tolerant platform views. Only call methods you need — each is
    /// fetched at most once per tick and cached. All methods return empty / null on
    /// platform errors (logged inside the view) so detectors stay simple.
    /// May be <see cref="NullPlatformView"/> when platform services are unavailable
    /// (e.g., before a project is opened) — every method on the null view returns empty.
    /// </summary>
    public required IPlatformView Platform { get; init; }
}

/// <summary>
/// Minimal view of an agent that detectors care about. Doesn't expose IAgent directly so
/// the detectors don't accidentally take actions on the agent (separation of concerns).
/// </summary>
public sealed record AgentStateView
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Role { get; init; }
    public required string Status { get; init; }
    public string? StatusReason { get; init; }
    public DateTimeOffset? StatusChangedAt { get; init; }
    /// <summary>
    /// Declared capabilities (typically from <c>SMEAgentDefinition.Capabilities</c> or
    /// the agent's role-specific defaults). Used by detectors that need to peer-compare
    /// agents (e.g. skip "agent stuck" findings when a higher-scoring peer should claim
    /// the work — mirrors <c>SpecialistEngineerAgent.RunAdditionalLoopWorkAsync</c>).
    /// Empty list when the agent declares no capabilities (a generalist).
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The PR number the agent is currently working on, if any. Populated from
    /// <see cref="IAgent.CurrentPrNumber"/>. Null when idle, pre-PR, or when the
    /// agent has already cleared its PR reference.
    /// </summary>
    public int? CurrentPrNumber { get; init; }
}

/// <summary>
/// Lazy, per-tick view over dev-platform resources (PRs, work items, reviews, commits).
/// Implementations cache results for the lifetime of one DetectorContext so multiple
/// detectors sharing the same context only pay the API cost once per tick.
/// All methods are exception-safe: errors are logged and surfaced as empty results.
/// </summary>
public interface IPlatformView
{
    /// <summary>Open pull requests in the working repo. Cached for the tick.</summary>
    Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct = default);

    /// <summary>Open work items / issues in the working repo. Cached for the tick.</summary>
    Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct = default);

    /// <summary>Unresolved review threads on a specific PR. Cached per-PR for the tick.</summary>
    Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct = default);

    /// <summary>Most-recent commit on a PR's head branch. Null if PR has no commits or fetch failed.</summary>
    Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct = default);
}

/// <summary>Detector-friendly projection of <c>PlatformPullRequest</c>.</summary>
public sealed record PullRequestView
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required string? AssignedAgent { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset? UpdatedAt { get; init; }
    public string? MergeableState { get; init; }
}

/// <summary>Detector-friendly projection of <c>PlatformWorkItem</c>.</summary>
public sealed record WorkItemView
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required string? AssignedAgent { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Detector-friendly projection of <c>PlatformReviewThread</c>.</summary>
public sealed record ReviewThreadView
{
    public required string ThreadId { get; init; }
    public required string FilePath { get; init; }
    public required int? Line { get; init; }
    public required string Author { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Detector-friendly projection of <c>PlatformCommitInfo</c>.</summary>
public sealed record CommitView
{
    public required string Sha { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset CommittedAt { get; init; }
}

/// <summary>
/// No-op platform view used when platform services are unavailable. Every method returns
/// an empty result. Lets detectors call <c>ctx.Platform.*</c> without null-checks.
/// </summary>
public sealed class NullPlatformView : IPlatformView
{
    public static readonly NullPlatformView Instance = new();
    private NullPlatformView() { }

    public Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PullRequestView>>(Array.Empty<PullRequestView>());

    public Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkItemView>>(Array.Empty<WorkItemView>());

    public Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReviewThreadView>>(Array.Empty<ReviewThreadView>());

    public Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct = default)
        => Task.FromResult<CommitView?>(null);
}
