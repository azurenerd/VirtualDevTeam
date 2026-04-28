using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Frameworks;
using AgentSquad.Core.Strategies.Contracts;

namespace AgentSquad.Core.Strategies;

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
        RevisionFeedbackGenerator? revisionFeedback = null)
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
    }

    /// <summary>All known framework/strategy IDs (built-in + external adapters).</summary>
    public IReadOnlyCollection<string> AllKnownIds =>
        _strategies.Keys.Concat(_externalAdapters.Keys).ToList().AsReadOnly();

    /// <summary>Run all enabled strategies for a task and evaluate. Does not apply the winner.</summary>
    public async Task<OrchestrationOutcome> RunCandidatesAsync(TaskContext task, CancellationToken ct)
    {
        var cfg = _cfg.CurrentValue;
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

        // Launch each strategy in its own worktree, bounded by the global gate.
        var runTasks = enabled.Select(id => RunOneAsync(task, id, cfg, ct)).ToList();
        var outputs = await Task.WhenAll(runTasks);

        // Evaluate survivors.
        var evalInput = outputs
            .Where(o => o.exec is not null)
            .Select(o => (o.exec!, o.patch))
            .ToList();

        EvaluationResult evalResult;
        var revCfg = cfg.RevisionRound;

        if (revCfg.Enabled && evalInput.Count > 1)
        {
            // ── Revision Round: gates-only → judge with feedback → revise → final judge ──
            evalResult = await RunWithRevisionAsync(task, evalInput, outputs, enabled, cfg, ct);
        }
        else
        {
            // ── Standard path (unchanged) ──
            evalResult = await _evaluator.EvaluateAsync(task, evalInput, ct);
        }

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
                c.Survived ? judgeSkippedReason : null), ct);
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
                    screenshotBase64), ct);
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
            await _events.EmitAsync(StrategyEvents.WinnerSelected, new WinnerSelectedEvent(
                task.RunId, task.TaskId, evalResult.Winner.StrategyId,
                evalResult.TieBreakReason ?? "",
                evalResult.EvaluationElapsed.TotalSeconds), ct);
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

        LogOrchestrationSummary(task.TaskId, runSw, evalResult);

        return new OrchestrationOutcome(task, evalResult);
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
        // Step 1: Run gates + screenshots only (no LLM judge, no winner pick)
        var initialEval = await _evaluator.EvaluateAsync(task, evalInput, ct);
        var survivors = initialEval.Candidates.Where(c => c.Survived).ToList();

        if (survivors.Count <= 1)
        {
            _logger.LogInformation(
                "Revision round skipped for task {Task}: only {Count} survivor(s)",
                task.TaskId, survivors.Count);
            return initialEval;
        }

        // Step 2: Judge scores survivors with feedback (single call)
        var ecfg = cfg.Evaluator;
        var sanitized = survivors.ToDictionary(
            c => c.StrategyId,
            c => JudgeInputSanitizer.SanitizePatch(c.Patch, ecfg.MaxJudgePatchChars));

        JudgeResult? judgeResult = null;
        if (_evaluator.Judge is not null)
        {
            judgeResult = await _evaluator.Judge.ScoreAsync(new JudgeInput
            {
                TaskId = task.TaskId,
                TaskTitle = task.TaskTitle,
                TaskDescription = task.TaskDescription,
                CandidatePatches = sanitized,
                MaxPatchChars = ecfg.MaxJudgePatchChars,
            }, ct);
        }

        // Step 3: Build RevisionContext per survivor + generate rubber-duck feedback
        var revisionContexts = new Dictionary<string, RevisionContext>(StringComparer.Ordinal);
        foreach (var survivor in survivors)
        {
            var score = judgeResult?.Scores.TryGetValue(survivor.StrategyId, out var s) == true ? s : null;
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

            // Emit initial-scored event
            var screenshotBase64 = survivor.ScreenshotBytes is { Length: > 0 }
                ? Convert.ToBase64String(survivor.ScreenshotBytes) : null;
            await _events.EmitAsync(StrategyEvents.CandidateInitialScored,
                new CandidateInitialScoredEvent(
                    task.RunId, task.TaskId, survivor.StrategyId,
                    score.AcceptanceCriteriaScore, score.DesignScore, score.ReadabilityScore,
                    score.VisualsScore,
                    score.Feedback,
                    screenshotBase64), ct);

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
                RubberDuckFeedback = rubberDuck,
                OriginalPatch = survivor.Patch,
            };
        }

        if (revisionContexts.Count == 0)
        {
            _logger.LogInformation("No revision contexts built — returning initial evaluation");
            return initialEval;
        }

        // Step 4: Run revision in fresh worktrees (shorter timeout)
        var revisionTimeout = TimeSpan.FromSeconds(cfg.RevisionRound.MaxRevisionSeconds);
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

        // Step 6: Best-of-two — for each candidate, keep the better total score
        var bestResults = new List<CandidateResult>();
        foreach (var finalCandidate in finalEval.Candidates)
        {
            var initialCandidate = initialEval.Candidates
                .FirstOrDefault(c => c.StrategyId.Equals(finalCandidate.StrategyId, StringComparison.OrdinalIgnoreCase));

            if (initialCandidate?.Score is not null && finalCandidate.Score is not null)
            {
                var initialScore = judgeResult?.Scores.TryGetValue(finalCandidate.StrategyId, out var iScore) == true ? iScore : null;
                var initialTotal = (initialScore?.AcceptanceCriteriaScore ?? 0) + (initialScore?.DesignScore ?? 0) + (initialScore?.ReadabilityScore ?? 0);
                var finalTotal = finalCandidate.Score.AcceptanceCriteriaScore + finalCandidate.Score.DesignScore + finalCandidate.Score.ReadabilityScore;

                if (initialTotal > finalTotal)
                {
                    _logger.LogInformation(
                        "Revision worsened {Strategy} ({Initial} → {Final}); keeping initial scores",
                        finalCandidate.StrategyId, initialTotal, finalTotal);
                    bestResults.Add(finalCandidate with { Score = initialCandidate.Score with
                    {
                        AcceptanceCriteriaScore = initialScore!.AcceptanceCriteriaScore,
                        DesignScore = initialScore.DesignScore,
                        ReadabilityScore = initialScore.ReadabilityScore,
                    }});
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
                .OrderByDescending(c => c.Score!.AcceptanceCriteriaScore)
                .ThenByDescending(c => c.Score!.DesignScore)
                .ThenByDescending(c => c.Score!.ReadabilityScore)
                .ThenByDescending(c => c.Score!.VisualsScore ?? -1)
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

            // Apply the original patch so the revision starts from initial code
            var applyOk = await _worktree.ApplyPatchAsync(handle.Path, revCtx.OriginalPatch, ct);
            if (!applyOk)
            {
                _logger.LogWarning("Failed to apply initial patch for revision of {Strategy}", strategyId);
                return (new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = "revision-patch-apply-failed",
                    Elapsed = sw.Elapsed,
                }, "");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var invocation = new StrategyInvocation
            {
                Task = task,
                WorktreePath = handle.Path,
                StrategyId = strategyId,
                Timeout = timeout,
                Revision = revCtx,
            };

            StrategyExecutionResult exec;
            string patch = "";
            try
            {
                if (strategy is not null)
                {
                    exec = await strategy.ExecuteAsync(invocation, timeoutCts.Token);
                }
                else
                {
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

    private void LogOrchestrationSummary(string taskId, Stopwatch runSw, EvaluationResult evalResult)
    {
        var winnerId = evalResult.Winner?.StrategyId ?? "<none>";
        var candidateTimes = string.Join(", ",
            evalResult.Candidates.Select(c => $"{c.StrategyId}={c.Execution.Elapsed.TotalSeconds:F1}s"));
        _logger.LogInformation(
            "Strategy orchestration wall-clock for task {Task}: {Total:F1}s (winner={Winner}); candidates: {Candidates}",
            taskId, runSw.Elapsed.TotalSeconds, winnerId, candidateTimes);
    }

    private async Task<(StrategyExecutionResult? exec, string patch)> RunOneAsync(
        TaskContext task, string strategyId, StrategyFrameworkConfig cfg, CancellationToken ct)
    {
        var isExternal = _externalAdapters.ContainsKey(strategyId);
        var timeout = cfg.Timeouts.GetTimeout(strategyId);

        // External adapters: pre-flight lifecycle check (readiness).
        if (isExternal && _externalAdapters[strategyId] is IFrameworkLifecycle lifecycle)
        {
            try
            {
                var readiness = await lifecycle.CheckReadinessAsync(ct);
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
                            false, failExec.FailureReason, 0, null), ct);
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
                        false, failExec.FailureReason, 0, null), ct);
                return (failExec, "");
            }
        }

        var strategy = isExternal ? null : _strategies[strategyId];
        var adapter = isExternal ? _externalAdapters[strategyId] : null;

        await _events.EmitAsync(StrategyEvents.CandidateStarted,
            new CandidateStartedEvent(task.RunId, task.TaskId, strategyId, DateTimeOffset.UtcNow), ct);

        WorktreeHandle? handle = null;
        var sw = Stopwatch.StartNew();
        try
        {
            // Global process-count throttling used to live here (StrategyConcurrencyGate.Acquire)
            // but moved into CopilotCliProcessManager (p3-semaphore-split): the gate now sits
            // AFTER each per-pool semaphore, so agentic bursts can't steal every permit and
            // starve baseline. Worktree creation and the evaluator don't spawn copilot
            // processes, so they don't need the gate.
            try
            {
                handle = await _worktree.CreateAsync(
                    task.AgentRepoPath, cfg.CandidateDirectoryName,
                    task.TaskId, strategyId, task.BaseSha, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Worktree setup failure (e.g. locked candidate dir, disk full, pruned repo).
                // Without this catch the method faulted before emitting CandidateCompleted,
                // leaving the dashboard state store with the candidate stuck in "Running"
                // forever and propagating the exception through Task.WhenAll in
                // RunCandidatesAsync — aborting the entire run. val-e2e exposed this path
                // when a prior candidate's cleanup left locked files in the parent dir.
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
                    failExec.Elapsed.TotalSeconds, failExec.TokensUsed), ct);
                return (failExec, "");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var invocation = new StrategyInvocation
            {
                Task = task,
                WorktreePath = handle.Path,
                StrategyId = strategyId,
                Timeout = timeout,
            };

            // Activity sink shared by both built-in strategies and external adapters.
            var activitySink = new Progress<FrameworkActivityEvent>(activity =>
            {
                var activityEntry = new ActivityEntry(
                    DateTimeOffset.UtcNow, activity.Category, activity.Message, activity.Metadata);
                _ = _events.EmitAsync(StrategyEvents.CandidateActivity,
                    new CandidateActivityEvent(task.RunId, task.TaskId, strategyId, activityEntry),
                    CancellationToken.None);
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
                    patch = await _worktree.ExtractPatchAsync(handle.Path, handle.BaseSha, ct);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                exec = new StrategyExecutionResult
                {
                    StrategyId = strategyId,
                    Succeeded = false,
                    FailureReason = $"timeout after {timeout.TotalSeconds}s",
                    Elapsed = sw.Elapsed,
                };
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
                exec.Elapsed.TotalSeconds, exec.TokensUsed), ct);

            // Phase 5: charge tokens to the per-run budget. Trips the breaker once
            // the configured cap is exceeded; subsequent tasks in the run see
            // IsExhausted=true and the sampling policy narrows to baseline-only.
            if (_budget is not null && exec.TokensUsed is > 0)
                _budget.Charge(task.RunId, exec.TokensUsed.Value);

            // Phase 6: per-strategy cost attribution. Uses the model reported
            // by the candidate; falls back to a generic label if unknown so we
            // still get a row for the strategy.
            if (_usage is not null && exec.TokensUsed is > 0)
                _usage.RecordStrategyTokens(strategyId, "cli-estimated", exec.TokensUsed.Value);

            return (exec, patch);
        }
        finally
        {
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
