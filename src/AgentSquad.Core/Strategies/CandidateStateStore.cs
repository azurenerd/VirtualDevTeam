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
public sealed class CandidateStateStore : IDisposable
{
    private readonly ConcurrentDictionary<(string RunId, string TaskId), TaskSnapshot> _active = new();
    private readonly object _recentLock = new();
    private readonly LinkedList<TaskSnapshot> _recent = new();
    private readonly int _recentCapacity;
    private readonly AgentStateStore? _persistence;
    private readonly Timer? _flushTimer;

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

        // Periodically flush active tasks to SQLite so data survives runner restarts.
        if (_persistence is not null)
            _flushTimer = new Timer(_ => FlushActiveTasks(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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
            VisualsScore = e.VisualsScore,
            ScreenshotBase64 = e.ScreenshotBase64,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordInitialScored(CandidateInitialScoredEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Evaluated };

        var updated = existingCandidate with
        {
            State = CandidateState.InitialScored,
            InitialAcScore = e.AcScore,
            InitialDesignScore = e.DesignScore,
            InitialReadabilityScore = e.ReadabilityScore,
            InitialVisualsScore = e.VisualsScore,
            JudgeFeedback = e.Feedback,
            InitialScreenshotBase64 = e.ScreenshotBase64 ?? existingCandidate.ScreenshotBase64,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordRevisionStarted(CandidateRevisionStartedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.InitialScored };

        var updated = existingCandidate with
        {
            State = CandidateState.Revising,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordRevisionCompleted(CandidateRevisionCompletedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Revising };

        var updated = existingCandidate with
        {
            // Stay in Revising state — will transition to Scored when final judge runs
            RevisionElapsedSec = e.RevisionElapsedSec,
            TokensUsed = (existingCandidate.TokensUsed ?? 0) + (e.TokensUsed ?? 0),
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordProgress(EvaluationProgressEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { CurrentPhase = e.Phase, ProgressDetail = e.Detail },
            (_, existing) => existing with { CurrentPhase = e.Phase, ProgressDetail = e.Detail });
        OnChange?.Invoke(snapshot);
    }

    public void RecordRetryStarted(CandidateRetryStartedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Completed };

        var updated = existingCandidate with
        {
            State = CandidateState.Retrying,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordRetryCompleted(CandidateRetryCompletedEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Retrying };

        var updated = existingCandidate with
        {
            State = CandidateState.Completed, // Back to completed — will go through evaluation again
            Succeeded = e.Succeeded,
            FailureReason = e.Succeeded ? null : e.FailureReason,
            TokensUsed = (existingCandidate.TokensUsed ?? 0) + (e.TokensUsed ?? 0),
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
    }

    public void RecordCancelled(OrchestrationCancelledEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryRemove(key, out var task)) return;

        var archived = task with
        {
            CompletedAt = e.At,
            Cancelled = true,
            TieBreakReason = $"cancelled: {e.Reason}",
        };
        PushRecent(archived);
        OnChange?.Invoke(archived);
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
                    InitialAcScore = c.InitialAcScore,
                    InitialDesignScore = c.InitialDesignScore,
                    InitialReadabilityScore = c.InitialReadabilityScore,
                    InitialVisualsScore = c.InitialVisualsScore,
                    JudgeFeedback = c.JudgeFeedback,
                    InitialScreenshotBase64 = c.InitialScreenshotBase64,
                    RevisionElapsedSec = c.RevisionElapsedSec,
                    RevisionSkippedReason = c.RevisionSkippedReason,
                    ActivityLog = c.ActivityLog.Select(a => new StrategyActivityLogEntry(
                        a.Timestamp, a.Category, a.Message,
                        a.Metadata is { Count: > 0 } ? JsonSerializer.Serialize(a.Metadata, _jsonOpts) : null
                    )).ToList(),
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
                            InitialAcScore = c.InitialAcScore,
                            InitialDesignScore = c.InitialDesignScore,
                            InitialReadabilityScore = c.InitialReadabilityScore,
                            InitialVisualsScore = c.InitialVisualsScore,
                            JudgeFeedback = c.JudgeFeedback,
                            InitialScreenshotBase64 = c.InitialScreenshotBase64,
                            RevisionElapsedSec = c.RevisionElapsedSec,
                            RevisionSkippedReason = c.RevisionSkippedReason,
                            ActivityLog = c.ActivityLog.Count > 0
                                ? c.ActivityLog.Select(a => new ActivityEntry(
                                    a.Timestamp, a.Category, a.Message, null)).ToImmutableList()
                                : ImmutableList<ActivityEntry>.Empty,
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

    /// <summary>
    /// Persist all currently-active tasks to SQLite so they survive runner restarts.
    /// Uses INSERT OR REPLACE so completed-task writes from PushRecent overwrite these.
    /// </summary>
    private void FlushActiveTasks()
    {
        if (_persistence is null) return;
        try
        {
            foreach (var task in _active.Values)
            {
                PersistToSqlite(task);
            }
        }
        catch
        {
            // Best-effort — don't crash the timer
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        // Final flush before shutdown
        FlushActiveTasks();
    }

    /// <summary>
    /// Reset all in-memory state and re-hydrate from the (possibly reconfigured) SQLite store.
    /// Pauses the flush timer during reset to prevent cross-writes.
    /// Call after AgentStateStore.Reconfigure() when the target repo changes.
    /// </summary>
    public void Reset()
    {
        // Pause the flush timer to prevent races
        _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            _active.Clear();
            lock (_recentLock) { _recent.Clear(); }
            HydrateFromSqlite();
        }
        finally
        {
            // Resume the flush timer
            _flushTimer?.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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
    /// <summary>Initial judge scores received; awaiting revision round.</summary>
    InitialScored,
    /// <summary>Revision attempt in progress.</summary>
    Revising,
    /// <summary>Gate-failed candidate retrying from scratch.</summary>
    Retrying,
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
    /// <summary>Visual quality score (0-10). Null when visual scoring is not applicable (non-visual task). 0 = visual task but screenshot missing or error page.</summary>
    public int? VisualsScore { get; init; }
    /// <summary>Base64-encoded PNG screenshot captured after build gate passed (null if not available).</summary>
    public string? ScreenshotBase64 { get; init; }
    // ── Revision round fields (all nullable for backward compat) ──
    /// <summary>Initial acceptance criteria score from first judge round. Null when revision round is disabled.</summary>
    public int? InitialAcScore { get; init; }
    /// <summary>Initial design score from first judge round.</summary>
    public int? InitialDesignScore { get; init; }
    /// <summary>Initial readability score from first judge round.</summary>
    public int? InitialReadabilityScore { get; init; }
    /// <summary>Initial visual quality score from first judge round.</summary>
    public int? InitialVisualsScore { get; init; }
    /// <summary>Judge feedback for revision (empty when all scores >= 8 or revision disabled).</summary>
    public string? JudgeFeedback { get; init; }
    /// <summary>Rubber-duck adversarial feedback for revision.</summary>
    public string? RubberDuckFeedback { get; init; }
    /// <summary>Screenshot from the initial round (before revision).</summary>
    public string? InitialScreenshotBase64 { get; init; }
    /// <summary>Wall-clock seconds for the revision attempt. Null when no revision ran.</summary>
    public double? RevisionElapsedSec { get; init; }
    /// <summary>Why revision was skipped (e.g., "sole-survivor", "disabled", "all-scores-high"). Null when revision ran.</summary>
    public string? RevisionSkippedReason { get; init; }
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
    /// <summary>Current evaluation phase (e.g., "candidates-running", "gates-evaluating", "judging", "retrying-failed").</summary>
    public string? CurrentPhase { get; init; }
    /// <summary>Human-readable progress detail string.</summary>
    public string? ProgressDetail { get; init; }
    /// <summary>True if this orchestration was cancelled by the user.</summary>
    public bool Cancelled { get; init; }
}
