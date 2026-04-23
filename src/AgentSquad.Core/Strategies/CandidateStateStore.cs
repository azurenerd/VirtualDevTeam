using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using AgentSquad.Core.Persistence;
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
///
/// Completed tasks are persisted to SQLite via <see cref="AgentStateStore"/> and
/// rehydrated on construction so data survives runner restarts.
/// </summary>
/// </summary>
public sealed class CandidateStateStore
{
    private readonly ConcurrentDictionary<(string RunId, string TaskId), TaskSnapshot> _active = new();
    private readonly object _recentLock = new();
    private readonly LinkedList<TaskSnapshot> _recent = new();
    private readonly int _recentCapacity;
    private readonly AgentStateStore? _persistence;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public CandidateStateStore(AgentStateStore? persistence = null, int recentCapacity = 100)
    {
        _recentCapacity = recentCapacity < 1 ? 1 : recentCapacity;
        _persistence = persistence;
        HydrateFromSqlite();
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

    public void RecordEvaluated(CandidateEvaluatedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Completed };

        var updated = existingCandidate with
        {
            State = CandidateState.Evaluated,
            Survived = e.Survived,
            ScreenshotBase64 = e.ScreenshotBase64 ?? existingCandidate.ScreenshotBase64,
            JudgeSkippedReason = e.JudgeSkippedReason,
            // For failed-gate candidates, override FailureReason with gate detail
            FailureReason = e.Survived ? existingCandidate.FailureReason : (e.FailureDetail ?? existingCandidate.FailureReason),
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

    public void RecordDetail(CandidateDetailEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        if (!task.Candidates.TryGetValue(e.StrategyId, out var existing)) return;

        var updated = existing with { ExecutionSummary = e.Summary };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, ex) => ex with { Candidates = ex.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    private const int MaxActiveActivityEntries = 200;
    private const int MaxArchivedActivityEntries = 50;

    public void RecordActivity(CandidateActivityEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        if (!task.Candidates.TryGetValue(e.StrategyId, out var existing)) return;

        var log = existing.ActivityLog.Count >= MaxActiveActivityEntries
            ? existing.ActivityLog.RemoveRange(0, existing.ActivityLog.Count - MaxActiveActivityEntries + 1).Add(e.Activity)
            : existing.ActivityLog.Add(e.Activity);

        var updated = existing with { ActivityLog = log };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, ex) => ex with { Candidates = ex.Candidates.SetItem(e.StrategyId, updated) });
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
        // Trim activity logs when archiving to bound memory in the recent buffer.
        var trimmed = snapshot with
        {
            Candidates = snapshot.Candidates.ToImmutableDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ActivityLog.Count > MaxArchivedActivityEntries
                    ? kvp.Value with { ActivityLog = kvp.Value.ActivityLog.RemoveRange(0, kvp.Value.ActivityLog.Count - MaxArchivedActivityEntries) }
                    : kvp.Value),
        };
        lock (_recentLock)
        {
            _recent.AddFirst(trimmed);
            while (_recent.Count > _recentCapacity)
                _recent.RemoveLast();
        }

        // Persist to SQLite (best-effort — don't crash the pipeline)
        PersistToSqlite(trimmed);
    }

