using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Merging;

namespace VirtualDevTeam.Orchestrator.Merging;

/// <summary>
/// Serializes merge attempts so only one PR merges at a time.
/// Uses a SemaphoreSlim — NOT a persistent queue.
/// Tracks active/pending state for dashboard visibility.
/// </summary>
public sealed class MergeCoordinator : IMergeCoordinator
{
    private readonly SemaphoreSlim _mergeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, bool> _inProgressPrs = new();
    private readonly ILogger<MergeCoordinator> _logger;
    private int _pendingCount;
    private int? _activePrNumber;
    private string? _activeAgentId;
    private DateTime? _activeStartedAt;

    public MergeCoordinator(ILogger<MergeCoordinator> logger)
    {
        _logger = logger;
    }

    public async Task<MergeCoordinatorResult> RunExclusiveAsync(
        int prNumber,
        string mergerAgentId,
        Func<CancellationToken, Task<MergeOutcome>> mergeAction,
        CancellationToken ct)
    {
        // Dedup: if this PR is already being processed, skip
        if (!_inProgressPrs.TryAdd(prNumber, true))
        {
            _logger.LogDebug(
                "MergeCoordinator: PR #{PR} already in progress — skipping duplicate attempt from {Agent}",
                prNumber, mergerAgentId);
            return MergeCoordinatorResult.Skip(prNumber, "Already in progress");
        }

        Interlocked.Increment(ref _pendingCount);
        try
        {
            await _mergeLock.WaitAsync(ct);
            Interlocked.Decrement(ref _pendingCount);

            _activePrNumber = prNumber;
            _activeAgentId = mergerAgentId;
            _activeStartedAt = DateTime.UtcNow;

            try
            {
                _logger.LogInformation(
                    "MergeCoordinator: PR #{PR} acquired merge lock (agent: {Agent})",
                    prNumber, mergerAgentId);

                var sw = Stopwatch.StartNew();
                var outcome = await mergeAction(ct);
                sw.Stop();

                _logger.LogInformation(
                    "MergeCoordinator: PR #{PR} merge completed — {Outcome} in {Elapsed:F1}s",
                    prNumber, outcome, sw.Elapsed.TotalSeconds);

                return new MergeCoordinatorResult
                {
                    Outcome = outcome,
                    PrNumber = prNumber,
                    Detail = $"Completed in {sw.Elapsed.TotalSeconds:F1}s",
                };
            }
            finally
            {
                _activePrNumber = null;
                _activeAgentId = null;
                _activeStartedAt = null;
                _mergeLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref _pendingCount);
            throw;
        }
        finally
        {
            _inProgressPrs.TryRemove(prNumber, out _);
        }
    }

    public MergeQueueStatus GetStatus()
    {
        var activeStart = _activeStartedAt;
        return new MergeQueueStatus
        {
            PendingCount = _pendingCount,
            ActivePrNumber = _activePrNumber,
            ActiveAgentId = _activeAgentId,
            ActiveDuration = activeStart.HasValue
                ? DateTime.UtcNow - activeStart.Value
                : null,
        };
    }
}
