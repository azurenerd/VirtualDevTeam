using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Per-tick lazy + cached view over the dev platform. Constructed fresh by FlowMonitorService
/// at the start of each tick and shared across all detectors in that tick — so multiple
/// detectors needing "open PRs" only pay for one API round-trip.
///
/// **Fault model**: every method swallows exceptions, logs at Warning, and returns an
/// empty / null result. Detectors must NEVER fail because of platform flakiness — the
/// FlowMonitor is supposed to be MORE reliable than the system it watches, per the
/// AutoGen "supervisor must be simpler than watched" principle (master plan T-E).
///
/// **Memory model**: instances are short-lived (~one tick = up to ~1min). The internal
/// dictionaries are local to the instance — when the next tick rolls, the whole view
/// is GC'd and rebuilt fresh.
/// </summary>
internal sealed class PerTickPlatformView : IPlatformView
{
    private readonly IPullRequestService? _prs;
    private readonly IWorkItemService? _workItems;
    private readonly IReviewService? _reviews;
    private readonly ILogger _logger;

    private Task<IReadOnlyList<PullRequestView>>? _openPrsTask;
    private Task<IReadOnlyList<WorkItemView>>? _openWorkItemsTask;
    private readonly Dictionary<int, Task<IReadOnlyList<ReviewThreadView>>> _threadsByPr = new();
    private readonly Dictionary<int, Task<CommitView?>> _latestCommitByPr = new();
    private readonly object _lock = new();

    /// <summary>
    /// Cross-tick cache for unresolved thread fetches. Thread data changes infrequently
    /// (only when a reviewer posts or resolves a thread), so a 120-second TTL avoids
    /// redundant API calls across consecutive FlowMonitor ticks while still converging
    /// within one stuck-threshold window.
    /// </summary>
    private static readonly ConcurrentDictionary<int, (DateTimeOffset FetchedAt, IReadOnlyList<ReviewThreadView> Threads)> _crossTickThreadCache = new();

    public PerTickPlatformView(
        IPullRequestService? prs,
        IWorkItemService? workItems,
        IReviewService? reviews,
        ILogger logger)
    {
        _prs = prs;
        _workItems = workItems;
        _reviews = reviews;
        _logger = logger;
    }

    public Task<IReadOnlyList<PullRequestView>> ListOpenPullRequestsAsync(CancellationToken ct = default)
    {
        lock (_lock) { _openPrsTask ??= LoadOpenPullRequestsAsync(ct); return _openPrsTask; }
    }

    public Task<IReadOnlyList<WorkItemView>> ListOpenWorkItemsAsync(CancellationToken ct = default)
    {
        lock (_lock) { _openWorkItemsTask ??= LoadOpenWorkItemsAsync(ct); return _openWorkItemsTask; }
    }

    public Task<IReadOnlyList<ReviewThreadView>> ListUnresolvedThreadsAsync(int prNumber, CancellationToken ct = default)
    {
        // Cross-tick cache: return cached data if fresh enough (120s TTL)
        if (_crossTickThreadCache.TryGetValue(prNumber, out var cached)
            && (DateTimeOffset.UtcNow - cached.FetchedAt).TotalSeconds < 120)
        {
            return Task.FromResult(cached.Threads);
        }

        lock (_lock)
        {
            if (!_threadsByPr.TryGetValue(prNumber, out var task))
            {
                task = LoadUnresolvedThreadsAsync(prNumber, ct);
                _threadsByPr[prNumber] = task;
            }
            return task;
        }
    }

    public Task<CommitView?> GetLatestCommitAsync(int prNumber, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_latestCommitByPr.TryGetValue(prNumber, out var task))
            {
                task = LoadLatestCommitAsync(prNumber, ct);
                _latestCommitByPr[prNumber] = task;
            }
            return task;
        }
    }

    private async Task<IReadOnlyList<PullRequestView>> LoadOpenPullRequestsAsync(CancellationToken ct)
    {
        if (_prs is null) return Array.Empty<PullRequestView>();
        try
        {
            var raw = await _prs.ListOpenAsync(ct).ConfigureAwait(false);
            return raw.Select(p => new PullRequestView
            {
                Number = p.Number,
                Title = p.Title,
                State = p.State,
                HeadBranch = p.HeadBranch,
                BaseBranch = p.BaseBranch,
                Labels = p.Labels.ToArray(),
                AssignedAgent = p.AssignedAgent,
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)),
                UpdatedAt = p.UpdatedAt.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(p.UpdatedAt.Value, DateTimeKind.Utc))
                    : null,
                MergeableState = p.MergeableState,
            }).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PerTickPlatformView: ListOpenPullRequestsAsync failed (returning empty)");
            return Array.Empty<PullRequestView>();
        }
    }

    private async Task<IReadOnlyList<WorkItemView>> LoadOpenWorkItemsAsync(CancellationToken ct)
    {
        if (_workItems is null) return Array.Empty<WorkItemView>();
        try
        {
            var raw = await _workItems.ListOpenAsync(ct).ConfigureAwait(false);
            return raw.Select(w => new WorkItemView
            {
                Number = w.Number,
                Title = w.Title,
                State = w.State,
                Labels = w.Labels.ToArray(),
                AssignedAgent = w.AssignedAgent,
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(w.CreatedAt, DateTimeKind.Utc)),
                UpdatedAt = w.UpdatedAt.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(w.UpdatedAt.Value, DateTimeKind.Utc))
                    : null,
            }).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PerTickPlatformView: ListOpenWorkItemsAsync failed (returning empty)");
            return Array.Empty<WorkItemView>();
        }
    }

    private async Task<IReadOnlyList<ReviewThreadView>> LoadUnresolvedThreadsAsync(int prNumber, CancellationToken ct)
    {
        if (_reviews is null) return Array.Empty<ReviewThreadView>();
        try
        {
            var threads = await _reviews.GetThreadsAsync(prNumber, ct).ConfigureAwait(false);
            var result = threads
                .Where(t => !t.IsResolved)
                .Select(t => new ReviewThreadView
                {
                    ThreadId = t.ThreadId,
                    FilePath = t.FilePath,
                    Line = t.Line,
                    Author = t.Author,
                    CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)),
                })
                .ToArray();
            _crossTickThreadCache[prNumber] = (DateTimeOffset.UtcNow, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PerTickPlatformView: ListUnresolvedThreadsAsync(pr={PR}) failed (returning empty)", prNumber);
            return Array.Empty<ReviewThreadView>();
        }
    }

    private async Task<CommitView?> LoadLatestCommitAsync(int prNumber, CancellationToken ct)
    {
        if (_prs is null) return null;
        try
        {
            var commits = await _prs.GetCommitsWithDatesAsync(prNumber, ct).ConfigureAwait(false);
            if (commits is null || commits.Count == 0) return null;
            // Latest = max CommittedAt (don't trust API ordering)
            var latest = commits.OrderByDescending(c => c.CommittedAt).First();
            return new CommitView
            {
                Sha = latest.Sha,
                Message = latest.Message,
                CommittedAt = new DateTimeOffset(DateTime.SpecifyKind(latest.CommittedAt, DateTimeKind.Utc)),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PerTickPlatformView: GetLatestCommitAsync(pr={PR}) failed (returning null)", prNumber);
            return null;
        }
    }
}