    private void PersistToSqlite(TaskSnapshot snapshot)
    {
        if (_persistence is null) return;
        try
        {
            var record = new StrategyTaskRecord
            {
                RunId = snapshot.RunId,
                TaskId = snapshot.TaskId,
                StartedAt = snapshot.StartedAt,
                CompletedAt = snapshot.CompletedAt,
                WinnerStrategyId = snapshot.WinnerStrategyId,
                TieBreakReason = snapshot.TieBreakReason,
                EvaluationElapsedSec = snapshot.EvaluationElapsedSec,
                Candidates = snapshot.Candidates.Values.Select(c => new StrategyCandidateRecord
                {
                    StrategyId = c.StrategyId,
                    State = c.State.ToString(),
                    StartedAt = c.StartedAt,
                    CompletedAt = c.CompletedAt,
                    ElapsedSec = c.ElapsedSec,
                    Succeeded = c.Succeeded,
                    FailureReason = c.FailureReason,
                    TokensUsed = c.TokensUsed,
                    AcScore = c.AcScore,
                    DesignScore = c.DesignScore,
                    ReadabilityScore = c.ReadabilityScore,
                    Survived = c.Survived,
                    JudgeSkippedReason = c.JudgeSkippedReason,
                    ExecutionSummaryJson = c.ExecutionSummary is not null
                        ? JsonSerializer.Serialize(c.ExecutionSummary, _jsonOpts) : null,
                    ScreenshotBase64 = c.ScreenshotBase64,
                }).ToList(),
            };
            _persistence.SaveStrategyTask(record);
        }
        catch
        {
            // Best-effort persistence — log failures are acceptable
        }
    }

    private void HydrateFromSqlite()
    {
        if (_persistence is null) return;
        try
        {
            var tasks = _persistence.LoadRecentStrategyTasks(_recentCapacity);
            lock (_recentLock)
            {
                foreach (var task in tasks)
                {
                    var candidates = task.Candidates.ToImmutableDictionary(
                        c => c.StrategyId,
                        c => new CandidateSnapshot
                        {
                            StrategyId = c.StrategyId,
                            State = Enum.TryParse<CandidateState>(c.State, out var s) ? s : CandidateState.Completed,
                            StartedAt = c.StartedAt,
                            CompletedAt = c.CompletedAt,
                            ElapsedSec = c.ElapsedSec,
                            Succeeded = c.Succeeded,
                            FailureReason = c.FailureReason,
                            TokensUsed = c.TokensUsed,
                            AcScore = c.AcScore,
                            DesignScore = c.DesignScore,
                            ReadabilityScore = c.ReadabilityScore,
                            Survived = c.Survived,
                            JudgeSkippedReason = c.JudgeSkippedReason,
                            ExecutionSummary = c.ExecutionSummaryJson is not null
                                ? JsonSerializer.Deserialize<CandidateExecutionSummary>(c.ExecutionSummaryJson, _jsonOpts) : null,
                            ScreenshotBase64 = c.ScreenshotBase64,
                        });

                    var snapshot = new TaskSnapshot
                    {
                        RunId = task.RunId,
                        TaskId = task.TaskId,
                        StartedAt = task.StartedAt,
                        CompletedAt = task.CompletedAt,
                        Candidates = candidates,
                        WinnerStrategyId = task.WinnerStrategyId,
                        TieBreakReason = task.TieBreakReason,
                        EvaluationElapsedSec = task.EvaluationElapsedSec,
                    };
                    _recent.AddLast(snapshot);
                }
            }
        }
        catch
        {
            // Best-effort hydration — start fresh if DB is corrupted
        }
    }
}

public enum CandidateState
{
    Pending,
    Running,
    Completed,
    /// <summary>Post-evaluation: build gates ran, screenshot captured, but LLM judge may not have scored.</summary>
    Evaluated,
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
    /// <summary>True if the candidate survived build gates (null if evaluation hasn't run yet).</summary>
    public bool? Survived { get; init; }
    /// <summary>Why the LLM judge was skipped, e.g. "sole-survivor". Null when judge ran normally.</summary>
    public string? JudgeSkippedReason { get; init; }
    /// <summary>Post-execution summary with file changes, metrics, logs, and judge reasoning. Null until detail event received.</summary>
    public CandidateExecutionSummary? ExecutionSummary { get; init; }
    /// <summary>Real-time activity log entries from framework execution. Immutable; bounded to 200 active, trimmed to 50 on archive.</summary>
    public ImmutableList<ActivityEntry> ActivityLog { get; init; } = ImmutableList<ActivityEntry>.Empty;
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
