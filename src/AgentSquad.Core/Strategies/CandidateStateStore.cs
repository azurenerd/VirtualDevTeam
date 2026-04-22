using System.Collections.Concurrent;
using System.Collections.Immutable;
using AgentSquad.Core.Strategies.Contracts;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Thread-safe in-memory store of active + recent-completed strategy candidate state,
/// fed by the <see cref="IStrategyEventSink"/> implementation. Consumed by the
/// dashboard <c>/strategies</c> page (Phase 4).
///
/// Active tasks stay in <see cref="_active"/> keyed by (runId, taskId). When a winner
/// arrives (or all candidates complete unsuccessfully), the task snapshot is moved
/// to <see cref="_recent"/>, a bounded ring buffer (default 100, configurable).
/// </summary>
public sealed class CandidateStateStore
{
    private readonly ConcurrentDictionary<(string RunId, string TaskId), TaskSnapshot> _active = new();
    private readonly object _recentLock = new();
    private readonly LinkedList<TaskSnapshot> _recent = new();
    private readonly int _recentCapacity;

    public CandidateStateStore(int recentCapacity = 100)
    {
        _recentCapacity = recentCapacity < 1 ? 1 : recentCapacity;
    }

    /// <summary>Fires on any state mutation. Listeners must be non-throwing and fast.</summary>
    public event Action<TaskSnapshot>? OnChange;

    public IReadOnlyList<TaskSnapshot> GetActiveTasks()
        => _active.Values.OrderByDescending(t => t.StartedAt).ToList();

    public IReadOnlyList<TaskSnapshot> GetRecentTasks(int limit = 50)
    {
        lock (_recentLock)
        {
            return _recent.Take(Math.Max(0, limit)).ToList();
        }
    }

    public void RecordStarted(CandidateStartedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        var snapshot = _active.AddOrUpdate(
            key,
            _ => new TaskSnapshot
            {
                RunId = e.RunId,
                TaskId = e.TaskId,
                StartedAt = e.At,
                Candidates = ImmutableDictionary<string, CandidateSnapshot>.Empty
                    .Add(e.StrategyId, new CandidateSnapshot
                    {
                        StrategyId = e.StrategyId,
                        State = CandidateState.Running,
                        StartedAt = e.At,
                    }),
            },
            (_, existing) => existing with
            {
                Candidates = existing.Candidates.SetItem(e.StrategyId, new CandidateSnapshot
                {
                    StrategyId = e.StrategyId,
                    State = CandidateState.Running,
                    StartedAt = e.At,
                }),
            });
        OnChange?.Invoke(snapshot);
    }

    public void RecordCompleted(CandidateCompletedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Running };

        var updated = existingCandidate with
        {
            State = CandidateState.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ElapsedSec = e.ElapsedSec,
            Succeeded = e.Succeeded,
            FailureReason = e.FailureReason,
            TokensUsed = e.TokensUsed,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordScored(CandidateScoredEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Completed };

        var updated = existingCandidate with
        {
            State = CandidateState.Scored,
            AcScore = e.AcScore,
            DesignScore = e.DesignScore,
            ReadabilityScore = e.ReadabilityScore,
            ScreenshotBase64 = e.ScreenshotBase64,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordWinner(WinnerSelectedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryRemove(key, out var task)) return;

        var winner = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c with { State = CandidateState.Winner }
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Winner };

        var finalSnapshot = task with
        {
            Candidates = task.Candidates.SetItem(e.StrategyId, winner),
            WinnerStrategyId = e.StrategyId,
            TieBreakReason = e.TieBreakReason,
            EvaluationElapsedSec = e.EvaluationElapsedSec,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        PushRecent(finalSnapshot);
        OnChange?.Invoke(finalSnapshot);
    }

    /// <summary>
    /// If an orchestration ends without a winner (all candidates failed), the
    /// orchestrator can call this to archive the task. Idempotent if the task is
    /// already archived.
    /// </summary>
    public void ArchiveTaskIfActive(string runId, string taskId, string? reason = null)
    {
        var key = (runId, taskId);
        if (!_active.TryRemove(key, out var task)) return;

        var archived = task with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            TieBreakReason = reason ?? task.TieBreakReason,
        };
        PushRecent(archived);
        OnChange?.Invoke(archived);
    }

    private void PushRecent(TaskSnapshot snapshot)
    {
        lock (_recentLock)
        {
            _recent.AddFirst(snapshot);
            while (_recent.Count > _recentCapacity)
                _recent.RemoveLast();
        }
    }
}

public enum CandidateState
{
    Pending,
    Running,
    Completed,
    Scored,
    Winner,
}

public sealed record CandidateSnapshot
{
    public required string StrategyId { get; init; }
    public required CandidateState State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? ElapsedSec { get; init; }
    public bool? Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public long? TokensUsed { get; init; }
    public int? AcScore { get; init; }
    public int? DesignScore { get; init; }
    public int? ReadabilityScore { get; init; }
    /// <summary>Base64-encoded PNG screenshot captured after build gate passed (null if not available).</summary>
    public string? ScreenshotBase64 { get; init; }
}

public sealed record TaskSnapshot
{
    public required string RunId { get; init; }
    public required string TaskId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required ImmutableDictionary<string, CandidateSnapshot> Candidates { get; init; }
    public string? WinnerStrategyId { get; init; }
    public string? TieBreakReason { get; init; }
    public double? EvaluationElapsedSec { get; init; }
}
