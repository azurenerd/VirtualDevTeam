using System.Collections.Immutable;
using VirtualDevTeam.Core.Strategies.Contracts;

namespace VirtualDevTeam.Core.Strategies.MediaCapture;

/// <summary>
/// Tracks media capture progress for a single candidate and emits events via
/// <see cref="IStrategyEventSink"/>. Auto-completes the previous running step
/// when a new step starts (safety net against missed CompleteStep calls).
/// </summary>
public sealed class MediaCaptureTracker : IMediaCaptureProgressSink
{
    private readonly string _runId;
    private readonly string _taskId;
    private readonly string _strategyId;
    private readonly Action<string, object> _eventEmitter;
    private readonly Dictionary<MediaCaptureStepId, StepState> _steps = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly object _lock = new();
    private MediaCaptureStepId? _currentStepId;

    public MediaCaptureTracker(
        string runId,
        string taskId,
        string strategyId,
        Action<string, object> eventEmitter)
    {
        _runId = runId;
        _taskId = taskId;
        _strategyId = strategyId;
        _eventEmitter = eventEmitter;

        foreach (var id in Enum.GetValues<MediaCaptureStepId>())
            _steps[id] = new StepState();
    }

    public void StartStep(MediaCaptureStepId stepId, string? detail = null)
    {
        lock (_lock)
        {
        // Auto-complete previous running step
        if (_currentStepId is { } prev && _steps[prev].Status == MediaCaptureStepStatus.Running)
            CompleteStep(prev);

        var state = _steps[stepId];
        state.Status = MediaCaptureStepStatus.Running;
        state.StartedAt = DateTimeOffset.UtcNow;
        state.Detail = detail;
        _currentStepId = stepId;
        }

        Emit(stepId, MediaCaptureStepStatus.Running, detail, null);
    }

    public void CompleteStep(MediaCaptureStepId stepId, string? detail = null)
    {
        string? emitDetail;
        double? emitElapsed;
        lock (_lock)
        {
        var state = _steps[stepId];
        state.Status = MediaCaptureStepStatus.Completed;
        state.CompletedAt = DateTimeOffset.UtcNow;
        state.ElapsedMs = state.StartedAt.HasValue
            ? (state.CompletedAt.Value - state.StartedAt.Value).TotalMilliseconds
            : null;
        if (detail is not null) state.Detail = detail;
        emitDetail = state.Detail;
        emitElapsed = state.ElapsedMs;
        }
        Emit(stepId, MediaCaptureStepStatus.Completed, emitDetail, emitElapsed);
    }

    public void FailStep(MediaCaptureStepId stepId, string? reason = null)
    {
        string? emitDetail;
        double? emitElapsed;
        lock (_lock)
        {
        var state = _steps[stepId];
        state.Status = MediaCaptureStepStatus.Failed;
        state.CompletedAt = DateTimeOffset.UtcNow;
        state.ElapsedMs = state.StartedAt.HasValue
            ? (state.CompletedAt.Value - state.StartedAt.Value).TotalMilliseconds
            : null;
        state.Detail = reason ?? state.Detail;
        emitDetail = state.Detail;
        emitElapsed = state.ElapsedMs;
        }
        Emit(stepId, MediaCaptureStepStatus.Failed, emitDetail, emitElapsed);
    }

    public void SkipStep(MediaCaptureStepId stepId, string? reason = null)
    {
        lock (_lock)
        {
        var state = _steps[stepId];
        state.Status = MediaCaptureStepStatus.Skipped;
        state.Detail = reason;
        }
        Emit(stepId, MediaCaptureStepStatus.Skipped, reason, null);
    }

    /// <summary>
    /// Skips all steps that are still <see cref="MediaCaptureStepStatus.Pending"/>
    /// and marks <see cref="MediaCaptureStepId.Complete"/> as failed.
    /// Call this when a gate failure aborts the capture pipeline early so the
    /// dashboard shows a definitive terminal state instead of orphaned pending pips.
    /// </summary>
    public void AbortRemaining(string reason)
    {
        lock (_lock)
        {
            foreach (var (id, state) in _steps)
            {
                if (id == MediaCaptureStepId.Complete) continue; // handled below
                if (state.Status == MediaCaptureStepStatus.Pending)
                {
                    state.Status = MediaCaptureStepStatus.Skipped;
                    state.Detail = reason;
                }
            }
            // Mark Complete as failed so the dashboard's "✓ done" / "✗ failed" logic fires
            var completeState = _steps[MediaCaptureStepId.Complete];
            if (completeState.Status == MediaCaptureStepStatus.Pending)
            {
                completeState.Status = MediaCaptureStepStatus.Failed;
                completeState.Detail = reason;
                completeState.CompletedAt = DateTimeOffset.UtcNow;
            }
            _currentStepId = null;
        }
        // Emit events outside the lock for each skipped step + the failed Complete
        foreach (var id in Enum.GetValues<MediaCaptureStepId>())
        {
            if (id == MediaCaptureStepId.Complete) continue;
            var state = _steps[id];
            if (state.Status == MediaCaptureStepStatus.Skipped && state.Detail == reason)
                Emit(id, MediaCaptureStepStatus.Skipped, reason, null);
        }
        Emit(MediaCaptureStepId.Complete, MediaCaptureStepStatus.Failed, reason, null);
    }

    /// <summary>Build an immutable snapshot of the current progress state.</summary>
    public MediaCaptureProgressSnapshot GetSnapshot()
    {
        lock (_lock)
        {
        var steps = _steps
            .OrderBy(kv => kv.Key)
            .Select(kv => new MediaCaptureStep(
                kv.Key, kv.Value.Status, kv.Value.Detail,
                kv.Value.StartedAt, kv.Value.CompletedAt, kv.Value.ElapsedMs))
            .ToImmutableList();

        return new MediaCaptureProgressSnapshot
        {
            Steps = steps,
            CurrentStepId = _currentStepId,
            StartedAt = _startedAt,
            TotalElapsedMs = (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds,
        };
        }
    }

    private void Emit(MediaCaptureStepId stepId, MediaCaptureStepStatus status, string? detail, double? elapsedMs)
    {
        try
        {
            _eventEmitter(StrategyEvents.MediaCaptureProgress, new MediaCaptureProgressEvent(
                _runId, _taskId, _strategyId, stepId, status, detail, DateTimeOffset.UtcNow, elapsedMs));
        }
        catch
        {
            // Never let event emission break the capture pipeline.
        }
    }

    private sealed class StepState
    {
        public MediaCaptureStepStatus Status = MediaCaptureStepStatus.Pending;
        public string? Detail;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? CompletedAt;
        public double? ElapsedMs;
    }
}
