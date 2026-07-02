using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Strategies.Contracts;
using VirtualDevTeam.Core.Strategies.Preview;

namespace VirtualDevTeam.Core.Strategies;

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

    /// <summary>
    /// PR link info that arrived BEFORE the first <see cref="CandidateStartedEvent"/> created
    /// an active snapshot. Drained by <see cref="RecordStarted"/> when the snapshot first
    /// appears so the link is preserved across the create-then-update race.
    /// </summary>
    private readonly ConcurrentDictionary<(string RunId, string TaskId), TaskPrLinkedEvent> _pendingPrLinks = new();
    private readonly object _recentLock = new();
    // Serializes read-modify-write sequences against _active. ConcurrentDictionary
    // alone is insufficient because each Record* method reads an existing
    // CandidateSnapshot, computes a merged "updated" value, then writes via
    // AddOrUpdate; if a concurrent Record* thread mutates the same candidate in
    // between, its update is clobbered (the local "updated" was built from a
    // stale read). Most visible symptom: high-frequency RecordActivity racing
    // RecordEvaluated wiped out AnimatedGifPath/VideoPath/CaptureMetrics so the
    // dashboard's collapsed media badges never appeared. The ?? preservation in
    // each Record* only protects against the same-thread stale read, not against
    // cross-thread races. The lock serializes those critical sections; all
    // operations are fast in-memory mutations so contention impact is negligible.
    private readonly object _activeLock = new();
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

    /// <summary>
    /// Get a specific candidate's snapshot from an active task. Returns null if not found.
    /// </summary>
    public CandidateSnapshot? GetCandidateSnapshot(string runId, string taskId, string strategyId)
    {
        var key = (runId, taskId);
        if (_active.TryGetValue(key, out var task) &&
            task.Candidates.TryGetValue(strategyId, out var candidate))
            return candidate;
        return null;
    }

    /// <summary>
    /// Restore a full media capture progress snapshot for a candidate (used during recovery).
    /// </summary>
    public void RestoreMediaCaptureProgress(string runId, string taskId, string strategyId,
        MediaCapture.MediaCaptureProgressSnapshot progress)
    {
        var key = (runId, taskId);
        lock (_activeLock)
        {
            if (!_active.TryGetValue(key, out var task)) return;
            if (!task.Candidates.TryGetValue(strategyId, out var candidate)) return;
            var updated = candidate with { MediaCaptureProgress = progress };
            _active.AddOrUpdate(key,
                _ => task with { Candidates = task.Candidates.SetItem(strategyId, updated) },
                (_, ex) => ex with { Candidates = ex.Candidates.SetItem(strategyId, updated) });
        }
    }

    public void RecordStarted(CandidateStartedEvent e)
    {
        lock (_activeLock)
        {
        var key = (e.RunId, e.TaskId);

        // If a TaskPrLinkedEvent arrived BEFORE this CandidateStarted (orchestrator
        // emits the link before RunCandidatesAsync), apply the PR fields when the
        // active snapshot is first created so the link isn't lost in the race.
        _pendingPrLinks.TryRemove(key, out var pendingPr);

        var snapshot = _active.AddOrUpdate(
            key,
            _ => new TaskSnapshot
            {
                RunId = e.RunId,
                TaskId = e.TaskId,
                TaskTitle = e.TaskTitle,
                StartedAt = e.At,
                Candidates = ImmutableDictionary<string, CandidateSnapshot>.Empty
                    .Add(e.StrategyId, new CandidateSnapshot
                    {
                        StrategyId = e.StrategyId,
                        State = CandidateState.Running,
                        StartedAt = e.At,
                    }),
                PrNumber = pendingPr?.PrNumber,
                PrUrl = pendingPr?.PrUrl,
                PrTitle = pendingPr?.PrTitle,
                Wave = e.Wave,
            },
            (_, existing) => existing with
            {
                Candidates = existing.Candidates.SetItem(e.StrategyId, new CandidateSnapshot
                {
                    StrategyId = e.StrategyId,
                    State = CandidateState.Running,
                    StartedAt = e.At,
                }),
                // Pending link wins over an existing-but-null PR — never clobber a
                // link that was already applied to an existing snapshot.
                PrNumber = existing.PrNumber ?? pendingPr?.PrNumber,
                PrUrl = existing.PrUrl ?? pendingPr?.PrUrl,
                PrTitle = existing.PrTitle ?? pendingPr?.PrTitle,
                // Wave: same "first-write wins, never clobber" rule.
                Wave = existing.Wave ?? e.Wave,
                // TaskTitle: keep first non-null value (title is stable).
                TaskTitle = existing.TaskTitle ?? e.TaskTitle,
            });
        OnChange?.Invoke(snapshot);
        }
    }

    public void RecordCompleted(CandidateCompletedEvent e)
    {
        lock (_activeLock)
        {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        var existingCandidate = task.Candidates.TryGetValue(e.StrategyId, out var c)
            ? c
            : new CandidateSnapshot { StrategyId = e.StrategyId, State = CandidateState.Running };

        var newState = e.FailureReason == "cancelled-by-user" ? CandidateState.Cancelled : CandidateState.Completed;
        var updated = existingCandidate with
        {
            State = newState,
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
    }

    public void RecordEvaluated(CandidateEvaluatedEvent e)
    {
        lock (_activeLock)
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
            ScreenshotPaths = e.ScreenshotPaths ?? existingCandidate.ScreenshotPaths,
            VideoPath = e.VideoPath ?? existingCandidate.VideoPath,
            AnimatedGifPath = e.AnimatedGifPath ?? existingCandidate.AnimatedGifPath,
            // Producer-supplied preview categorization. Default-stable (PlaywrightScreenshot)
            // means existing event emitters that don't pass PreviewSource keep existing
            // dashboard behaviour; only ImageAssets/Diagrams flip the badge.
            PreviewSource = e.PreviewSource ?? existingCandidate.PreviewSource,
            IncludedAssetPaths = e.IncludedAssetPaths ?? existingCandidate.IncludedAssetPaths,
            SecondaryPreviewBase64 = e.SecondaryPreviewBase64 ?? existingCandidate.SecondaryPreviewBase64,
            SecondaryAssetPaths = e.SecondaryAssetPaths ?? existingCandidate.SecondaryAssetPaths,
            SecondaryPreviewSource = e.SecondaryPreviewSource ?? existingCandidate.SecondaryPreviewSource,
            JudgeSkippedReason = e.JudgeSkippedReason,
            // For failed-gate candidates, override FailureReason with gate detail
            FailureReason = e.Survived ? existingCandidate.FailureReason : (e.FailureDetail ?? existingCandidate.FailureReason),
            CaptureMetrics = e.CaptureMetrics ?? existingCandidate.CaptureMetrics,
            PageAnalysis = e.PageAnalysis ?? existingCandidate.PageAnalysis,
            AppBaseUrl = e.AppBaseUrl ?? existingCandidate.AppBaseUrl,
            // Derive runtime error summary from PageAnalysis for dashboard display
            HasRuntimeErrors = (e.PageAnalysis?.ConsoleErrors.Count > 0 || e.PageAnalysis?.FailedRequests.Count > 0)
                ? true
                : existingCandidate.HasRuntimeErrors,
            InteractionSummary = e.PageAnalysis is { } pa && (pa.ConsoleErrors.Count > 0 || pa.FailedRequests.Count > 0)
                ? $"{pa.ConsoleErrors.Count} console error(s), {pa.FailedRequests.Count} failed request(s)"
                : existingCandidate.InteractionSummary,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
        }
    }

    public void RecordVideoReady(CandidateVideoReadyEvent e)
    {
        var key = (e.RunId, e.TaskId);
        if (e.Failed || (string.IsNullOrEmpty(e.VideoPath) && string.IsNullOrEmpty(e.AnimatedGifPath))) return;

        // Try active first
        bool handledActive = false;
        TaskSnapshot? activeSnapshot = null;
        lock (_activeLock)
        {
        if (_active.TryGetValue(key, out var task))
        {
            if (!task.Candidates.TryGetValue(e.StrategyId, out var existing)) return;
            var updated = existing with
            {
                VideoPath = e.VideoPath ?? existing.VideoPath,
                AnimatedGifPath = e.AnimatedGifPath ?? existing.AnimatedGifPath,
            };
            activeSnapshot = _active.AddOrUpdate(
                key,
                _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
                (_, ex) => ex with { Candidates = ex.Candidates.SetItem(e.StrategyId, updated) });
            handledActive = true;
        }
        }
        if (handledActive)
        {
            if (activeSnapshot is not null) OnChange?.Invoke(activeSnapshot);
            return;
        }
        // Fall back to recent (fixes dropped late events)
        {
            lock (_recentLock)
            {
                var node = _recent.First;
                while (node is not null)
                {
                    if (node.Value.RunId == key.RunId && node.Value.TaskId == key.TaskId)
                    {
                        if (!node.Value.Candidates.TryGetValue(e.StrategyId, out var existing)) return;
                        var updated = existing with
                        {
                            VideoPath = e.VideoPath ?? existing.VideoPath,
                            AnimatedGifPath = e.AnimatedGifPath ?? existing.AnimatedGifPath,
                        };
                        node.Value = node.Value with { Candidates = node.Value.Candidates.SetItem(e.StrategyId, updated) };
                        OnChange?.Invoke(node.Value);
                        return;
                    }
                    node = node.Next;
                }
            }
        }
    }

    /// <summary>
    /// Merges a single <see cref="MediaCaptureProgressEvent"/> into the candidate's
    /// <see cref="CandidateSnapshot.MediaCaptureProgress"/>. First event for a candidate
    /// initializes all 12 steps as Pending; subsequent events update one step at a time.
    /// </summary>
    public void RecordMediaCaptureProgress(MediaCaptureProgressEvent e)
    {
        var key = (e.RunId, e.TaskId);
        bool handledActive = false;
        TaskSnapshot? activeSnapshot = null;
        lock (_activeLock)
        {
        if (_active.TryGetValue(key, out var task))
        {
            if (!task.Candidates.TryGetValue(e.StrategyId, out var existing)) return;
            var updated = existing with { MediaCaptureProgress = MergeMediaCaptureProgress(existing.MediaCaptureProgress, e) };
            activeSnapshot = _active.AddOrUpdate(
                key,
                _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
                (_, ex) => ex with { Candidates = ex.Candidates.SetItem(e.StrategyId, updated) });
            handledActive = true;
        }
        }
        if (handledActive)
        {
            if (activeSnapshot is not null) OnChange?.Invoke(activeSnapshot);
            return;
        }

        // Fall back to recent (covers late events after the task was archived)
        lock (_recentLock)
        {
            var node = _recent.First;
            while (node is not null)
            {
                if (node.Value.RunId == key.RunId && node.Value.TaskId == key.TaskId)
                {
                    if (!node.Value.Candidates.TryGetValue(e.StrategyId, out var existing)) return;
                    var updated = existing with { MediaCaptureProgress = MergeMediaCaptureProgress(existing.MediaCaptureProgress, e) };
                    node.Value = node.Value with { Candidates = node.Value.Candidates.SetItem(e.StrategyId, updated) };
                    OnChange?.Invoke(node.Value);
                    return;
                }
                node = node.Next;
            }
        }
    }

    private static MediaCapture.MediaCaptureProgressSnapshot MergeMediaCaptureProgress(
        MediaCapture.MediaCaptureProgressSnapshot? existing,
        MediaCaptureProgressEvent e)
    {
        var startedAt = existing?.StartedAt ?? e.At;

        var stepsBuilder = existing?.Steps.ToBuilder()
            ?? Enum.GetValues<MediaCapture.MediaCaptureStepId>()
                .OrderBy(id => id)
                .Select(id => new MediaCapture.MediaCaptureStep(id, MediaCapture.MediaCaptureStepStatus.Pending))
                .ToImmutableList()
                .ToBuilder();

        for (int i = 0; i < stepsBuilder.Count; i++)
        {
            if (stepsBuilder[i].Id != e.StepId) continue;
            var prev = stepsBuilder[i];
            var stepStartedAt = e.Status == MediaCapture.MediaCaptureStepStatus.Running ? e.At : prev.StartedAt;
            var stepCompletedAt = e.Status is MediaCapture.MediaCaptureStepStatus.Completed
                or MediaCapture.MediaCaptureStepStatus.Failed
                or MediaCapture.MediaCaptureStepStatus.Skipped
                ? e.At : prev.CompletedAt;
            stepsBuilder[i] = new MediaCapture.MediaCaptureStep(
                e.StepId, e.Status, e.Detail ?? prev.Detail,
                stepStartedAt, stepCompletedAt, e.ElapsedMs ?? prev.ElapsedMs);
            break;
        }

        // CurrentStepId tracks the running step (cleared when nothing is running)
        var currentStepId = e.Status == MediaCapture.MediaCaptureStepStatus.Running
            ? e.StepId
            : (existing?.CurrentStepId == e.StepId ? null : existing?.CurrentStepId);

        return new MediaCapture.MediaCaptureProgressSnapshot
        {
            Steps = stepsBuilder.ToImmutable(),
            CurrentStepId = currentStepId,
            StartedAt = startedAt,
            TotalElapsedMs = (e.At - startedAt).TotalMilliseconds,
        };
    }

    public void RecordScored(CandidateScoredEvent e)
    {
        lock (_activeLock)
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
            ScreenshotBase64 = e.ScreenshotBase64 ?? existingCandidate.ScreenshotBase64,
            // Carry preview source/asset list through scored event too, so the badge
            // stays correct when the dashboard sees scored before evaluated (race).
            PreviewSource = e.PreviewSource ?? existingCandidate.PreviewSource,
            IncludedAssetPaths = e.IncludedAssetPaths ?? existingCandidate.IncludedAssetPaths,
            SecondaryPreviewBase64 = e.SecondaryPreviewBase64 ?? existingCandidate.SecondaryPreviewBase64,
            SecondaryAssetPaths = e.SecondaryAssetPaths ?? existingCandidate.SecondaryAssetPaths,
            SecondaryPreviewSource = e.SecondaryPreviewSource ?? existingCandidate.SecondaryPreviewSource,
            JudgeFeedback = e.Feedback ?? existingCandidate.JudgeFeedback,
            AcFeedback = e.AcFeedback ?? existingCandidate.AcFeedback,
            DesignFeedback = e.DesignFeedback ?? existingCandidate.DesignFeedback,
            ReadabilityFeedback = e.ReadabilityFeedback ?? existingCandidate.ReadabilityFeedback,
            VisualsFeedback = e.VisualsFeedback ?? existingCandidate.VisualsFeedback,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
        }
    }

    public void RecordInitialScored(CandidateInitialScoredEvent e)
    {
        lock (_activeLock)
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
            AcFeedback = e.AcFeedback,
            DesignFeedback = e.DesignFeedback,
            ReadabilityFeedback = e.ReadabilityFeedback,
            VisualsFeedback = e.VisualsFeedback,
            InitialAcFeedback = e.AcFeedback,
            InitialDesignFeedback = e.DesignFeedback,
            InitialReadabilityFeedback = e.ReadabilityFeedback,
            InitialVisualsFeedback = e.VisualsFeedback,
            InitialScreenshotBase64 = e.ScreenshotBase64 ?? existingCandidate.ScreenshotBase64,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, existing) => existing with { Candidates = existing.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
        }
    }

    public void RecordRevisionStarted(CandidateRevisionStartedEvent e)
    {
        lock (_activeLock)
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
    }

    public void RecordRevisionCompleted(CandidateRevisionCompletedEvent e)
    {
        lock (_activeLock)
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
    }

    public void RecordProgress(EvaluationProgressEvent e)
    {
        lock (_activeLock)
        {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        // "no-winner" phase means all candidates failed — archive the task
        // so it moves from active to recent and stops showing as "RUNNING"
        if (string.Equals(e.Phase, "no-winner", StringComparison.Ordinal))
        {
            ArchiveTaskIfActive(e.RunId, e.TaskId, "no-winner: " + e.Detail);
            return;
        }

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { CurrentPhase = e.Phase, ProgressDetail = e.Detail },
            (_, existing) => existing with { CurrentPhase = e.Phase, ProgressDetail = e.Detail });
        OnChange?.Invoke(snapshot);
        }
    }

    public void RecordRetryStarted(CandidateRetryStartedEvent e)
    {
        lock (_activeLock)
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
    }

    public void RecordRetryCompleted(CandidateRetryCompletedEvent e)
    {
        lock (_activeLock)
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
    }

    public void RecordCancelled(OrchestrationCancelledEvent e)
    {
        lock (_activeLock)
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
    }

    public void RecordDetail(CandidateDetailEvent e)
    {
        lock (_activeLock)
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
    }

    private const int MaxActiveActivityEntries = 200;
    private const int MaxArchivedActivityEntries = 50;

    public void RecordActivity(CandidateActivityEvent e)
    {
        lock (_activeLock)
        {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;

        if (!task.Candidates.TryGetValue(e.StrategyId, out var existing)) return;

        var log = existing.ActivityLog.Count >= MaxActiveActivityEntries
            ? existing.ActivityLog.RemoveRange(0, existing.ActivityLog.Count - MaxActiveActivityEntries + 1).Add(e.Activity)
            : existing.ActivityLog.Add(e.Activity);

        var updated = existing with { ActivityLog = log, LastActivityAt = DateTimeOffset.UtcNow };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, ex) => ex with { Candidates = ex.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
        }
    }

    public void RecordWinner(WinnerSelectedEvent e)
    {
        lock (_activeLock)
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
    }

    /// <summary>
    /// Record that a PR has been linked to this strategy task. Updates active and recent
    /// snapshots so the dashboard can surface a link to the resulting PR. Idempotent.
    /// </summary>
    public void RecordTaskPrLinked(TaskPrLinkedEvent e)
    {
        var key = (e.RunId, e.TaskId);

        // Active path: in-flight task gets its PR fields filled in
        TaskSnapshot? activeUpdated = null;
        lock (_activeLock)
        {
        if (_active.TryGetValue(key, out var active))
        {
            var updated = active with
            {
                PrNumber = e.PrNumber,
                PrUrl = e.PrUrl ?? active.PrUrl,
                PrTitle = e.PrTitle ?? active.PrTitle,
            };
            if (_active.TryUpdate(key, updated, active))
            {
                activeUpdated = updated;
            }
        }
        }
        if (activeUpdated is not null)
        {
            OnChange?.Invoke(activeUpdated);
            PersistToSqlite(activeUpdated);
            return;
        }

        // Recent path: a completed task gets back-filled with its PR (e.g. integration task
        // creates the PR after the strategies completed). Find by (RunId, TaskId) and replace.
        TaskSnapshot? updatedRecent = null;
        lock (_recentLock)
        {
            var node = _recent.First;
            while (node is not null)
            {
                if (string.Equals(node.Value.RunId, e.RunId, StringComparison.Ordinal) &&
                    string.Equals(node.Value.TaskId, e.TaskId, StringComparison.Ordinal))
                {
                    updatedRecent = node.Value with
                    {
                        PrNumber = e.PrNumber,
                        PrUrl = e.PrUrl ?? node.Value.PrUrl,
                        PrTitle = e.PrTitle ?? node.Value.PrTitle,
                    };
                    node.Value = updatedRecent;
                    break;
                }
                node = node.Next;
            }
        }
        if (updatedRecent is not null)
        {
            OnChange?.Invoke(updatedRecent);
            PersistToSqlite(updatedRecent);
            return;
        }

        // No matching snapshot yet — the link arrived BEFORE the first CandidateStarted.
        // Stash it; RecordStarted will drain and apply when the snapshot first appears.
        _pendingPrLinks[key] = e;
    }

    public void RecordAnalyzerUpdate(CandidateAnalyzerUpdateEvent e)
    {
        lock (_activeLock)
        {
        var key = (e.RunId, e.TaskId);
        if (!_active.TryGetValue(key, out var task)) return;
        if (!task.Candidates.TryGetValue(e.StrategyId, out var existing)) return;

        var updated = existing with
        {
            ToolCallCount = e.ToolCallCount,
            BuildPassed = e.BuildPassed,
            TestsPassed = e.TestsPassed,
            BuildFailCount = e.BuildFailCount,
            AnalyzerVerdict = e.AnalyzerVerdict,
            NudgeSent = e.NudgeSent,
        };

        var snapshot = _active.AddOrUpdate(
            key,
            _ => task with { Candidates = task.Candidates.SetItem(e.StrategyId, updated) },
            (_, ex) => ex with { Candidates = ex.Candidates.SetItem(e.StrategyId, updated) });
        OnChange?.Invoke(snapshot);
        }
    }

    /// <summary>
    /// Record the OS process ID for a running candidate. Used to detect and kill stuck processes.
    /// </summary>
    public void RecordProcessStarted(string runId, string taskId, string strategyId, int processId)
    {
        lock (_activeLock)
        {
            var key = (runId, taskId);
            if (!_active.TryGetValue(key, out var task)) return;
            if (!task.Candidates.TryGetValue(strategyId, out var candidate)) return;

            var updated = candidate with
            {
                ProcessId = processId,
                ProcessStartedAt = DateTimeOffset.UtcNow,
            };

            var snapshot = _active.AddOrUpdate(
                key,
                _ => task with { Candidates = task.Candidates.SetItem(strategyId, updated) },
                (_, ex) => ex with { Candidates = ex.Candidates.SetItem(strategyId, updated) });
            OnChange?.Invoke(snapshot);
        }
    }

    /// <summary>
    /// Returns candidates that have been running longer than the specified threshold
    /// and still have an active process ID, with no recent activity. Used by FlowMonitor
    /// to detect stuck processes.
    /// </summary>
    public IReadOnlyList<(string RunId, string TaskId, string StrategyId, int ProcessId, TimeSpan Elapsed)> GetStuckCandidates(TimeSpan threshold)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<(string, string, string, int, TimeSpan)>();

        foreach (var (key, task) in _active)
        {
            foreach (var (strategyId, candidate) in task.Candidates)
            {
                if (candidate.State != CandidateState.Running) continue;
                if (candidate.ProcessId is null || candidate.ProcessStartedAt is null) continue;

                // Use LastActivityAt if available (more accurate than ProcessStartedAt alone —
                // a candidate that produced output 2 min ago isn't stuck even if it started 30 min ago)
                var referenceTime = candidate.LastActivityAt ?? candidate.ProcessStartedAt.Value;
                var elapsed = now - referenceTime;
                if (elapsed >= threshold)
                {
                    result.Add((key.RunId, key.TaskId, strategyId, candidate.ProcessId.Value, elapsed));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Get the reset count for a specific candidate. Used by FlowMonitor's escalation
    /// ladder to decide which rung to apply (rung 1 = reset, rung 2 = no-wrapper, rung 3 = cancel).
    /// </summary>
    public int GetResetCount(string taskId, string strategyId)
    {
        foreach (var (key, task) in _active)
        {
            if (!key.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase)) continue;
            if (task.Candidates.TryGetValue(strategyId, out var candidate))
                return candidate.ResetCount;
        }
        return 0;
    }

    /// <summary>
    /// If an orchestration ends without a winner (all candidates failed), the
    /// orchestrator can call this to archive the task. Idempotent if the task is
    /// already archived.
    /// </summary>
    public void ArchiveTaskIfActive(string runId, string taskId, string? reason = null)
    {
        lock (_activeLock)
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
            // Deduplicate: if a task with the same RunId+TaskId already exists
            // (e.g. from a retry or re-evaluation), replace it with the newer snapshot.
            var existing = _recent.FirstOrDefault(t =>
                string.Equals(t.RunId, trimmed.RunId, StringComparison.Ordinal) &&
                string.Equals(t.TaskId, trimmed.TaskId, StringComparison.Ordinal));
            if (existing is not null)
                _recent.Remove(existing);

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
                TaskTitle = snapshot.TaskTitle,
                StartedAt = snapshot.StartedAt,
                CompletedAt = snapshot.CompletedAt,
                WinnerStrategyId = snapshot.WinnerStrategyId,
                TieBreakReason = snapshot.TieBreakReason,
                EvaluationElapsedSec = snapshot.EvaluationElapsedSec,
                PrNumber = snapshot.PrNumber,
                PrUrl = snapshot.PrUrl,
                PrTitle = snapshot.PrTitle,
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
                    VisualsScore = c.VisualsScore,
                    Survived = c.Survived,
                    JudgeSkippedReason = c.JudgeSkippedReason,
                    ExecutionSummaryJson = c.ExecutionSummary is not null
                        ? JsonSerializer.Serialize(c.ExecutionSummary, _jsonOpts) : null,
                    ScreenshotBase64 = c.ScreenshotBase64,
                    VideoPath = c.VideoPath,
                    AnimatedGifPath = c.AnimatedGifPath,
                    ScreenshotPathsJson = c.ScreenshotPaths is { Count: > 0 }
                        ? JsonSerializer.Serialize(c.ScreenshotPaths, _jsonOpts) : null,
                    CaptureMetricsJson = c.CaptureMetrics is not null
                        ? JsonSerializer.Serialize(c.CaptureMetrics, _jsonOpts) : null,
                    PageAnalysisJson = c.PageAnalysis is not null
                        ? JsonSerializer.Serialize(c.PageAnalysis, _jsonOpts) : null,
                    AppBaseUrl = c.AppBaseUrl,
                    InitialAcScore = c.InitialAcScore,
                    InitialDesignScore = c.InitialDesignScore,
                    InitialReadabilityScore = c.InitialReadabilityScore,
                    InitialVisualsScore = c.InitialVisualsScore,
                    JudgeFeedback = c.JudgeFeedback,
                    AcFeedback = c.AcFeedback,
                    DesignFeedback = c.DesignFeedback,
                    ReadabilityFeedback = c.ReadabilityFeedback,
                    VisualsFeedback = c.VisualsFeedback,
                    InitialAcFeedback = c.InitialAcFeedback,
                    InitialDesignFeedback = c.InitialDesignFeedback,
                    InitialReadabilityFeedback = c.InitialReadabilityFeedback,
                    InitialVisualsFeedback = c.InitialVisualsFeedback,
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CandidateStateStore] PersistToSqlite failed for {snapshot.RunId}/{snapshot.TaskId}: {ex.GetType().Name}: {ex.Message}");
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
                        c =>
                        {
                            var state = Enum.TryParse<CandidateState>(c.State, out var s) ? s : CandidateState.Completed;

                            // Orphan recovery: candidates still Running/Retrying/Revising after a restart
                            // were killed mid-execution. Mark them as failed so they don't show
                            // as perpetually "RUNNING" or "REVISING" on the dashboard.
                            var failureReason = c.FailureReason;
                            var succeeded = c.Succeeded;
                            if (state is CandidateState.Running or CandidateState.Retrying or CandidateState.Revising)
                            {
                                state = CandidateState.Completed;
                                failureReason ??= "interrupted: runner restarted during execution";
                                succeeded ??= false;
                            }

                            return new CandidateSnapshot
                            {
                                StrategyId = c.StrategyId,
                                State = state,
                                StartedAt = c.StartedAt,
                                CompletedAt = c.CompletedAt,
                                ElapsedSec = c.ElapsedSec,
                                Succeeded = succeeded,
                                FailureReason = failureReason,
                                TokensUsed = c.TokensUsed,
                                AcScore = c.AcScore,
                                DesignScore = c.DesignScore,
                                ReadabilityScore = c.ReadabilityScore,
                                VisualsScore = c.VisualsScore,
                                Survived = c.Survived,
                                JudgeSkippedReason = c.JudgeSkippedReason,
                                ExecutionSummary = c.ExecutionSummaryJson is not null
                                    ? JsonSerializer.Deserialize<CandidateExecutionSummary>(c.ExecutionSummaryJson, _jsonOpts) : null,
                                ScreenshotBase64 = c.ScreenshotBase64,
                                VideoPath = c.VideoPath,
                                AnimatedGifPath = c.AnimatedGifPath,
                                ScreenshotPaths = c.ScreenshotPathsJson is not null
                                    ? JsonSerializer.Deserialize<List<string>>(c.ScreenshotPathsJson, _jsonOpts) : null,
                                CaptureMetrics = c.CaptureMetricsJson is not null
                                    ? JsonSerializer.Deserialize<ScreenshotCaptureSummary>(c.CaptureMetricsJson, _jsonOpts) : null,
                                PageAnalysis = c.PageAnalysisJson is not null
                                    ? JsonSerializer.Deserialize<PageAnalysis>(c.PageAnalysisJson, _jsonOpts) : null,
                                AppBaseUrl = c.AppBaseUrl,
                                InitialAcScore = c.InitialAcScore,
                                InitialDesignScore = c.InitialDesignScore,
                                InitialReadabilityScore = c.InitialReadabilityScore,
                                InitialVisualsScore = c.InitialVisualsScore,
                                JudgeFeedback = c.JudgeFeedback,
                                AcFeedback = c.AcFeedback,
                                DesignFeedback = c.DesignFeedback,
                                ReadabilityFeedback = c.ReadabilityFeedback,
                                VisualsFeedback = c.VisualsFeedback,
                                InitialAcFeedback = c.InitialAcFeedback,
                                InitialDesignFeedback = c.InitialDesignFeedback,
                                InitialReadabilityFeedback = c.InitialReadabilityFeedback,
                                InitialVisualsFeedback = c.InitialVisualsFeedback,
                                InitialScreenshotBase64 = c.InitialScreenshotBase64,
                                RevisionElapsedSec = c.RevisionElapsedSec,
                                RevisionSkippedReason = c.RevisionSkippedReason,
                                ActivityLog = c.ActivityLog.Count > 0
                                    ? c.ActivityLog.Select(a => new ActivityEntry(
                                        a.Timestamp, a.Category, a.Message, null)).ToImmutableList()
                                    : ImmutableList<ActivityEntry>.Empty,
                            };
                        });

                    var snapshot = new TaskSnapshot
                    {
                        RunId = task.RunId,
                        TaskId = task.TaskId,
                        TaskTitle = task.TaskTitle,
                        StartedAt = task.StartedAt,
                        // Ensure orphaned tasks without CompletedAt get a timestamp
                        CompletedAt = task.CompletedAt ?? task.StartedAt,
                        Candidates = candidates,
                        WinnerStrategyId = task.WinnerStrategyId,
                        TieBreakReason = task.TieBreakReason ?? (task.WinnerStrategyId is null ? "interrupted: runner restarted" : null),
                        EvaluationElapsedSec = task.EvaluationElapsedSec,
                        PrNumber = task.PrNumber,
                        PrUrl = task.PrUrl,
                        PrTitle = task.PrTitle,
                    };
                    _recent.AddLast(snapshot);
                }
            }
        }
        catch (Exception ex)
        {
            // Log hydration failures so they're visible in runner output.
            // A bare catch previously hid the root cause of "Recent Completed (last 0)".
            Console.Error.WriteLine($"[CandidateStateStore] HydrateFromSqlite failed: {ex.GetType().Name}: {ex.Message}");
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
    /// <summary>Candidate cancelled by user from the dashboard.</summary>
    Cancelled,
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
    /// <summary>Paths to all screenshots captured during multi-page interaction (relative to workspace root). Null if capture skipped.</summary>
    public IReadOnlyList<string>? ScreenshotPaths { get; init; }
    /// <summary>Path to trimmed interaction video (relative to workspace root). Null if video capture skipped.</summary>
    public string? VideoPath { get; init; }
    /// <summary>Path to animated GIF generated from the video. Null if FFmpeg unavailable or video not captured.</summary>
    public string? AnimatedGifPath { get; init; }
    /// <summary>
    /// Which preview producer supplied <see cref="ScreenshotBase64"/>. Drives badge/tab
    /// selection on the dashboard (📷 Playwright, 🎨 ImageAssets, 📐 Diagrams, or
    /// the no-visual-content placeholder). Defaults to <see cref="CandidatePreviewSource.PlaywrightScreenshot"/>
    /// for backward compatibility with snapshots created before producers existed.
    /// </summary>
    public CandidatePreviewSource PreviewSource { get; init; } = CandidatePreviewSource.PlaywrightScreenshot;
    /// <summary>
    /// Source asset paths included in a non-Playwright preview (image-asset contact sheet
    /// or rendered-diagram set). Null when the producer didn't surface a per-asset list
    /// (Playwright + NoVisualContent paths). Paths are relative to the candidate worktree
    /// when produced inline, or absolute when copied to a durable artifact directory.
    /// </summary>
    public IReadOnlyList<string>? IncludedAssetPaths { get; init; }

    // ── Secondary preview (mixed-content PRs: code + committed assets) ──
    /// <summary>
    /// Base64-encoded PNG of a SECONDARY preview (e.g. an image-asset contact sheet)
    /// when the candidate worktree is "mixed-content": it has BOTH a runnable app
    /// (primary Playwright capture in <see cref="ScreenshotBase64"/>) AND committed art
    /// assets. Rendered as a small "Assets used" strip below the primary preview.
    /// Null in the common single-source case.
    /// </summary>
    public string? SecondaryPreviewBase64 { get; init; }
    /// <summary>
    /// Source paths of the assets included in <see cref="SecondaryPreviewBase64"/>.
    /// Used by the dashboard to render clickable per-asset thumbnails (each opens in
    /// the lightbox). Null when no secondary preview applied.
    /// </summary>
    public IReadOnlyList<string>? SecondaryAssetPaths { get; init; }
    /// <summary>
    /// <see cref="CandidatePreviewSource"/> of the secondary preview (almost always
    /// <see cref="CandidatePreviewSource.ImageAssets"/> today, but kept extensible).
    /// Null when no secondary preview applied.
    /// </summary>
    public CandidatePreviewSource? SecondaryPreviewSource { get; init; }
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
    /// <summary>Per-dimension judge feedback for Acceptance Criteria score.</summary>
    public string? AcFeedback { get; init; }
    /// <summary>Per-dimension judge feedback for Design score.</summary>
    public string? DesignFeedback { get; init; }
    /// <summary>Per-dimension judge feedback for Readability score.</summary>
    public string? ReadabilityFeedback { get; init; }
    /// <summary>Per-dimension judge feedback for Visuals score.</summary>
    public string? VisualsFeedback { get; init; }
    /// <summary>Initial round per-dimension feedback: Acceptance Criteria.</summary>
    public string? InitialAcFeedback { get; init; }
    /// <summary>Initial round per-dimension feedback: Design.</summary>
    public string? InitialDesignFeedback { get; init; }
    /// <summary>Initial round per-dimension feedback: Readability.</summary>
    public string? InitialReadabilityFeedback { get; init; }
    /// <summary>Initial round per-dimension feedback: Visuals.</summary>
    public string? InitialVisualsFeedback { get; init; }
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
    /// <summary>Media capture pipeline progress (12 steps from Playwright readiness through artifact storage). Null until the first MediaCaptureProgressEvent is received for this candidate.</summary>
    public MediaCapture.MediaCaptureProgressSnapshot? MediaCaptureProgress { get; init; }

    /// <summary>
    /// Dual-capture metrics: per-source artifact counts, tool calls, pages discovered, tested URLs.
    /// Null until capture completes. Populated from the parallel MCP + Direct capture pipeline.
    /// </summary>
    public ScreenshotCaptureSummary? CaptureMetrics { get; init; }

    /// <summary>
    /// CDP-derived page analysis: UI vs API detection, console errors, failed requests.
    /// Collected during C# Playwright capture via Chrome DevTools Protocol.
    /// </summary>
    public PageAnalysis? PageAnalysis { get; init; }

    /// <summary>
    /// The base URL the app was started on (e.g., "http://localhost:5142").
    /// Null when the app didn't start or capture was skipped.
    /// </summary>
    public string? AppBaseUrl { get; init; }

    /// <summary>Aggregated runtime behavior summary for dashboard display.</summary>
    public string? InteractionSummary { get; init; }

    /// <summary>Whether any runtime errors were detected (console errors, failed requests, test failures).</summary>
    public bool? HasRuntimeErrors { get; init; }

    // ── Live analyzer state (updated during candidate execution) ──
    /// <summary>Tool call count from the AgenticStreamAnalyzer. Null until first analyzer update.</summary>
    public int? ToolCallCount { get; init; }
    /// <summary>Whether the analyzer detected a successful build.</summary>
    public bool? BuildPassed { get; init; }
    /// <summary>Whether the analyzer detected tests passing.</summary>
    public bool? TestsPassed { get; init; }
    /// <summary>Number of build failures detected by the analyzer.</summary>
    public int? BuildFailCount { get; init; }
    /// <summary>Last AI or deterministic verdict from the analyzer (e.g., "error-loop: same build error 3x").</summary>
    public string? AnalyzerVerdict { get; init; }
    /// <summary>Whether a tests-passed nudge was sent to the CLI session.</summary>
    public bool? NudgeSent { get; init; }
    /// <summary>OS process ID of the CLI process running this candidate. Null until process starts.</summary>
    public int? ProcessId { get; init; }
    /// <summary>When the candidate's CLI process started. Used with ProcessId to detect stuck candidates.</summary>
    public DateTimeOffset? ProcessStartedAt { get; init; }
    /// <summary>Last time any activity (stdout line, tool call, reasoning) was observed from this candidate. Used by FlowMonitor to detect stuck candidates with no output.</summary>
    public DateTimeOffset? LastActivityAt { get; init; }
    /// <summary>Number of times this candidate has been reset (killed + retried). Used by FlowMonitor's escalation ladder: rung 1=retry same, rung 2=retry no wrapper, rung 3=cancel.</summary>
    public int ResetCount { get; init; } = 0;
    /// <summary>Reason for the most recent reset (e.g., "stuck-no-output", "manual-dashboard").</summary>
    public string? LastResetReason { get; init; }
    /// <summary>When the most recent reset occurred.</summary>
    public DateTimeOffset? LastResetAt { get; init; }
}

public sealed record TaskSnapshot
{
    public required string RunId { get; init; }
    public required string TaskId { get; init; }
    /// <summary>Human-readable task title from the work item (e.g. "Wire sprite previews into bestiary panel"). Null for older records that predate this field.</summary>
    public string? TaskTitle { get; init; }
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

    /// <summary>
    /// PR number backing this strategy task (created by the engineer agent before strategies run,
    /// or after winner-apply for integration tasks). Null until <see cref="TaskPrLinkedEvent"/> arrives.
    /// </summary>
    public int? PrNumber { get; init; }

    /// <summary>Direct URL to the PR (e.g. https://github.com/owner/repo/pull/42).</summary>
    public string? PrUrl { get; init; }

    /// <summary>Title of the PR for display in the link.</summary>
    public string? PrTitle { get; init; }

    /// <summary>
    /// Wave label from the engineering plan (e.g. "W0", "W1", "W2"). Null when the task wasn't
    /// part of a wave-scheduled plan (legacy tasks, ad-hoc reruns, or pre-Wave-plumbing snapshots).
    /// The Frameworks dashboard prefers this value over its TaskId-based heuristic when present.
    /// </summary>
    public string? Wave { get; init; }
}
