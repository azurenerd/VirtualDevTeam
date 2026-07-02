using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Frameworks;
using VirtualDevTeam.Core.Strategies.Contracts;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Coordinates the multi-strategy run for a single task: creates per-candidate
/// worktrees, invokes each enabled strategy in parallel under a concurrency cap,
/// extracts patches, runs the evaluator, emits lifecycle events, and writes the
/// experiment record. Returns a descriptive result but does NOT apply the winner —
/// that's the SE agent's responsibility (so it can do a head-change check first).
/// </summary>
public class StrategyOrchestrator
{
    private readonly ILogger<StrategyOrchestrator> _logger;
    private readonly GitWorktreeManager _worktree;
    private readonly CandidateEvaluator _evaluator;
    private readonly ExperimentTracker _tracker;
    private readonly StrategyConcurrencyGate _gate;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly IReadOnlyDictionary<string, ICodeGenerationStrategy> _strategies;
    private readonly IReadOnlyDictionary<string, IAgenticFrameworkAdapter> _externalAdapters;
    private readonly IStrategyEventSink _events;
    private readonly StrategySamplingPolicy? _sampling;
    private readonly RunBudgetTracker? _budget;
    private readonly AgentUsageTracker? _usage;
    private readonly RevisionFeedbackGenerator? _revisionFeedback;
    private readonly IOrchestrationCancellationService? _cancellation;
    private readonly StrategyRecoveryStore? _recovery;
    private readonly CandidateStateStore? _candidateStateStore;
    private readonly IChatCompletionRunner? _llmRunner;

    public StrategyOrchestrator(
        ILogger<StrategyOrchestrator> logger,
        GitWorktreeManager worktree,
        CandidateEvaluator evaluator,
        ExperimentTracker tracker,
        StrategyConcurrencyGate gate,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        IEnumerable<ICodeGenerationStrategy> strategies,
        IStrategyEventSink? events = null,
        StrategySamplingPolicy? sampling = null,
        RunBudgetTracker? budget = null,
        AgentUsageTracker? usage = null,
        IEnumerable<IAgenticFrameworkAdapter>? adapters = null,
        RevisionFeedbackGenerator? revisionFeedback = null,
        IOrchestrationCancellationService? cancellation = null,
        StrategyRecoveryStore? recovery = null,
        IChatCompletionRunner? llmRunner = null,
        CandidateStateStore? candidateStateStore = null)
    {
        _logger = logger;
        _worktree = worktree;
        _evaluator = evaluator;
        _tracker = tracker;
        _gate = gate;
        _cfg = cfg;
        _strategies = strategies.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        // External adapters: only keep adapters whose Id does NOT already exist as a
        // built-in ICodeGenerationStrategy. This avoids double-executing built-in
        // strategies through their wrapper adapters.
        _externalAdapters = (adapters ?? Enumerable.Empty<IAgenticFrameworkAdapter>())
            .Where(a => !_strategies.ContainsKey(a.Id))
            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        _events = events ?? NullStrategyEventSink.Instance;
        _sampling = sampling;
        _budget = budget;
        _usage = usage;
        _revisionFeedback = revisionFeedback;
        _cancellation = cancellation;
        _recovery = recovery;
        _candidateStateStore = candidateStateStore;
        _llmRunner = llmRunner;
    }

    /// <summary>All known framework/strategy IDs (built-in + external adapters).</summary>
    public IReadOnlyCollection<string> AllKnownIds =>
        _strategies.Keys.Concat(_externalAdapters.Keys).ToList().AsReadOnly();

    /// <summary>
    /// Emit a TaskPrLinked event so the Frameworks dashboard can surface a clickable
    /// link from this strategy task back to its backing PR. Idempotent — safe to call
    /// multiple times (e.g. before strategies start and again after a rename).
    /// </summary>
    public Task EmitTaskPrLinkedAsync(string runId, string taskId, int prNumber, string? prUrl, string? prTitle, CancellationToken ct)
        => _events.EmitAsync(StrategyEvents.TaskPrLinked, new TaskPrLinkedEvent(runId, taskId, prNumber, prUrl, prTitle), ct);

    /// <summary>Run all enabled strategies for a task and evaluate. Does not apply the winner.</summary>
    public async Task<OrchestrationOutcome> RunCandidatesAsync(TaskContext task, CancellationToken ct)
    {
        var cfg = _cfg.CurrentValue;

        // ── Recovery: check for orphaned checkpoint from a prior runner session ──
        if (cfg.RecoverOrphanedCandidates && _recovery is not null)
        {
            var recovered = await TryRecoverFromCheckpointAsync(task, cfg, ct);
            if (recovered is not null)
                return recovered;
        }

        var runSw = Stopwatch.StartNew();
        // Dedupe defensively. .NET IConfiguration.Bind APPENDS list items to
        // any default List<T> initializer on the target property rather than
        // replacing it, so a config file that re-lists the default values
        // (["baseline","mcp-enhanced"]) produces a 4-item runtime list.
        // Orchestrating the same strategy twice wastes tokens AND races on the
        // worktree directory (same candidate dir name, unique-suffix fix still
        // can't fully recover from cleanup file locks). Distinct() here is the
        // surgical fix; root cause is in StrategyFrameworkConfig's default init.
        var enabled = StrategyIdNormalizer.NormalizeAll(cfg.EnabledStrategies)
            .Where(id => !string.Equals(id, "baseline", StringComparison.OrdinalIgnoreCase)) // Baseline removed from UI — never compete
            .Where(id => _strategies.ContainsKey(id) || _externalAdapters.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabled.Count == 0)
        {
            _logger.LogWarning("No enabled strategies registered; skipping orchestration for task {Task}", task.TaskId);
            return OrchestrationOutcome.Empty(task);
        }

        // Phase 5: sampling policy + budget check — may shrink the enabled set.
        // TODO(val-e2e): Once experiment-data/<runId>.ndjson contains real survival
        // data from live runs, wire AdaptiveStrategySelector here (inject it via
        // the constructor like _sampling, then call selector.Filter(enabled) before
        // handing `enabled` to the sampling policy). Until then the selector is
        // registered in DI but intentionally not invoked — we do NOT want to drop
        // strategies based on synthetic/empty history. See docs/StrategyFramework.md
        // Phase 5 status row.
        var samplingReason = "no-policy";
        if (_sampling is not null)
        {
            var decision = _sampling.Decide(task, enabled);
            if (decision.SelectedStrategies.Count == 0)
            {
                _logger.LogInformation("Sampling policy eliminated all strategies for task {Task}: {Reason}",
                    task.TaskId, decision.Reason);
                return OrchestrationOutcome.Empty(task);
            }
            if (decision.SelectedStrategies.Count != enabled.Count)
            {
                _logger.LogInformation("Sampling policy narrowed strategies for task {Task}: {Reason}. Running: {List}",
                    task.TaskId, decision.Reason, string.Join(",", decision.SelectedStrategies));
                enabled = decision.SelectedStrategies.ToList();
            }
            samplingReason = decision.Reason;
        }

        _logger.LogInformation("Orchestrating {Count} strategies for task {Task}: {Strategies}",
            enabled.Count, task.TaskId, string.Join(",", enabled));

        // Register a linked CTS so dashboard can cancel this orchestration.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancellation?.Register(task.RunId, task.TaskId, linkedCts);

        // Hoisted for emergency winner selection in the catch block.
        // Assigned after evaluation completes so the catch can salvage partial results.
        IReadOnlyList<CandidateResult>? emergencyCandidates = null;

        // Concurrent sink: records each candidate's output as soon as it completes,
        // independent of Task.WhenAll.  If cancellation interrupts WhenAll before
        // the assignment to `outputs`, the catch block can still build emergency
        // candidates from whatever finished.
        var completedOutputs = new ConcurrentDictionary<string, (StrategyExecutionResult exec, string? patch)>();

        // Hoisted so the catch block can dispose per-candidate CTS instances.
        var candidateCtsMap = new Dictionary<string, CancellationTokenSource>();

        try
        {

        // Emit progress: starting candidates
        await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
            task.RunId, task.TaskId, "candidates-running", 0, enabled.Count,
            $"Running {enabled.Count} candidates…"), linkedCts.Token);

        // Launch each strategy in its own worktree, bounded by the global gate.
        // Each candidate gets its own linked CTS so it can be cancelled individually.
        foreach (var id in enabled)
        {
            var candidateCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
            candidateCtsMap[id] = candidateCts;
            _cancellation?.RegisterCandidate(task.RunId, task.TaskId, id, candidateCts);
        }

        var runTasks = enabled.Select(async id =>
        {
            var result = await RunOneAsync(task, id, cfg, candidateCtsMap[id].Token, linkedCts.Token);
            // Record as soon as this candidate completes — survives Task.WhenAll cancellation.
            if (result.exec is not null)
                completedOutputs[id] = (result.exec, result.patch);
            return result;
        }).ToList();
        var outputs = await Task.WhenAll(runTasks);

        // Dispose per-candidate CTS instances now that all candidates have completed.
        foreach (var cts in candidateCtsMap.Values) cts.Dispose();

        // Filter out user-cancelled candidates — they should not participate in evaluation.
        var cancelledIds = outputs
            .Where(o => o.exec?.FailureReason == "cancelled-by-user")
            .Select(o => o.exec!.StrategyId)
            .ToHashSet();
        if (cancelledIds.Count > 0)
        {
            _logger.LogInformation("Filtering {Count} user-cancelled candidates from evaluation: {Ids}",
                cancelledIds.Count, string.Join(",", cancelledIds));
            outputs = outputs.Where(o => o.exec?.FailureReason != "cancelled-by-user").ToArray();
        }

