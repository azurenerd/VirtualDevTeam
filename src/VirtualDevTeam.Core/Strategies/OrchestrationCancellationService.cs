using System.Collections.Concurrent;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Provides per-task and per-candidate cancellation support for strategy orchestration.
/// Dashboard can request cancellation via the REST API; the orchestrator
/// checks <see cref="IsCancellationRequested"/> during its run loop.
/// </summary>
public interface IOrchestrationCancellationService
{
    /// <summary>Register a CancellationTokenSource for a task so it can be cancelled externally.</summary>
    void Register(string runId, string taskId, CancellationTokenSource cts);

    /// <summary>Register a CancellationTokenSource for a specific candidate within a task.</summary>
    void RegisterCandidate(string runId, string taskId, string strategyId, CancellationTokenSource cts);

    /// <summary>Unregister when task completes normally.</summary>
    void Unregister(string runId, string taskId);

    /// <summary>Unregister a specific candidate when it completes.</summary>
    void UnregisterCandidate(string runId, string taskId, string strategyId);

    /// <summary>Request cancellation of a running orchestration. Returns false if not found.</summary>
    bool RequestCancellation(string runId, string taskId);

    /// <summary>Request cancellation of a specific candidate. Returns false if not found or already done.</summary>
    bool RequestCandidateCancellation(string runId, string taskId, string strategyId);

    /// <summary>Check if cancellation was requested (without cancelling the token).</summary>
    bool IsCancellationRequested(string runId, string taskId);

    /// <summary>Check if a specific candidate was cancelled.</summary>
    bool IsCandidateCancelled(string runId, string taskId, string strategyId);

    /// <summary>Request a reset (cancel + retry) of a specific candidate. Returns false if not found.</summary>
    bool RequestCandidateReset(string runId, string taskId, string strategyId);

    /// <summary>Check if a reset was requested for a candidate (as opposed to a permanent cancel).</summary>
    bool IsResetRequested(string runId, string taskId, string strategyId);

    /// <summary>Clear the reset flag after the orchestrator has processed it.</summary>
    void ClearResetFlag(string runId, string taskId, string strategyId);

    /// <summary>Get all currently registered (active) orchestration task IDs.</summary>
    IReadOnlyList<(string RunId, string TaskId)> GetActiveOrchestrations();

    /// <summary>
    /// Request cancellation AND mark that FlowMonitor wants emergency winner selection.
    /// Distinguished from user-requested cancel (which should return Empty).
    /// </summary>
    bool RequestEmergencyPromotion(string runId, string taskId);

    /// <summary>Check if the cancellation was an emergency promotion (vs user cancel).</summary>
    bool IsEmergencyPromotion(string runId, string taskId);
}

public sealed class OrchestrationCancellationService : IOrchestrationCancellationService
{
    private readonly ConcurrentDictionary<(string RunId, string TaskId), CancellationTokenSource> _sources = new();
    private readonly ConcurrentDictionary<(string RunId, string TaskId, string StrategyId), CancellationTokenSource> _candidateSources = new();
    private readonly ConcurrentDictionary<(string RunId, string TaskId, string StrategyId), bool> _resetFlags = new();
    private readonly ConcurrentDictionary<(string RunId, string TaskId), bool> _emergencyPromotionFlags = new();

    public void Register(string runId, string taskId, CancellationTokenSource cts)
        => _sources.TryAdd((runId, taskId), cts);

    public void RegisterCandidate(string runId, string taskId, string strategyId, CancellationTokenSource cts)
        => _candidateSources.TryAdd((runId, taskId, strategyId), cts);

    public void UnregisterCandidate(string runId, string taskId, string strategyId)
        => _candidateSources.TryRemove((runId, taskId, strategyId), out _);

    public bool RequestCancellation(string runId, string taskId)
    {
        if (!_sources.TryGetValue((runId, taskId), out var cts))
            return false;

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool RequestCandidateCancellation(string runId, string taskId, string strategyId)
    {
        if (!_candidateSources.TryGetValue((runId, taskId, strategyId), out var cts))
            return false;

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool IsCancellationRequested(string runId, string taskId)
    {
        if (!_sources.TryGetValue((runId, taskId), out var cts))
            return false;
        return cts.IsCancellationRequested;
    }

    public bool IsCandidateCancelled(string runId, string taskId, string strategyId)
    {
        if (!_candidateSources.TryGetValue((runId, taskId, strategyId), out var cts))
            return false;
        return cts.IsCancellationRequested;
    }

    public bool RequestCandidateReset(string runId, string taskId, string strategyId)
    {
        _resetFlags[(runId, taskId, strategyId)] = true;
        return RequestCandidateCancellation(runId, taskId, strategyId);
    }

    public bool IsResetRequested(string runId, string taskId, string strategyId)
        => _resetFlags.TryGetValue((runId, taskId, strategyId), out var v) && v;

    public void ClearResetFlag(string runId, string taskId, string strategyId)
        => _resetFlags.TryRemove((runId, taskId, strategyId), out _);

    public bool RequestEmergencyPromotion(string runId, string taskId)
    {
        _emergencyPromotionFlags[(runId, taskId)] = true;
        return RequestCancellation(runId, taskId);
    }

    public bool IsEmergencyPromotion(string runId, string taskId)
        => _emergencyPromotionFlags.TryGetValue((runId, taskId), out var v) && v;

    public void Unregister(string runId, string taskId)
    {
        _sources.TryRemove((runId, taskId), out _);
        _emergencyPromotionFlags.TryRemove((runId, taskId), out _);
        // Clean up all candidate registrations for this task
        foreach (var key in _candidateSources.Keys.Where(k => k.RunId == runId && k.TaskId == taskId).ToList())
            _candidateSources.TryRemove(key, out _);
    }

    public IReadOnlyList<(string RunId, string TaskId)> GetActiveOrchestrations()
        => _sources.Keys.ToList();
}