        // Emit progress: candidates complete, starting evaluation
        var succeededCount = outputs.Count(o => o.exec?.Succeeded == true);
        await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
            task.RunId, task.TaskId, "gates-evaluating", enabled.Count, enabled.Count,
            $"{succeededCount}/{enabled.Count} succeeded — evaluating gates…"), ct);

        // ── Gate Retry: re-run gate-failed candidates from scratch ──
        var retryCfg = cfg.GateRetry;
        if (retryCfg.Enabled && retryCfg.MaxRetries > 0)
        {
            var retryOutputs = await RunGateRetryAsync(task, outputs, enabled, cfg, ct);
            if (retryOutputs is not null)
                outputs = retryOutputs;
        }

        // ── Checkpoint: all candidates done (incl. retries), save before evaluation ──
        if (_recovery is not null)
        {
            var checkpointCandidates = outputs
                .Where(o => o.exec is not null)
                .Select(o =>
                {
                    // Pull media capture progress from CandidateStateStore if available
                    IReadOnlyList<MediaCapture.MediaCaptureStep>? mediaSteps = null;
                    if (_candidateStateStore is not null)
                    {
                        var snapshot = _candidateStateStore.GetCandidateSnapshot(task.RunId, task.TaskId, o.exec!.StrategyId);
                        mediaSteps = snapshot?.MediaCaptureProgress?.Steps;
                    }

                    return new CandidateCheckpoint
                    {
                        StrategyId = o.exec!.StrategyId,
                        Succeeded = o.exec.Succeeded,
                        FailureReason = o.exec.FailureReason,
                        NoOpAcknowledged = o.exec.NoOpAcknowledged,
                        ElapsedSeconds = o.exec.Elapsed.TotalSeconds,
                        TokensUsed = o.exec.TokensUsed,
                        Patch = o.patch ?? "",
                        MediaCaptureSteps = mediaSteps,
                    };
                })
                .ToList();
            _recovery.SaveExecutionDone(task.TaskId, task.RunId, task.BaseSha,
                new TaskContextSnapshot
                {
                    TaskId = task.TaskId,
                    RunId = task.RunId,
                    TaskTitle = task.TaskTitle,
                    TaskDescription = task.TaskDescription,
                    BaseSha = task.BaseSha,
                    AgentRepoPath = task.AgentRepoPath,
                },
                checkpointCandidates);
        }

        // Evaluate survivors.
        var evalInput = outputs
            .Where(o => o.exec is not null)
            .Select(o => (o.exec!, o.patch))
            .ToList();

        EvaluationResult evalResult;
        var revCfg = cfg.RevisionRound;

        if (revCfg.Enabled && !revCfg.SkipInitialJudgment && evalInput.Count > 1)
        {
            await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
                task.RunId, task.TaskId, "revision-round", evalInput.Count, enabled.Count,
                "Revision round: judge scoring with feedback…"), ct);
            // ── Revision Round: gates-only → judge with feedback → revise → final judge ──
            evalResult = await RunWithRevisionAsync(task, evalInput, outputs, enabled, cfg, ct);
        }
        else
        {
            // ── Standard path: single judgment (no revision round) ──
            // Also used when SkipInitialJudgment=true (default) to avoid double-judging.
            await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
                task.RunId, task.TaskId, "judging", evalInput.Count(e => e.Item1.Succeeded), enabled.Count,
                "Evaluating gates and scoring…"), ct);
            evalResult = await _evaluator.EvaluateAsync(task, evalInput, ct);
        }

        // Snapshot candidates for emergency winner selection in catch block.
        emergencyCandidates = evalResult.Candidates;

        // Determine judge-skipped reason for surviving candidates.
        var survivorCount = evalResult.Candidates.Count(c => c.Survived);
        string? judgeSkippedReason = survivorCount switch
        {
            0 => "no-survivors",
            1 => "sole-survivor",
            _ when evalResult.Candidates.All(c => c.Score is null) => "no-judge-configured",
            _ => null, // judge ran normally
        };

        // Emit evaluated events for ALL candidates (screenshot + gate result).
        foreach (var c in evalResult.Candidates)
        {
            var screenshotBase64 = c.ScreenshotBytes is { Length: > 0 }
                ? Convert.ToBase64String(c.ScreenshotBytes)
                : null;
            await _events.EmitAsync(StrategyEvents.CandidateEvaluated, new CandidateEvaluatedEvent(
                task.RunId, task.TaskId, c.StrategyId,
                c.Survived, c.FailedGate, c.FailureDetail,
                screenshotBase64,
                c.Survived ? judgeSkippedReason : null,
                c.VideoPath, c.ScreenshotPaths, c.AnimatedGifPath,
                c.PreviewSource, c.IncludedAssetPaths,
                c.SecondaryPreviewBase64, c.SecondaryAssetPaths, c.SecondaryPreviewSource,
                c.CaptureMetrics, c.PageAnalysis), ct);
        }

        // Emit scored events only for candidates that actually went through LLM judge.
        foreach (var c in evalResult.Candidates)
        {
            if (c.Score is not null)
            {
                var screenshotBase64 = c.ScreenshotBytes is { Length: > 0 }
                    ? Convert.ToBase64String(c.ScreenshotBytes)
                    : null;
                await _events.EmitAsync(StrategyEvents.CandidateScored, new CandidateScoredEvent(
                    task.RunId, task.TaskId, c.StrategyId,
                    c.Score.AcceptanceCriteriaScore, c.Score.DesignScore, c.Score.ReadabilityScore,
                    c.Score.VisualsScore,
                    screenshotBase64,
                    c.Score.Feedback,
                    c.Score.AcFeedback, c.Score.DesignFeedback, c.Score.ReadabilityFeedback, c.Score.VisualsFeedback,
                    c.PreviewSource, c.IncludedAssetPaths,
                    c.SecondaryPreviewBase64, c.SecondaryAssetPaths, c.SecondaryPreviewSource), ct);
            }
        }

        // Emit detail events with full execution summary (file changes, logs, metrics).
        foreach (var c in evalResult.Candidates)
        {
            var summary = BuildExecutionSummary(c, judgeSkippedReason);
            await _events.EmitAsync(StrategyEvents.CandidateDetail,
                new CandidateDetailEvent(task.RunId, task.TaskId, c.StrategyId, summary), ct);
        }

        if (evalResult.Winner is not null)
        {
            await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
                task.RunId, task.TaskId, "winner-selected", enabled.Count, enabled.Count,
                $"Winner: {evalResult.Winner.StrategyId}"), ct);
            await _events.EmitAsync(StrategyEvents.WinnerSelected, new WinnerSelectedEvent(
                task.RunId, task.TaskId, evalResult.Winner.StrategyId,
                evalResult.TieBreakReason ?? "",
                evalResult.EvaluationElapsed.TotalSeconds), ct);
        }
        else
        {
            await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
                task.RunId, task.TaskId, "no-winner", enabled.Count, enabled.Count,
                "No candidate survived evaluation"), ct);
        }

        // Write experiment record.
        _tracker.Write(new ExperimentRecord
        {
            RunId = task.RunId,
            TaskId = task.TaskId,
            TaskTitle = task.TaskTitle,
            StartedAt = DateTimeOffset.UtcNow - runSw.Elapsed,
            CompletedAt = DateTimeOffset.UtcNow,
            Candidates = evalResult.Candidates.Select(c => new CandidateRecord
            {
                StrategyId = c.StrategyId,
                Succeeded = c.Survived,
                FailureReason = c.FailureDetail,
                FailedGate = c.FailedGate,
                ElapsedSec = c.Execution.Elapsed.TotalSeconds,
                PatchSizeBytes = c.PatchSizeBytes,
                TokensUsed = c.Execution.TokensUsed,
                AcceptanceCriteriaScore = c.Score?.AcceptanceCriteriaScore,
                DesignScore = c.Score?.DesignScore,
                ReadabilityScore = c.Score?.ReadabilityScore,
                VisualsScore = c.Score?.VisualsScore,
                FrameworkId = c.StrategyId,
                IsExternalFramework = _externalAdapters.ContainsKey(c.StrategyId),
            }).ToList(),
            WinnerStrategyId = evalResult.Winner?.StrategyId,
            TieBreakReason = evalResult.TieBreakReason,
            EvaluationElapsedSec = evalResult.EvaluationElapsed.TotalSeconds,
            TotalTokens = evalResult.Candidates.Sum(c => c.Execution.TokensUsed ?? 0),
        });

        // ── Checkpoint: winner selected — SE will call MarkApplied after applying the patch ──
        if (_recovery is not null && evalResult.Winner is not null)
        {
            _recovery.SaveWinnerSelected(task.TaskId, task.RunId, evalResult.Winner.StrategyId);
        }

        LogOrchestrationSummary(task.TaskId, runSw, evalResult);

        return new OrchestrationOutcome(task, evalResult);

        } // end try (cancellation registration)
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Dispose per-candidate CTS instances that may not have been disposed.
            foreach (var cts in candidateCtsMap.Values)
            {
                try { cts.Dispose(); } catch { /* best-effort */ }
            }

            // Distinguish FlowMonitor emergency promotion from user-requested cancel.
            // Emergency promotion: FlowMonitor detected stuck scoring/media and wants
            // the best available candidate selected. User cancel: operator explicitly
            // cancelled via dashboard — respect that by returning Empty.
            var isEmergencyPromotion = _cancellation?.IsEmergencyPromotion(task.RunId, task.TaskId) == true;

            // If cancellation fired before evaluation completed, emergencyCandidates is
            // null.  Build from the concurrent sink so completed candidates aren't lost.
            if (emergencyCandidates is null or { Count: 0 } && completedOutputs.Count > 0)
            {
                emergencyCandidates = completedOutputs.Values
                    .Select(o => new CandidateResult
                    {
                        StrategyId = o.exec.StrategyId,
                        // Pre-evaluation: no gate/judge ran.  Treat successful execution
                        // with a non-empty patch as "survived" for emergency purposes.
                        Survived = o.exec.Succeeded && !string.IsNullOrWhiteSpace(o.patch),
                        Patch = o.patch ?? "",
                        PatchSizeBytes = (o.patch ?? "").Length,
                        Execution = o.exec,
                    })
                    .ToList();
                _logger.LogWarning(
                    "Built {Count} emergency candidates from concurrent sink (pre-evaluation snapshot) for task {Task}",
                    emergencyCandidates.Count, task.TaskId);
            }

            if (isEmergencyPromotion && emergencyCandidates is { Count: > 0 } && _cfg.CurrentValue.Evaluator.EmergencyWinnerEnabled)
            {
                _logger.LogWarning(
                    "Orchestration cancelled by FlowMonitor emergency promotion for task {Task} — attempting emergency winner selection",
                    task.TaskId);
                try
                {
                    var emergencyResult = _evaluator.SelectEmergencyWinner(emergencyCandidates);
                    if (emergencyResult?.Winner != null)
                    {
                        if (_recovery != null)
                        {
                            _recovery.SaveWinnerSelected(task.TaskId, task.RunId, emergencyResult.Winner.StrategyId);
                            _logger.LogWarning(
                                "🚨 Emergency winner checkpointed after promotion: {StrategyId} for task {Task}",
                                emergencyResult.Winner.StrategyId, task.TaskId);
                        }

                        await _events.EmitAsync(StrategyEvents.WinnerSelected, new WinnerSelectedEvent(
                            task.RunId, task.TaskId, emergencyResult.Winner.StrategyId,
                            emergencyResult.TieBreakReason ?? "emergency-promotion",
                            (double)((emergencyResult.Winner.Score?.AcceptanceCriteriaScore ?? 0) +
                                     (emergencyResult.Winner.Score?.DesignScore ?? 0) +
                                     (emergencyResult.Winner.Score?.ReadabilityScore ?? 0))), CancellationToken.None);

                        _logger.LogWarning(
                            "🚨 Emergency winner salvaged via FlowMonitor promotion — returning {StrategyId}",
                            emergencyResult.Winner.StrategyId);
                        return new OrchestrationOutcome(task, emergencyResult);
                    }
                }
                catch (Exception emergencyEx)
                {
                    _logger.LogError(emergencyEx,
                        "Emergency winner selection failed after promotion for task {Task}", task.TaskId);
                }
            }

            // User-requested cancel or no emergency winner available
            _logger.LogInformation("Orchestration cancelled by user for task {Task}", task.TaskId);
            await _events.EmitAsync(StrategyEvents.OrchestrationCancelled, new OrchestrationCancelledEvent(
                task.RunId, task.TaskId, isEmergencyPromotion ? "emergency-promotion-no-winner" : "user-requested",
                DateTimeOffset.UtcNow), CancellationToken.None);
            return OrchestrationOutcome.Empty(task);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defense-in-depth: if any unhandled exception escapes evaluation (e.g., a
            // bug in the judge / visual scoring / gate logic), emit a terminal progress
            // event so the dashboard exits the "Evaluating gates and scoring…" state.
            // Without this, the SE agent's outer catch would swallow the error and the
            // strategy page would hang indefinitely (observed 2h 56m hang on 2026-05-22
            // when ApplyVisualScoresAsync threw "Collection was modified" — candidates
            // showed EVALUATED + "not scored" but no winner was ever selected).
            _logger.LogError(ex,
                "Strategy orchestration threw for task {Task}; emitting framework-error terminal event",
                task.TaskId);
            try
            {
                await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
                    task.RunId, task.TaskId, "framework-error", 0, 0,
                    $"Framework error: {ex.GetType().Name}: {ex.Message}"), CancellationToken.None);
            }
            catch { /* terminal emit is best-effort */ }

            // ── Layer 2: Emergency winner selection ──
            // If we have partial evaluation results (candidates were scored before the crash),
            // attempt to salvage the best candidate instead of losing all work.
            // Also build from the concurrent sink if evaluation never started.
            if (emergencyCandidates is null or { Count: 0 } && completedOutputs.Count > 0)
            {
                emergencyCandidates = completedOutputs.Values
                    .Select(o => new CandidateResult
                    {
                        StrategyId = o.exec.StrategyId,
                        Survived = o.exec.Succeeded && !string.IsNullOrWhiteSpace(o.patch),
                        Patch = o.patch ?? "",
                        PatchSizeBytes = (o.patch ?? "").Length,
                        Execution = o.exec,
                    })
                    .ToList();
                _logger.LogWarning(
                    "Built {Count} emergency candidates from concurrent sink (general exception path) for task {Task}",
                    emergencyCandidates.Count, task.TaskId);
            }

            if (emergencyCandidates is { Count: > 0 } && _cfg.CurrentValue.Evaluator.EmergencyWinnerEnabled)
            {
                try
                {
                    var emergencyResult = _evaluator.SelectEmergencyWinner(emergencyCandidates);
                    if (emergencyResult?.Winner != null)
                    {
                        // Persist the emergency winner so restart recovery can apply it
                        if (_recovery != null)
                        {
                            _recovery.SaveWinnerSelected(task.TaskId, task.RunId, emergencyResult.Winner.StrategyId);
                            _logger.LogWarning(
                                "🚨 Emergency winner checkpointed: {StrategyId} for task {Task} run {Run}",
                                emergencyResult.Winner.StrategyId, task.TaskId, task.RunId);
                        }

                        // Emit winner-selected event so the dashboard updates
                        var emergencyScore = (double)(
                            (emergencyResult.Winner.Score?.AcceptanceCriteriaScore ?? 0) +
                            (emergencyResult.Winner.Score?.DesignScore ?? 0) +
                            (emergencyResult.Winner.Score?.ReadabilityScore ?? 0));
                        await _events.EmitAsync(StrategyEvents.WinnerSelected, new WinnerSelectedEvent(
                            task.RunId, task.TaskId, emergencyResult.Winner.StrategyId,
                            emergencyResult.TieBreakReason ?? "emergency",
                            emergencyScore), CancellationToken.None);

                        _logger.LogWarning(
                            "🚨 Emergency winner salvaged after crash — returning {StrategyId} instead of throwing",
                            emergencyResult.Winner.StrategyId);
                        return new OrchestrationOutcome(task, emergencyResult);
                    }
                }
                catch (Exception emergencyEx)
                {
                    _logger.LogError(emergencyEx, "Emergency winner selection itself failed for task {Task}", task.TaskId);
                    // Fall through to original throw
                }
            }

            throw;
        }
        finally
        {
            _cancellation?.Unregister(task.RunId, task.TaskId);
        }
    }

    // ── Revision Round ──

    private async Task<EvaluationResult> RunWithRevisionAsync(
        TaskContext task,
        IReadOnlyList<(StrategyExecutionResult exec, string patch)> evalInput,
        (StrategyExecutionResult? exec, string patch)[] outputs,
        List<string> enabled,
        StrategyFrameworkConfig cfg,
        CancellationToken ct)
    {
        // Step 1: Run gates + judge scoring for all survivors
        // EvaluateAsync runs both gates AND the LLM judge when survivors.Count > 1.
        var initialEval = await _evaluator.EvaluateAsync(task, evalInput, ct);
        var survivors = initialEval.Candidates.Where(c => c.Survived).ToList();

        if (survivors.Count <= 1)
        {
            _logger.LogInformation(
                "Revision round skipped for task {Task}: only {Count} survivor(s)",
                task.TaskId, survivors.Count);
            return initialEval;
        }

        // Step 2: Build RevisionContext per survivor from Step 1 scores + generate rubber-duck feedback
        var revisionContexts = new Dictionary<string, RevisionContext>(StringComparer.Ordinal);
        foreach (var survivor in survivors)
        {
            var score = survivor.Score;
            if (score is null)
            {
                _logger.LogDebug("No judge score for {Strategy} — skipping revision", survivor.StrategyId);
                continue;
            }

            var initialScores = new Dictionary<string, int>
            {
                ["ac"] = score.AcceptanceCriteriaScore,
                ["design"] = score.DesignScore,
                ["readability"] = score.ReadabilityScore,
            };
            if (score.VisualsScore is > 0)
                initialScores["visuals"] = score.VisualsScore.Value;

            // Emit initial-scored event
            var screenshotBase64 = survivor.ScreenshotBytes is { Length: > 0 }
                ? Convert.ToBase64String(survivor.ScreenshotBytes) : null;
            await _events.EmitAsync(StrategyEvents.CandidateInitialScored,
                new CandidateInitialScoredEvent(
                    task.RunId, task.TaskId, survivor.StrategyId,
                    score.AcceptanceCriteriaScore, score.DesignScore, score.ReadabilityScore,
                    score.VisualsScore,
                    score.Feedback,
                    screenshotBase64,
                    score.AcFeedback, score.DesignFeedback, score.ReadabilityFeedback, score.VisualsFeedback), ct);

            // Generate rubber-duck feedback (different model tier for diversity)
            var rubberDuck = "";
            if (_revisionFeedback is not null)
            {
                rubberDuck = await _revisionFeedback.GenerateFeedbackAsync(
                    task.TaskTitle, task.TaskDescription,
                    survivor.StrategyId, survivor.Patch, score, ct);
            }

            revisionContexts[survivor.StrategyId] = new RevisionContext
            {
                InitialScores = initialScores,
                JudgeFeedback = score.Feedback,
                AcFeedback = score.AcFeedback,
                DesignFeedback = score.DesignFeedback,
                ReadabilityFeedback = score.ReadabilityFeedback,
                VisualsFeedback = score.VisualsFeedback,
                RubberDuckFeedback = rubberDuck,
                OriginalPatch = survivor.Patch,
            };
        }

        if (revisionContexts.Count == 0)
        {
            _logger.LogInformation("No revision contexts built — returning initial evaluation");
            return initialEval;
        }

        // Step 4: Run revision in fresh worktrees (no timeout when MaxRevisionSeconds is 0)
        var revisionTimeout = TimeoutsConfig.ToTimeSpan(cfg.RevisionRound.MaxRevisionSeconds);
        var revisionTasks = new List<Task<(StrategyExecutionResult? exec, string patch)>>();
        var revisionStrategies = new List<string>();

        foreach (var (strategyId, revCtx) in revisionContexts)
        {
            var originalOutput = outputs.FirstOrDefault(o =>
                o.exec?.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase) == true);
            if (originalOutput.exec is null) continue;

            await _events.EmitAsync(StrategyEvents.CandidateRevisionStarted,
                new CandidateRevisionStartedEvent(task.RunId, task.TaskId, strategyId, DateTimeOffset.UtcNow), ct);

            revisionStrategies.Add(strategyId);
            revisionTasks.Add(RunRevisionAsync(task, strategyId, revCtx, revisionTimeout, cfg, ct));
        }

        var revisionOutputs = await Task.WhenAll(revisionTasks);

        // Emit revision-completed events
        for (int i = 0; i < revisionStrategies.Count; i++)
        {
            var revOut = revisionOutputs[i];
            await _events.EmitAsync(StrategyEvents.CandidateRevisionCompleted,
                new CandidateRevisionCompletedEvent(
                    task.RunId, task.TaskId, revisionStrategies[i],
                    revOut.exec?.Succeeded ?? false, revOut.exec?.FailureReason,
                    revOut.exec?.Elapsed.TotalSeconds ?? 0, revOut.exec?.TokensUsed), ct);
        }

        // Step 5: Final evaluation with revised patches
        var finalInput = revisionOutputs
            .Where(o => o.exec is not null)
            .Select(o => (o.exec!, o.patch))
            .ToList();

        // If some revisions failed, include original outputs for those candidates
        foreach (var survivor in survivors)
        {
            if (!finalInput.Any(f => f.Item1.StrategyId.Equals(survivor.StrategyId, StringComparison.OrdinalIgnoreCase)))
            {
                var origOutput = evalInput.FirstOrDefault(o =>
                    o.exec.StrategyId.Equals(survivor.StrategyId, StringComparison.OrdinalIgnoreCase));
                if (origOutput.exec is not null)
                    finalInput.Add(origOutput);
            }
        }

        var finalEval = await _evaluator.EvaluateAsync(task, finalInput, ct);

        // Step 6: Best-of-two — for each candidate, keep whichever version ranks higher
        // using the same lexicographic ordering as winner selection (AC > Design > Readability > Visuals).
        // If revision made things worse, we restore the FULL initial candidate (patch + scores + screenshot).
        var bestResults = new List<CandidateResult>();
        foreach (var finalCandidate in finalEval.Candidates)
        {
            var initialCandidate = initialEval.Candidates
                .FirstOrDefault(c => c.StrategyId.Equals(finalCandidate.StrategyId, StringComparison.OrdinalIgnoreCase));

            if (initialCandidate?.Score is not null && finalCandidate.Score is not null)
            {
                // Compare using same ranking policy as winner selection: AC > Design > Readability > Visuals
                var iScore = initialCandidate.Score;
                var fScore = finalCandidate.Score;

                var initialBetter =
                    iScore.AcceptanceCriteriaScore > fScore.AcceptanceCriteriaScore
                    || (iScore.AcceptanceCriteriaScore == fScore.AcceptanceCriteriaScore
                        && iScore.DesignScore > fScore.DesignScore)
                    || (iScore.AcceptanceCriteriaScore == fScore.AcceptanceCriteriaScore
                        && iScore.DesignScore == fScore.DesignScore
                        && iScore.ReadabilityScore > fScore.ReadabilityScore)
                    || (iScore.AcceptanceCriteriaScore == fScore.AcceptanceCriteriaScore
                        && iScore.DesignScore == fScore.DesignScore
                        && iScore.ReadabilityScore == fScore.ReadabilityScore
                        && (iScore.VisualsScore ?? -1) > (fScore.VisualsScore ?? -1));

                if (initialBetter)
                {
                    var initialTotal = iScore.AcceptanceCriteriaScore + iScore.DesignScore + iScore.ReadabilityScore + (iScore.VisualsScore ?? 0);
                    var finalTotal = fScore.AcceptanceCriteriaScore + fScore.DesignScore + fScore.ReadabilityScore + (fScore.VisualsScore ?? 0);
                    _logger.LogInformation(
                        "Revision worsened {Strategy} (initial {InitAc}/{InitDesign}/{InitRead} total={InitTotal} → final {FinAc}/{FinDesign}/{FinRead} total={FinTotal}); keeping initial candidate",
                        finalCandidate.StrategyId,
                        iScore.AcceptanceCriteriaScore, iScore.DesignScore, iScore.ReadabilityScore, initialTotal,
                        fScore.AcceptanceCriteriaScore, fScore.DesignScore, fScore.ReadabilityScore, finalTotal);
                    // Restore the FULL initial candidate — patch, execution, screenshot, and scores
                    bestResults.Add(initialCandidate);
                    continue;
                }
            }
            bestResults.Add(finalCandidate);
        }

        // Re-pick winner from best-of-two results
        var bestSurvivors = bestResults.Where(c => c.Survived && c.Score is not null).ToList();
        CandidateResult? winner = null;
        string? tieBreak = null;

        if (bestSurvivors.Count > 0)
        {
            var ordered = bestSurvivors
                .OrderByDescending(c =>
                    c.Score!.AcceptanceCriteriaScore
                    + c.Score!.DesignScore
                    + c.Score!.ReadabilityScore
                    + (c.Score!.VisualsScore ?? 0))
                .ThenByDescending(c => c.Score!.AcceptanceCriteriaScore)
                .ThenBy(c => c.Execution.TokensUsed ?? long.MaxValue)
                .ThenBy(c => c.Execution.Elapsed)
                .ThenBy(c => c.StrategyId, StringComparer.Ordinal)
                .ToList();
            winner = ordered[0];
            tieBreak = "revision-round-rank";
        }

        return new EvaluationResult
        {
            Candidates = bestResults,
            Winner = winner,
            TieBreakReason = tieBreak,
            EvaluationElapsed = finalEval.EvaluationElapsed,
        };
    }

    /// <summary>
    /// Runs a revision attempt for a single strategy. Creates a fresh worktree
    /// and applies the initial patch, then invokes the strategy with RevisionContext
    /// containing judge feedback for targeted fixes.
    /// </summary>
    private async Task<(StrategyExecutionResult? exec, string patch)> RunRevisionAsync(
        TaskContext task, string strategyId, RevisionContext revCtx,
        TimeSpan timeout, StrategyFrameworkConfig cfg, CancellationToken ct)
    {
        var isExternal = _externalAdapters.ContainsKey(strategyId);
        var strategy = isExternal ? null : _strategies.GetValueOrDefault(strategyId);
        var adapter = isExternal ? _externalAdapters[strategyId] : null;

        if (strategy is null && adapter is null)
        {
            _logger.LogWarning("No strategy/adapter found for revision of {Id}", strategyId);
            return (new StrategyExecutionResult
            {
                StrategyId = strategyId,
                Succeeded = false,
                FailureReason = "revision-no-strategy",
                Elapsed = TimeSpan.Zero,
            }, "");
        }

        WorktreeHandle? handle = null;
        var sw = Stopwatch.StartNew();
        try
        {
            handle = await _worktree.CreateAsync(
                task.AgentRepoPath, cfg.CandidateDirectoryName,
                task.TaskId, strategyId + "-rev", task.BaseSha, ct);

            // Apply the original patch so the revision starts from initial code.
            // For T-FINAL (integration tasks), empty patches are valid — the strategy was told
            // "produce NO code changes if everything integrates cleanly". Skip apply in that case.
            if (!string.IsNullOrWhiteSpace(revCtx.OriginalPatch))
            {
                var applyOk = await _worktree.ApplyPatchAsync(handle.Path, revCtx.OriginalPatch, ct);
                if (!applyOk)
                {
                    _logger.LogWarning("Failed to apply initial patch for revision of {Strategy} — patch may have conflicts with base", strategyId);
                    return (new StrategyExecutionResult
                    {
                        StrategyId = strategyId,
                        Succeeded = false,
                        FailureReason = "revision-patch-apply-failed: could not apply T1 patch to fresh worktree (git apply --3way failed — likely merge conflicts with base)",
                        Elapsed = sw.Elapsed,
                    }, "");
                }
            }
            else
            {
                _logger.LogDebug("Revision for {Strategy}: empty original patch (T-FINAL clean integration) — starting from base", strategyId);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != Timeout.InfiniteTimeSpan)
                timeoutCts.CancelAfter(timeout);

            StrategyExecutionResult exec;
            string patch = "";
            try
            {
                // ── Surgical revision path: use focused feedback-only prompt ──
                // Adapters that support revision get a lightweight prompt with ONLY the
                // judge feedback and file hints. This avoids re-running the full strategy
                // which would take 7-19 min; surgical edits typically complete in 30s-2min.
                if (adapter is not null && adapter.SupportsRevision)
                {
                    var originalFiles = ExtractFilesFromPatch(revCtx.OriginalPatch);
                    var revisionInvocation = new Frameworks.FrameworkRevisionInvocation
                    {
                        WorktreePath = handle.Path,
                        FrameworkId = strategyId,
                        TaskTitle = task.TaskTitle,
                        TaskId = task.TaskId,
                        RunId = task.RunId,
                        Timeout = timeout,
                        InitialScores = revCtx.InitialScores,
                        JudgeFeedback = revCtx.JudgeFeedback,
                        AcFeedback = revCtx.AcFeedback,
                        DesignFeedback = revCtx.DesignFeedback,
                        ReadabilityFeedback = revCtx.ReadabilityFeedback,
                        VisualsFeedback = revCtx.VisualsFeedback,
                        RubberDuckFeedback = revCtx.RubberDuckFeedback,
                        OriginalFiles = originalFiles,
                        BaseSha = handle.BaseSha,
                    };

                    _logger.LogInformation(
                        "Running surgical revision for {Strategy} task {Task} ({FileCount} files to fix)",
                        strategyId, task.TaskId, originalFiles.Count);

                    var fwResult = await adapter.ExecuteRevisionAsync(revisionInvocation, timeoutCts.Token);
                    exec = FromFrameworkResult(fwResult);
                }
                else if (strategy is not null)
                {
                    // Built-in strategy: full re-execution with revision context (legacy path)
                    var invocation = new StrategyInvocation
                    {
                        Task = task,
                        WorktreePath = handle.Path,
                        StrategyId = strategyId,
                        Timeout = timeout,
                        Revision = revCtx,
                        BaseSha = handle.BaseSha,
                    };
                    exec = await strategy.ExecuteAsync(invocation, timeoutCts.Token);
                }
                else
                {
                    // External adapter that doesn't support revision: full re-execution
                    var invocation = new StrategyInvocation
                    {
                        Task = task,
                        WorktreePath = handle.Path,
                        StrategyId = strategyId,
                        Timeout = timeout,
                        Revision = revCtx,
                        BaseSha = handle.BaseSha,
                    };
                    var fwInvocation = ToFrameworkInvocation(invocation);
                    var fwResult = await adapter!.ExecuteAsync(fwInvocation, timeoutCts.Token);
                    exec = FromFrameworkResult(fwResult);
                }

                if (exec.Succeeded)
                {
                    patch = await _worktree.ExtractPatchAsync(handle.Path, handle.BaseSha, ct);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                exec = new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = $"revision-timeout after {timeout.TotalSeconds}s",
                    Elapsed = sw.Elapsed,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Revision threw for strategy {S} task {T}", strategyId, task.TaskId);
                exec = new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = $"revision-exception: {ex.GetType().Name}: {ex.Message}",
                    Elapsed = sw.Elapsed,
                };
            }

            return (exec, patch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Revision worktree setup failed for {S}", strategyId);
            return (new StrategyExecutionResult
            {
                StrategyId = strategyId,
                Succeeded = false,
                FailureReason = $"revision-worktree: {ex.GetType().Name}: {ex.Message}",
                Elapsed = sw.Elapsed,
            }, "");
        }
        finally
        {
            if (handle is not null) await handle.DisposeAsync();
        }
    }

    /// <summary>
    /// Extracts the list of file paths from a unified diff patch (lines starting with "--- a/" or "+++ b/").
    /// Used to provide file hints to the surgical revision prompt.
    /// </summary>
    private static IReadOnlyList<string> ExtractFilesFromPatch(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch)) return Array.Empty<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                var path = line[6..].Trim();
                if (path != "/dev/null")
                    files.Add(path);
            }
            else if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                var path = line[6..].Trim();
                if (path != "/dev/null")
                    files.Add(path);
            }
        }
        return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Gate Retry: re-run failed candidates from scratch ──

    /// <summary>
    /// Identifies candidates that failed gates with retryable failure modes and re-executes
    /// them from scratch. Returns updated outputs array if any retries succeeded, null if
    /// no retries were attempted or all retries also failed.
    /// </summary>
    private async Task<(StrategyExecutionResult? exec, string patch)[]?> RunGateRetryAsync(
        TaskContext task,
        (StrategyExecutionResult? exec, string patch)[] outputs,
        List<string> enabled,
        StrategyFrameworkConfig cfg,
        CancellationToken ct)
    {
        var retryCfg = cfg.GateRetry;

        // Find candidates that failed with retryable gates
        var failedCandidates = outputs
            .Where(o => o.exec is not null && !o.exec.Succeeded)
            .Where(o =>
            {
                if (retryCfg.RetryableGates.Count == 0) return true; // Empty = retry all
                // Match failure reason against retryable gates
                var reason = o.exec!.FailureReason ?? "";
                return retryCfg.RetryableGates.Any(g =>
                    reason.Contains(g, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (failedCandidates.Count == 0)
            return null;

        // Retry even when no candidate succeeded — the task may be feasible but both
        // strategies hit transient issues (network, auth, etc.). Previously we skipped
        // retries if none succeeded, but this made ALL-fail scenarios unrecoverable.
        _logger.LogInformation(
            "Gate retry: {Count} candidates failed with retryable gates for task {Task} — retrying",
            failedCandidates.Count, task.TaskId);

        await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
            task.RunId, task.TaskId, "retrying-failed", failedCandidates.Count, enabled.Count,
            $"Retrying {failedCandidates.Count} gate-failed candidate(s)…"), ct);

        var retryTasks = new List<(int outputIndex, Task<(StrategyExecutionResult? exec, string patch)> retryTask)>();

        foreach (var failed in failedCandidates)
        {
            var strategyId = failed.exec!.StrategyId;
            var outputIndex = Array.FindIndex(outputs, o =>
                o.exec?.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase) == true);
            if (outputIndex < 0) continue;

            var failedGate = failed.exec.FailureReason?.Split(':')[0] ?? "unknown";
            await _events.EmitAsync(StrategyEvents.CandidateRetryStarted, new CandidateRetryStartedEvent(
                task.RunId, task.TaskId, strategyId, failedGate, DateTimeOffset.UtcNow), ct);

            // Re-run from scratch using RunOneAsync — use original cfg (timeout per strategy already set)
            retryTasks.Add((outputIndex, RunOneAsync(task, strategyId, cfg, ct, ct)));
        }

        var retryResults = await Task.WhenAll(retryTasks.Select(t => t.retryTask));

        // Emit retry-completed events and merge results
        var anyRetrySucceeded = false;
        var updatedOutputs = outputs.ToArray(); // Copy

        for (int i = 0; i < retryTasks.Count; i++)
        {
            var (outputIndex, _) = retryTasks[i];
            var retryResult = retryResults[i];
            var strategyId = failedCandidates[i].exec!.StrategyId;

            await _events.EmitAsync(StrategyEvents.CandidateRetryCompleted, new CandidateRetryCompletedEvent(
                task.RunId, task.TaskId, strategyId,
                retryResult.exec?.Succeeded ?? false,
                retryResult.exec?.FailureReason,
                retryResult.exec?.Elapsed.TotalSeconds ?? 0,
                retryResult.exec?.TokensUsed), ct);

            if (retryResult.exec?.Succeeded == true)
            {
                // Replace original failed output with successful retry
                updatedOutputs[outputIndex] = retryResult;
                anyRetrySucceeded = true;
                _logger.LogInformation(
                    "Gate retry succeeded for {Strategy} task {Task}",
                    strategyId, task.TaskId);
            }
            else
            {
                _logger.LogInformation(
                    "Gate retry also failed for {Strategy} task {Task}: {Reason}",
                    strategyId, task.TaskId, retryResult.exec?.FailureReason ?? "unknown");
            }
        }

        return anyRetrySucceeded ? updatedOutputs : null;
    }

    private void LogOrchestrationSummary(string taskId, Stopwatch runSw, EvaluationResult evalResult)
    {
        var winnerId = evalResult.Winner?.StrategyId ?? "<none>";
        var candidateTimes = string.Join(", ",
            evalResult.Candidates.Select(c => $"{c.StrategyId}={c.Execution.Elapsed.TotalSeconds:F1}s"));
        _logger.LogInformation(
            "Strategy orchestration wall-clock for task {Task}: {Total:F1}s (winner={Winner}); candidates: {Candidates}",
            taskId, runSw.Elapsed.TotalSeconds, winnerId, candidateTimes);
    }

    /// <summary>
    /// Checks for a recovery checkpoint from a prior runner session. If one exists with
    /// a matching baseSha, reconstructs the evaluation inputs and runs evaluation (or
    /// returns the pre-selected winner) without re-executing strategies from scratch.
    /// </summary>
    private async Task<OrchestrationOutcome?> TryRecoverFromCheckpointAsync(
        TaskContext task, StrategyFrameworkConfig cfg, CancellationToken ct)
    {
        if (_recovery is null) return null;

        var checkpoint = _recovery.GetCheckpoint(task.TaskId, task.BaseSha);
        if (checkpoint is null) return null;

        _logger.LogInformation(
            "Strategy recovery: found checkpoint for task {TaskId} run {RunId} phase={Phase} ({Count} candidates)",
            task.TaskId, checkpoint.RunId, checkpoint.Phase, checkpoint.Candidates.Count);

        var runSw = Stopwatch.StartNew();

        try
        {
            // Reconstruct StrategyExecutionResult + patch pairs from the checkpoint
            var evalInput = checkpoint.Candidates
                .Select(c => (
                    exec: new StrategyExecutionResult
                    {
                        StrategyId = c.StrategyId,
                        Succeeded = c.Succeeded,
                        FailureReason = c.FailureReason,
                        NoOpAcknowledged = c.NoOpAcknowledged,
                        Elapsed = TimeSpan.FromSeconds(c.ElapsedSeconds),
                        TokensUsed = c.TokensUsed,
                    },
                    patch: c.Patch,
                    mediaSteps: c.MediaCaptureSteps))
                .ToList();

            // Restore media capture progress into CandidateStateStore so the UI can show timelines
            if (_candidateStateStore is not null)
            {
                foreach (var c in evalInput)
                {
                    if (c.mediaSteps is { Count: > 0 })
                    {
                        var progress = new MediaCapture.MediaCaptureProgressSnapshot
                        {
                            Steps = System.Collections.Immutable.ImmutableList.CreateRange(c.mediaSteps),
                            StartedAt = c.mediaSteps[0].StartedAt ?? DateTimeOffset.UtcNow,
                            TotalElapsedMs = c.mediaSteps.Sum(s => s.ElapsedMs ?? 0),
                        };
                        _candidateStateStore.RestoreMediaCaptureProgress(
                            task.RunId, task.TaskId, c.exec.StrategyId, progress);
                    }
                }
            }

            if (evalInput.Count == 0)
            {
                _logger.LogWarning("Strategy recovery: checkpoint has 0 candidates — discarding");
                _recovery.MarkApplied(task.TaskId, checkpoint.RunId);
                return null;
            }

            // If winner was already selected (crash between winner-select and apply),
            // return the outcome directly without re-evaluating
            if (checkpoint.Phase == "WinnerSelected" && !string.IsNullOrEmpty(checkpoint.WinnerStrategyId))
            {
                var winnerCandidate = evalInput.FirstOrDefault(e =>
                    string.Equals(e.exec.StrategyId, checkpoint.WinnerStrategyId, StringComparison.OrdinalIgnoreCase));

                if (winnerCandidate.exec is not null)
                {
                    _logger.LogInformation(
                        "Strategy recovery: winner {Winner} already selected — skipping evaluation, applying directly",
                        checkpoint.WinnerStrategyId);

                    var winnerResult = new CandidateResult
                    {
                        StrategyId = winnerCandidate.exec.StrategyId,
                        Survived = true,
                        Patch = winnerCandidate.patch,
                        PatchSizeBytes = System.Text.Encoding.UTF8.GetByteCount(winnerCandidate.patch),
                        Execution = winnerCandidate.exec,
                    };

                    await _events.EmitAsync(StrategyEvents.WinnerSelected, new WinnerSelectedEvent(
                        task.RunId, task.TaskId, winnerResult.StrategyId,
                        "recovered-winner", 0), ct);

                    _recovery.MarkApplied(task.TaskId, checkpoint.RunId);

                    return new OrchestrationOutcome(task, new EvaluationResult
                    {
                        Candidates = new List<CandidateResult> { winnerResult },
                        Winner = winnerResult,
                        TieBreakReason = "recovered-winner",
                        EvaluationElapsed = runSw.Elapsed,
                    });
                }
            }

            // Phase = ExecutionDone: re-evaluate from persisted patches
            _logger.LogInformation(
                "Strategy recovery: re-evaluating {Count} candidates from checkpoint (skipping re-execution)",
                evalInput.Count);

            await _events.EmitAsync(StrategyEvents.EvaluationProgress, new EvaluationProgressEvent(
                task.RunId, task.TaskId, "recovery-evaluating", evalInput.Count, evalInput.Count,
                $"Recovering {evalInput.Count} candidates from prior session…"), ct);

            var evalResult = await _evaluator.EvaluateAsync(task,
                evalInput.Select(c => (c.exec, c.patch)).ToList(), ct);

            // Emit events for recovered candidates
            // Emit all dashboard events (CandidateEvaluated + CandidateScored + CandidateDetail)
            var survivorCount = evalResult.Candidates.Count(c => c.Survived);
            string? judgeSkippedReason = survivorCount switch
            {
                0 => "no-survivors",
                1 => "sole-survivor",
                _ when evalResult.Candidates.All(c => c.Score is null) => "no-judge-configured",
                _ => null,
            };

            foreach (var c in evalResult.Candidates)
            {
                var screenshotBase64 = c.ScreenshotBytes is { Length: > 0 }
                    ? Convert.ToBase64String(c.ScreenshotBytes) : null;
                await _events.EmitAsync(StrategyEvents.CandidateEvaluated, new CandidateEvaluatedEvent(
                    task.RunId, task.TaskId, c.StrategyId,
                    c.Survived, c.FailedGate, c.FailureDetail,
                    screenshotBase64,
                    c.Survived ? judgeSkippedReason : null,
                    c.VideoPath, c.ScreenshotPaths, c.AnimatedGifPath,
                    c.PreviewSource, c.IncludedAssetPaths,
                    c.SecondaryPreviewBase64, c.SecondaryAssetPaths, c.SecondaryPreviewSource,
                    c.CaptureMetrics, c.PageAnalysis), ct);

                if (c.Score is not null)
                {
                    await _events.EmitAsync(StrategyEvents.CandidateScored, new CandidateScoredEvent(
                        task.RunId, task.TaskId, c.StrategyId,
                        c.Score.AcceptanceCriteriaScore, c.Score.DesignScore, c.Score.ReadabilityScore,
                        c.Score.VisualsScore, screenshotBase64, c.Score.Feedback,
                        c.Score.AcFeedback, c.Score.DesignFeedback, c.Score.ReadabilityFeedback, c.Score.VisualsFeedback,
                        c.PreviewSource, c.IncludedAssetPaths,
                        c.SecondaryPreviewBase64, c.SecondaryAssetPaths, c.SecondaryPreviewSource), ct);
                }

                var summary = BuildExecutionSummary(c, judgeSkippedReason);
                await _events.EmitAsync(StrategyEvents.CandidateDetail,
                    new CandidateDetailEvent(task.RunId, task.TaskId, c.StrategyId, summary), ct);
            }

            if (evalResult.Winner is not null)
            {
                await _events.EmitAsync(StrategyEvents.WinnerSelected, new WinnerSelectedEvent(
                    task.RunId, task.TaskId, evalResult.Winner.StrategyId,
                    $"recovered+{evalResult.TieBreakReason}",
                    evalResult.EvaluationElapsed.TotalSeconds), ct);
            }

            // Write experiment record for the recovered evaluation
            _tracker.Write(new ExperimentRecord
            {
                RunId = task.RunId,
                TaskId = task.TaskId,
                TaskTitle = task.TaskTitle,
                StartedAt = DateTimeOffset.UtcNow - runSw.Elapsed,
                CompletedAt = DateTimeOffset.UtcNow,
                Candidates = evalResult.Candidates.Select(c => new CandidateRecord
                {
                    StrategyId = c.StrategyId,
                    Succeeded = c.Survived,
                    FailureReason = c.FailureDetail,
                    FailedGate = c.FailedGate,
                    ElapsedSec = c.Execution.Elapsed.TotalSeconds,
                    PatchSizeBytes = c.PatchSizeBytes,
                    TokensUsed = c.Execution.TokensUsed,
                    AcceptanceCriteriaScore = c.Score?.AcceptanceCriteriaScore,
                    DesignScore = c.Score?.DesignScore,
                    ReadabilityScore = c.Score?.ReadabilityScore,
                    VisualsScore = c.Score?.VisualsScore,
                    FrameworkId = c.StrategyId,
                    IsExternalFramework = _externalAdapters.ContainsKey(c.StrategyId),
                }).ToList(),
                WinnerStrategyId = evalResult.Winner?.StrategyId,
                TieBreakReason = $"recovered+{evalResult.TieBreakReason}",
                EvaluationElapsedSec = evalResult.EvaluationElapsed.TotalSeconds,
                TotalTokens = evalResult.Candidates.Sum(c => c.Execution.TokensUsed ?? 0),
            });

            _recovery.MarkApplied(task.TaskId, checkpoint.RunId);
            LogOrchestrationSummary(task.TaskId, runSw, evalResult);

            _logger.LogInformation(
                "Strategy recovery SUCCEEDED for task {TaskId}: winner={Winner} (saved ~{SavedMin:F0} min of re-execution)",
                task.TaskId, evalResult.Winner?.StrategyId ?? "<none>",
                checkpoint.Candidates.Sum(c => c.ElapsedSeconds) / 60.0);

            return new OrchestrationOutcome(task, evalResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate genuine cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Strategy recovery evaluation FAILED for task {TaskId} — attempting emergency winner from checkpoint data",
                task.TaskId);

            // Layer 2: Try emergency winner from checkpoint candidates before falling through
            // to expensive fresh execution. The checkpoint already has candidate patches and results.
            if (_cfg.CurrentValue.Evaluator.EmergencyWinnerEnabled && checkpoint.Candidates.Count > 0)
            {
                try
                {
                    var emergencyCandidates = checkpoint.Candidates.Select(c => new CandidateResult
                    {
                        StrategyId = c.StrategyId,
                        Survived = c.Succeeded,
                        Patch = c.Patch,
                        PatchSizeBytes = System.Text.Encoding.UTF8.GetByteCount(c.Patch ?? ""),
                        Execution = new StrategyExecutionResult
                        {
                            StrategyId = c.StrategyId,
                            Succeeded = c.Succeeded,
                            FailureReason = c.FailureReason,
                            NoOpAcknowledged = c.NoOpAcknowledged,
                            Elapsed = TimeSpan.FromSeconds(c.ElapsedSeconds),
                            TokensUsed = c.TokensUsed,
                        },
                    }).ToList();

                    var emergencyResult = _evaluator.SelectEmergencyWinner(emergencyCandidates);
                    if (emergencyResult?.Winner != null)
                    {
                        _recovery.SaveWinnerSelected(task.TaskId, checkpoint.RunId, emergencyResult.Winner.StrategyId);

                        var emergencyScore = (double)(
                            (emergencyResult.Winner.Score?.AcceptanceCriteriaScore ?? 0) +
                            (emergencyResult.Winner.Score?.DesignScore ?? 0) +
                            (emergencyResult.Winner.Score?.ReadabilityScore ?? 0));
                        await _events.EmitAsync(StrategyEvents.WinnerSelected, new WinnerSelectedEvent(
                            task.RunId, task.TaskId, emergencyResult.Winner.StrategyId,
                            "recovery-emergency",
                            emergencyScore), CancellationToken.None);

                        _logger.LogWarning(
                            "🚨 Recovery emergency winner salvaged: {StrategyId} for task {TaskId} (avoided fresh re-execution)",
                            emergencyResult.Winner.StrategyId, task.TaskId);

                        _recovery.MarkApplied(task.TaskId, checkpoint.RunId);
                        return new OrchestrationOutcome(task, emergencyResult);
                    }
                }
                catch (Exception emergencyEx)
                {
                    _logger.LogError(emergencyEx,
                        "Recovery emergency winner selection itself failed for task {TaskId}", task.TaskId);
                }
            }

            _logger.LogWarning(
                "Strategy recovery FAILED for task {TaskId} — falling through to fresh execution",
                task.TaskId);
            _recovery.MarkApplied(task.TaskId, checkpoint.RunId); // Clean up so we don't retry
            return null; // Fall through to normal execution
        }
    }

    private async Task<(StrategyExecutionResult? exec, string patch)> RunOneAsync(
        TaskContext task, string strategyId, StrategyFrameworkConfig cfg,
        CancellationToken candidateCt, CancellationToken parentCt = default)
    {
        var isExternal = _externalAdapters.ContainsKey(strategyId);
        var timeout = cfg.Timeouts.GetTimeout(strategyId);

        // External adapters: pre-flight lifecycle check (readiness).
        if (isExternal && _externalAdapters[strategyId] is IFrameworkLifecycle lifecycle)
        {
            try
            {
                var readiness = await lifecycle.CheckReadinessAsync(candidateCt);
                if (readiness.Status != FrameworkReadiness.Ready)
                {
                    _logger.LogWarning(
                        "Framework {Id} not ready ({Status}): {Msg}. Missing: {Missing}",
                        strategyId, readiness.Status, readiness.Message,
                        string.Join(", ", readiness.MissingDependencies));

                    var failExec = new StrategyExecutionResult
                    {
                        StrategyId = strategyId,
                        Succeeded = false,
                        FailureReason = $"framework-not-ready: {readiness.Message}",
                        Elapsed = TimeSpan.Zero,
                    };
                    await _events.EmitAsync(StrategyEvents.CandidateCompleted,
                        new CandidateCompletedEvent(task.RunId, task.TaskId, strategyId,
                            false, failExec.FailureReason, 0, null), CancellationToken.None);
                    return (failExec, "");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Readiness check threw for framework {Id}", strategyId);
                var failExec = new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = $"readiness-check-error: {ex.Message}",
                    Elapsed = TimeSpan.Zero,
                };
                await _events.EmitAsync(StrategyEvents.CandidateCompleted,
                    new CandidateCompletedEvent(task.RunId, task.TaskId, strategyId,
                        false, failExec.FailureReason, 0, null), CancellationToken.None);
                return (failExec, "");
            }
        }

        var strategy = isExternal ? null : _strategies[strategyId];
        var adapter = isExternal ? _externalAdapters[strategyId] : null;

        await _events.EmitAsync(StrategyEvents.CandidateStarted,
            new CandidateStartedEvent(task.RunId, task.TaskId, strategyId, DateTimeOffset.UtcNow, task.Wave, task.TaskTitle), CancellationToken.None);

        WorktreeHandle? handle = null;
        var sw = Stopwatch.StartNew();
        try
        {
            try
            {
                handle = await _worktree.CreateAsync(
                    task.AgentRepoPath, cfg.CandidateDirectoryName,
                    task.TaskId, strategyId, task.BaseSha, candidateCt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !candidateCt.IsCancellationRequested)
            {
                _logger.LogError(ex, "Worktree create failed for strategy {S} task {T}", strategyId, task.TaskId);
                var failExec = new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = $"worktree-create: {ex.GetType().Name}: {ex.Message}",
                    Elapsed = sw.Elapsed,
                };
                await _events.EmitAsync(StrategyEvents.CandidateCompleted, new CandidateCompletedEvent(
                    task.RunId, task.TaskId, strategyId, failExec.Succeeded, failExec.FailureReason,
                    failExec.Elapsed.TotalSeconds, failExec.TokensUsed), CancellationToken.None);
                return (failExec, "");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(candidateCt);
            if (timeout != Timeout.InfiniteTimeSpan)
                timeoutCts.CancelAfter(timeout);

            var invocation = new StrategyInvocation
            {
                Task = task,
                WorktreePath = handle.Path,
                StrategyId = strategyId,
                Timeout = timeout,
                BaseSha = handle.BaseSha,
            };

            // Activity sink shared by both built-in strategies and external adapters.
            // Wrapped with AgenticStreamAnalyzer for AI-powered session monitoring.
            using var analyzer = new AgenticStreamAnalyzer(_logger, _llmRunner);
            analyzer.Initialize(task.TaskId, task.TaskTitle);
            analyzer.OnStateChanged += snapshot =>
            {
                _ = _events.EmitAsync(StrategyEvents.CandidateAnalyzerUpdate,
                    new CandidateAnalyzerUpdateEvent(task.RunId, task.TaskId, strategyId,
                        snapshot.ToolCallCount, snapshot.BuildPassed, snapshot.TestsPassed,
                        snapshot.BuildFailCount, snapshot.AnalyzerVerdict, snapshot.NudgeSent),
                    CancellationToken.None);
            };
            var analyzerTeeSink = new AnalyzerTeeSink(null, analyzer);

            var activitySink = new Progress<FrameworkActivityEvent>(activity =>
            {
                // Forward to dashboard (CandidateStateStore also updates LastActivityAt
                // for stuck-candidate detection via its event subscription)
                var activityEntry = new ActivityEntry(
                    DateTimeOffset.UtcNow, activity.Category, activity.Message, activity.Metadata);
                _ = _events.EmitAsync(StrategyEvents.CandidateActivity,
                    new CandidateActivityEvent(task.RunId, task.TaskId, strategyId, activityEntry),
                    CancellationToken.None);

                // Forward to AI analyzer
                analyzer.OnActivityEvent(activity);
            });

            StrategyExecutionResult exec;
            string patch = "";
            try
            {
                if (strategy is not null)
                {
                    exec = await strategy.ExecuteAsync(
                        invocation with { ActivitySink = activitySink }, timeoutCts.Token);
                }
                else
                {
                    // External adapter path: pre-execution gate → execute → post-execution gate.
                    var fwInvocation = ToFrameworkInvocation(invocation, activitySink);

                    if (FrameworkExecutionGate.RequiresPreExecutionGate(strategyId))
                    {
                        var preGate = FrameworkExecutionGate.CreatePreExecutionGate(
                            strategyId, task.TaskId, task.TaskTitle, timeout);
                        _logger.LogInformation(
                            "[FrameworkGate] PRE {FrameworkId} task {TaskId}: {Summary}",
                            preGate.FrameworkId, preGate.TaskId, preGate.Summary);
                    }

                    var fwResult = await adapter!.ExecuteAsync(fwInvocation, timeoutCts.Token);
                    exec = FromFrameworkResult(fwResult);

                    if (FrameworkExecutionGate.RequiresPreExecutionGate(strategyId))
                    {
                        var postGate = FrameworkExecutionGate.CreatePostExecutionGate(
                            strategyId, task.TaskId, fwResult);
                        _logger.LogInformation(
                            "[FrameworkGate] POST {FrameworkId} task {TaskId}: {Summary}",
                            postGate.FrameworkId, postGate.TaskId, postGate.Summary);
                    }
                }

                if (exec.Succeeded)
                {
                    patch = await _worktree.ExtractPatchAsync(handle.Path, handle.BaseSha, candidateCt);

                    // ── Empty Patch Retry ──
                    // If strategy reports success but produced no file changes, this is likely a
                    // transient CLI failure (network error, auth timeout, tool execution failure
                    // that the CLI swallowed). Retry once in a fresh worktree before accepting
                    // the empty result. This prevents phantom "gate1-output: empty patch" failures
                    // that require manual investigation.
                    //
                    // Exception: if the strategy explicitly acknowledged a no-op (the agentic
                    // CLI inspected the worktree and reported "task already complete" because
                    // a prior merged PR already contains the implementation), retrying in a
                    // fresh worktree at the same BaseSha would just produce the same verdict.
                    // Skip the retry and let the candidate surface as a legitimate no-op.
                    if (string.IsNullOrWhiteSpace(patch) && !task.TaskId.Equals("T-FINAL", StringComparison.OrdinalIgnoreCase)
                        && !exec.NoOpAcknowledged)
                    {
                        var diagInfo = $"strategy={strategyId}, elapsed={sw.Elapsed.TotalSeconds:F1}s, tokens={exec.TokensUsed ?? 0}";
                        _logger.LogWarning(
                            "Empty patch from SUCCESSFUL strategy {Strategy} for task {Task} — retrying in fresh worktree ({Diag})",
                            strategyId, task.TaskId, diagInfo);

                        // Emit diagnostic activity so the UI shows what happened
                        await _events.EmitAsync(StrategyEvents.CandidateActivity,
                            new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                    $"⚠️ Strategy reported success but produced no file changes (elapsed: {sw.Elapsed.TotalSeconds:F1}s). Retrying in fresh worktree…",
                                    new Dictionary<string, object>
                                    {
                                        ["reason"] = "empty-patch-from-success",
                                        ["elapsed"] = $"{sw.Elapsed.TotalSeconds:F1}s",
                                        ["tokens"] = (exec.TokensUsed ?? 0).ToString(),
                                    })),
                            CancellationToken.None);

                        // Dispose current worktree and retry
                        await handle.DisposeAsync();
                        handle = null;

                        try
                        {
                            handle = await _worktree.CreateAsync(
                                task.AgentRepoPath, cfg.CandidateDirectoryName,
                                task.TaskId, strategyId + "-retry", task.BaseSha, candidateCt);

                            // Use retry timeout (or no timeout when configured to 0)
                            var retryTimeout = TimeoutsConfig.ToTimeSpan(cfg.GateRetry.RetryTimeoutSeconds);
                            if (retryTimeout != Timeout.InfiniteTimeSpan && timeout != Timeout.InfiniteTimeSpan)
                                retryTimeout = TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, retryTimeout.TotalSeconds));
                            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(candidateCt);
                            if (retryTimeout != Timeout.InfiniteTimeSpan)
                                retryCts.CancelAfter(retryTimeout);

                            var retryInvocation = new StrategyInvocation
                            {
                                Task = task,
                                WorktreePath = handle.Path,
                                StrategyId = strategyId,
                                Timeout = retryTimeout,
                                BaseSha = handle.BaseSha,
                            };

                            StrategyExecutionResult retryExec;
                            if (strategy is not null)
                            {
                                retryExec = await strategy.ExecuteAsync(
                                    retryInvocation with { ActivitySink = activitySink }, retryCts.Token);
                            }
                            else
                            {
                                var fwInvocation = ToFrameworkInvocation(retryInvocation, activitySink);
                                var fwResult = await adapter!.ExecuteAsync(fwInvocation, retryCts.Token);
                                retryExec = FromFrameworkResult(fwResult);
                            }

                            if (retryExec.Succeeded)
                            {
                                var retryPatch = await _worktree.ExtractPatchAsync(handle.Path, handle.BaseSha, candidateCt);
                                if (!string.IsNullOrWhiteSpace(retryPatch))
                                {
                                    _logger.LogInformation(
                                        "Empty patch retry SUCCEEDED for {Strategy} task {Task} — got {Lines} lines",
                                        strategyId, task.TaskId, retryPatch.Split('\n').Length);

                                    await _events.EmitAsync(StrategyEvents.CandidateActivity,
                                        new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                            new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                                $"✅ Retry produced {retryPatch.Split('\n').Length} lines of changes",
                                                null)),
                                        CancellationToken.None);

                                    // Accumulate tokens from both attempts
                                    var totalTokens = (exec.TokensUsed ?? 0) + (retryExec.TokensUsed ?? 0);
                                    exec = retryExec with
                                    {
                                        Elapsed = sw.Elapsed, // Total wall-clock including first attempt
                                        TokensUsed = totalTokens > 0 ? totalTokens : null,
                                    };
                                    patch = retryPatch;
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "Empty patch retry also produced empty for {Strategy} task {Task} — accepting failure",
                                        strategyId, task.TaskId);

                                    await _events.EmitAsync(StrategyEvents.CandidateActivity,
                                        new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                            new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                                "❌ Retry also produced no file changes — likely a prompt or task issue, not transient",
                                                null)),
                                        CancellationToken.None);
                                }
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Empty patch retry FAILED for {Strategy} task {Task}: {Reason}",
                                    strategyId, task.TaskId, retryExec.FailureReason);

                                await _events.EmitAsync(StrategyEvents.CandidateActivity,
                                    new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                        new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                            $"❌ Retry failed: {retryExec.FailureReason}",
                                            null)),
                                    CancellationToken.None);
                            }
                        }
                        catch (OperationCanceledException) when (!candidateCt.IsCancellationRequested)
                        {
                            _logger.LogWarning("Empty patch retry timed out for {Strategy} task {Task}", strategyId, task.TaskId);

                            await _events.EmitAsync(StrategyEvents.CandidateActivity,
                                new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                    new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                        "❌ Retry timed out",
                                        null)),
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Empty patch retry threw for {Strategy} task {Task}", strategyId, task.TaskId);
                        }
                    }
                }

                // ── Stuck/Transient Failure Retry with Escalation Ladder ──
                // If a built-in strategy failed due to a transient issue (stuck-no-output,
                // exit-nonzero, tool-call-cap, wrapper-child-exited), retry with escalating
                // recovery strategies:
                //   Attempt 1 (rung 1): retry in fresh worktree, same config
                //   Attempt 2 (rung 2): retry in fresh worktree, ForceNoWrapper=true
                // If both fail, the candidate is marked failed (FlowMonitor can then cancel).
                if (!exec.Succeeded && strategy is not null
                    && (exec.FailureReason?.Contains("stuck", StringComparison.OrdinalIgnoreCase) == true
                        || exec.FailureReason?.Contains("exit-nonzero", StringComparison.OrdinalIgnoreCase) == true
                        || exec.FailureReason?.Contains("tool-call-cap", StringComparison.OrdinalIgnoreCase) == true
                        || exec.FailureReason?.Contains("wrapper", StringComparison.OrdinalIgnoreCase) == true
                        || exec.FailureReason?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true
                        || exec.FailureReason?.Contains("reset-by-operator", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var maxRetries = 2; // rung 1 + rung 2

                    // If the original candidate token was cancelled (reset), create a fresh one for retries.
                    var retryCandidateCts = candidateCt.IsCancellationRequested
                        ? CancellationTokenSource.CreateLinkedTokenSource(parentCt)
                        : null;
                    var retryToken = retryCandidateCts?.Token ?? candidateCt;
                    if (retryCandidateCts is not null)
                        _cancellation?.RegisterCandidate(task.RunId, task.TaskId, strategyId, retryCandidateCts);

                    try
                    {
                    for (var retryAttempt = 1; retryAttempt <= maxRetries; retryAttempt++)
                    {
                        var forceNoWrapper = retryAttempt >= 2; // rung 2: strip wrapper
                        var rungLabel = forceNoWrapper ? "rung 2 — no wrapper" : "rung 1 — same config";

                        _logger.LogWarning(
                            "Strategy {Strategy} failed ({Reason}) for task {Task} — retry {Attempt}/{Max} ({Rung})",
                            strategyId, exec.FailureReason, task.TaskId, retryAttempt, maxRetries, rungLabel);

                        await _events.EmitAsync(StrategyEvents.CandidateActivity,
                            new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                    $"⚠️ Strategy failed ({exec.FailureReason}). Retry {retryAttempt}/{maxRetries} ({rungLabel}) in fresh worktree…",
                                    null)),
                            CancellationToken.None);

                        await handle.DisposeAsync();
                        handle = null;

                        try
                        {
                            handle = await _worktree.CreateAsync(
                                task.AgentRepoPath, cfg.CandidateDirectoryName,
                                task.TaskId, $"{strategyId}-retry{retryAttempt}", task.BaseSha, retryToken);

                            var retryTimeout = TimeoutsConfig.ToTimeSpan(cfg.GateRetry.RetryTimeoutSeconds);
                            if (retryTimeout != Timeout.InfiniteTimeSpan && timeout != Timeout.InfiniteTimeSpan)
                                retryTimeout = TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, retryTimeout.TotalSeconds));
                            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(retryToken);
                            if (retryTimeout != Timeout.InfiniteTimeSpan)
                                retryCts.CancelAfter(retryTimeout);

                            var retryInvocation = new StrategyInvocation
                            {
                                Task = task,
                                WorktreePath = handle.Path,
                                StrategyId = strategyId,
                                Timeout = retryTimeout,
                                BaseSha = handle.BaseSha,
                                ForceNoWrapper = forceNoWrapper,
                                AttemptNumber = retryAttempt,
                            };

                            var retryExec = await strategy.ExecuteAsync(
                                retryInvocation with { ActivitySink = activitySink }, retryCts.Token);

                            if (retryExec.Succeeded)
                            {
                                var retryPatch = await _worktree.ExtractPatchAsync(handle.Path, handle.BaseSha, retryToken);
                                if (!string.IsNullOrWhiteSpace(retryPatch))
                                {
                                    _logger.LogInformation(
                                        "Retry {Attempt} ({Rung}) SUCCEEDED for {Strategy} task {Task} — {Lines} lines",
                                        retryAttempt, rungLabel, strategyId, task.TaskId, retryPatch.Split('\n').Length);

                                    await _events.EmitAsync(StrategyEvents.CandidateActivity,
                                        new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                            new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                                $"✅ Retry {retryAttempt} ({rungLabel}) succeeded with {retryPatch.Split('\n').Length} lines",
                                                null)),
                                        CancellationToken.None);

                                    exec = retryExec with { Elapsed = sw.Elapsed };
                                    patch = retryPatch;
                                    break; // Success — exit retry loop
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "Retry {Attempt} succeeded but produced empty patch for {Strategy} task {Task}",
                                        retryAttempt, strategyId, task.TaskId);
                                }
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Retry {Attempt} ({Rung}) FAILED for {Strategy} task {Task}: {Reason}",
                                    retryAttempt, rungLabel, strategyId, task.TaskId, retryExec.FailureReason);

                                await _events.EmitAsync(StrategyEvents.CandidateActivity,
                                    new CandidateActivityEvent(task.RunId, task.TaskId, strategyId,
                                        new ActivityEntry(DateTimeOffset.UtcNow, "retry",
                                            $"❌ Retry {retryAttempt} ({rungLabel}) failed: {retryExec.FailureReason}",
                                            null)),
                                    CancellationToken.None);
                                // Update exec for the next iteration's failure reason check
                                exec = retryExec with { Elapsed = sw.Elapsed };
                            }
                        }
                        catch (OperationCanceledException) when (!retryToken.IsCancellationRequested)
                        {
                            _logger.LogWarning("Retry {Attempt} timed out for {Strategy} task {Task}", retryAttempt, strategyId, task.TaskId);
                            break; // Don't retry after timeout
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Retry {Attempt} threw for {Strategy} task {Task}", retryAttempt, strategyId, task.TaskId);
                            break; // Don't retry after exception
                        }
                    }
                    }
                    finally { retryCandidateCts?.Dispose(); }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !candidateCt.IsCancellationRequested)
            {
                exec = new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = $"timeout after {timeout.TotalSeconds}s",
                    Elapsed = sw.Elapsed,
                };
            }
            catch (OperationCanceledException) when (candidateCt.IsCancellationRequested && !parentCt.IsCancellationRequested)
            {
                var isReset = _cancellation?.IsResetRequested(task.RunId, task.TaskId, strategyId) == true;
                if (isReset)
                {
                    _cancellation!.ClearResetFlag(task.RunId, task.TaskId, strategyId);
                    _logger.LogInformation("Candidate {S} reset by operator for task {T} — will retry in fresh worktree", strategyId, task.TaskId);
                    exec = new StrategyExecutionResult
                    {
                        StrategyId = strategyId,
                        Succeeded = false,
                        FailureReason = "reset-by-operator",
                        Elapsed = sw.Elapsed,
                    };
                }
                else
                {
                    _logger.LogInformation("Candidate {S} cancelled by user for task {T}", strategyId, task.TaskId);
                    exec = new StrategyExecutionResult
                    {
                        StrategyId = strategyId,
                        Succeeded = false,
                        FailureReason = "cancelled-by-user",
                        Elapsed = sw.Elapsed,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Strategy {S} threw for task {T}", strategyId, task.TaskId);
                exec = new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = $"exception: {ex.GetType().Name}: {ex.Message}",
                    Elapsed = sw.Elapsed,
                };
            }

            await _events.EmitAsync(StrategyEvents.CandidateCompleted, new CandidateCompletedEvent(
                task.RunId, task.TaskId, strategyId, exec.Succeeded, exec.FailureReason,
                exec.Elapsed.TotalSeconds, exec.TokensUsed), CancellationToken.None);

            if (_budget is not null && exec.TokensUsed is > 0)
                _budget.Charge(task.RunId, exec.TokensUsed.Value);

            if (_usage is not null && exec.TokensUsed is > 0)
                _usage.RecordStrategyTokens(strategyId, "cli-estimated", exec.TokensUsed.Value);

            return (exec, patch);
        }
        finally
        {
            _cancellation?.UnregisterCandidate(task.RunId, task.TaskId, strategyId);
            if (handle is not null) await handle.DisposeAsync();
        }
    }

    // ── Framework ↔ Strategy type converters ──

    private static FrameworkInvocation ToFrameworkInvocation(StrategyInvocation si, IProgress<FrameworkActivityEvent>? activitySink = null) => new()
    {
        Task = new FrameworkTaskContext
        {
            TaskId = si.Task.TaskId,
            TaskTitle = si.Task.TaskTitle,
            TaskDescription = si.Task.TaskDescription,
            PrBranch = si.Task.PrBranch,
            BaseSha = si.Task.BaseSha,
            RunId = si.Task.RunId,
            AgentRepoPath = si.Task.AgentRepoPath,
            Complexity = si.Task.Complexity,
            IsWebTask = si.Task.IsWebTask,
            PmSpec = si.Task.PmSpec,
            Architecture = si.Task.Architecture,
            TechStack = si.Task.TechStack,
            IssueContext = si.Task.IssueContext,
            DesignContext = si.Task.DesignContext,
            ExistingProjectContext = si.Task.ExistingProjectContext,
        },
        WorktreePath = si.WorktreePath,
        FrameworkId = si.StrategyId,
        Timeout = si.Timeout,
        ActivitySink = activitySink,
        Revision = si.Revision,
    };

    private static StrategyExecutionResult FromFrameworkResult(FrameworkExecutionResult fr) => new()
    {
        StrategyId = fr.FrameworkId,
        Succeeded = fr.Succeeded,
        FailureReason = fr.FailureReason,
        Elapsed = fr.Elapsed,
        TokensUsed = fr.TokensUsed,
        Log = fr.Log,
    };

    /// <summary>
    /// Build a post-execution summary from evaluation result data (patch, metrics, logs, scores).
    /// Centralizes diff parsing via <see cref="PatchAnalyzer"/>.
    /// </summary>
    private static CandidateExecutionSummary BuildExecutionSummary(
        CandidateResult c, string? judgeSkippedReason)
    {
        var fileChanges = PatchAnalyzer.Parse(c.Patch);
        var fileSummaries = fileChanges.Select(f => new FileChangeSummary
        {
            Path = f.Path,
            Type = f.Type.ToString(),
            LinesAdded = f.LinesAdded,
            LinesRemoved = f.LinesRemoved,
            IsBinary = f.IsBinary,
        }).ToList();

        // Truncate diagnostic log to last 200 lines for dashboard display
        const int maxLogLines = 200;
        var log = c.Execution.Log;
        if (log.Count > maxLogLines)
            log = log.Skip(log.Count - maxLogLines).ToList();

        return new CandidateExecutionSummary
        {
            StrategyId = c.StrategyId,
            Survived = c.Survived,
            FailedGate = c.FailedGate,
            FailureDetail = c.FailureDetail,
            JudgeReasoning = c.Score?.Reasoning,
            JudgeSkippedReason = c.Survived ? judgeSkippedReason : null,
            FilesChanged = fileSummaries,
            TotalLinesAdded = fileSummaries.Sum(f => f.LinesAdded),
            TotalLinesRemoved = fileSummaries.Sum(f => f.LinesRemoved),
            PatchSizeBytes = c.PatchSizeBytes,
            ElapsedSec = c.Execution.Elapsed.TotalSeconds,
            TokensUsed = c.Execution.TokensUsed,
            DiagnosticLog = log,
            Scores = c.Score is not null ? new ScoreSummary
            {
                AcceptanceCriteria = c.Score.AcceptanceCriteriaScore,
                Design = c.Score.DesignScore,
                Readability = c.Score.ReadabilityScore,
                Visuals = c.Score.VisualsScore,
            } : null,
        };
    }
}

public record OrchestrationOutcome(TaskContext Task, EvaluationResult Evaluation)
{
    public bool HasWinner => Evaluation.Winner is not null;

    public static OrchestrationOutcome Empty(TaskContext task) => new(task, new EvaluationResult
    {
        Candidates = Array.Empty<CandidateResult>(),
        Winner = null,
        TieBreakReason = "no-strategies-enabled",
        EvaluationElapsed = TimeSpan.Zero,
    });
}

/// <summary>
/// Abstraction for emitting lifecycle events (SignalR-bound in the Runner; no-op in tests).
/// Implementations MUST NOT throw on unknown event types.
/// </summary>
public interface IStrategyEventSink
{
    Task EmitAsync(string eventName, object payload, CancellationToken ct);
}

public sealed class NullStrategyEventSink : IStrategyEventSink
{
    public static readonly NullStrategyEventSink Instance = new();
    public Task EmitAsync(string eventName, object payload, CancellationToken ct) => Task.CompletedTask;
}
