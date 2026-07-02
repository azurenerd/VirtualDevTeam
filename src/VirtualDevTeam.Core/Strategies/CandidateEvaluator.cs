using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies.Contracts;
using VirtualDevTeam.Core.Strategies.Preview;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Runs hard gates against a candidate patch inside a scratch worktree and chooses a
/// winner. LLM scoring is delegated to an optional <see cref="ILlmJudge"/> (null-safe).
/// Hard gates:
///  - Gate1 OutputProduced: non-empty patch.
///  - Gate2 Build: apply patch to scratch worktree, then `dotnet build` success.
///    (Also rejects patches that touch the reserved evaluator path or escape the worktree.)
///  - Gate3 AppStarts: stub (returns pass when not a web task).
///  - Gate4 EvaluatorTests: stub (returns pass when no evaluator suite configured).
/// Tiebreakers on scoring ties: fewer tokens, then faster time, then stable alphabetical id.
/// </summary>
public class CandidateEvaluator
{
    private readonly ILogger<CandidateEvaluator> _logger;
    private readonly GitWorktreeManager _worktree;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly ILlmJudge? _judge;
    private readonly IVisualJudge? _visualJudge;
    private readonly PlaywrightRunner? _screenshotRunner;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _appCfg;
    private readonly IStrategyEventSink _events;
    private readonly VideoTrimmer? _videoTrimmer;
    private readonly ContactSheetGenerator? _contactSheet;
    private readonly CandidatePreviewService? _previewService;
    private readonly InteractionPlanGenerator? _interactionPlanGen;

    /// <summary>Exposed for the orchestrator's revision round to call the judge directly.</summary>
    public ILlmJudge? Judge => _judge;

    public CandidateEvaluator(
        ILogger<CandidateEvaluator> logger,
        GitWorktreeManager worktree,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        ILlmJudge? judge = null,
        IVisualJudge? visualJudge = null,
        PlaywrightRunner? screenshotRunner = null,
        IOptionsMonitor<VirtualDevTeamConfig>? appCfg = null,
        IStrategyEventSink? events = null,
        VideoTrimmer? videoTrimmer = null,
        ContactSheetGenerator? contactSheet = null,
        CandidatePreviewService? previewService = null,
        InteractionPlanGenerator? interactionPlanGen = null)
    {
        _logger = logger;
        _worktree = worktree;
        _cfg = cfg;
        _judge = judge;
        _visualJudge = visualJudge;
        _screenshotRunner = screenshotRunner;
        _appCfg = appCfg;
        _events = events ?? NullStrategyEventSink.Instance;
        _videoTrimmer = videoTrimmer;
        _contactSheet = contactSheet;
        _previewService = previewService;
        _interactionPlanGen = interactionPlanGen;

        var ws = _appCfg?.CurrentValue?.Workspace;
        _logger.LogInformation(
            "CandidateEvaluator constructed: screenshotRunner={HasRunner}, appCfg={HasCfg}, " +
            "workspace={HasWs}, captureScreenshots={Capture}, videoTrimmer={HasTrimmer}, previewService={HasPreview}",
            _screenshotRunner is not null, _appCfg is not null,
            ws is not null, ws?.CaptureScreenshots, _videoTrimmer is not null,
            _previewService is not null);
    }

    public async Task<EvaluationResult> EvaluateAsync(
        TaskContext task,
        IReadOnlyList<(StrategyExecutionResult exec, string patch)> strategyOutputs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<CandidateResult>(strategyOutputs.Count);
        var cfg = _cfg.CurrentValue;
        var cachedPlaywrightReady = _screenshotRunner?.IsReady == true;
        var captureScreenshotsEnabled = _appCfg?.CurrentValue?.Workspace?.CaptureScreenshots == true;

        // Worktree handles are kept alive for the CLI-native judge to browse.
        // Disposed in the finally block after judging completes.
        var worktreeHandles = new List<(string strategyId, WorktreeHandle handle)>();
        string? winnerStrategyId = null; // hoisted for finally-block access

        try
        {
            // Run gate evaluation + media capture for all candidates in PARALLEL.
            // Each candidate gets its own worktree — no shared state between them.
            // Worktree handles are thread-safe to collect (ConcurrentBag) and disposed after judging.
            var evalTasks = strategyOutputs.Select(async item =>
            {
                var (exec, patch) = item;
                var (res, handle) = await RunGatesAsync(task, exec, patch, cfg, cachedPlaywrightReady, captureScreenshotsEnabled, ct);

                // Early screenshot emission: send CandidateEvaluated immediately per-candidate
                // so the dashboard can display the screenshot before all candidates finish.
                var earlyScreenshot = res.ScreenshotBytes is { Length: > 0 }
                    ? Convert.ToBase64String(res.ScreenshotBytes)
                    : null;
                await _events.EmitAsync(StrategyEvents.CandidateEvaluated, new CandidateEvaluatedEvent(
                    task.RunId, task.TaskId, res.StrategyId,
                    res.Survived, res.FailedGate, res.FailureDetail,
                    earlyScreenshot,
                    null, // judgeSkippedReason not yet known — will be re-emitted with scores later
                    res.VideoPath,
                    res.ScreenshotPaths,
                    res.AnimatedGifPath,
                    res.PreviewSource,
                    res.IncludedAssetPaths,
                    res.SecondaryPreviewBase64,
                    res.SecondaryAssetPaths,
                    res.SecondaryPreviewSource,
                    res.CaptureMetrics,
                    res.PageAnalysis,
                    res.AppBaseUrl), ct);

                return (res, handle, exec.StrategyId);
            }).ToList();

            var evalResults = await Task.WhenAll(evalTasks);

            foreach (var (res, handle, strategyId) in evalResults)
            {
                results.Add(res);
                if (handle is not null)
                    worktreeHandles.Add((strategyId, handle));
            }

            var survivors = results.Where(r => r.Survived).ToList();
            CandidateResult? winner = null;
            string? tieBreak = null;

            // Build worktree-path and build-context maps for CLI-native judge.
            var worktreePaths = worktreeHandles.ToDictionary(
                h => h.strategyId, h => h.handle.Path, StringComparer.Ordinal);
            var buildContextMap = results
                .Where(r => r.Survived)
                .ToDictionary(r => r.StrategyId, r =>
                {
                    var ctx = "Build: succeeded.";
                    // Always include runtime behavior context (positive signals like
                    // "no errors detected" help the judge as much as error reports)
                    if (r.InteractionContext is not null)
                        ctx += "\n\n" + r.InteractionContext.ToPromptSummary();
                    return ctx;
                }, StringComparer.Ordinal);

            if (survivors.Count == 0)
            {
                // No winner — evaluator caller will fall through to baseline re-run or blocker.
            }
            else if (survivors.Count == 1)
            {
                winner = survivors[0];
                tieBreak = "sole-survivor";

                // 2026-05-12 sole-survivor binary-quality safeguard (rd-4 critical):
                // The original "Pillow beats real" incident scenario was a sole-survivor
                // case (Squad failed a hard gate, CLI inherited the win despite producing
                // 4 fake 800-byte stub PNGs). The multi-survivor binary-quality gate
                // doesn't help here. Inspect the sole survivor's binaries and DECLINE
                // the win if score is < 30 (clear fake-dominance). Caller falls through
                // to baseline rerun rather than shipping garbage.
                // 2026-05-15 fix: only reject when there are actual image deliverables
                // (real + fake > 0). Neutral-only results (build artifacts, DLLs) should
                // NOT trigger rejection — they're not art tasks.
                // 2026-05-26 fix: tighten further — only reject when FakeCount > 0.
                // The original gate fired on (RealCount + FakeCount) > 0 which false-
                // positived on projects with legitimate small images (favicons, logos)
                // that dilute the score via neutral binaries. The "Pillow beats real"
                // incident always involves actual fake stubs (FakeCount > 0).
                if (worktreePaths.TryGetValue(winner.StrategyId, out var wtSole))
                {
                    var qSole = CandidateBinaryQualityCheck.Inspect(wtSole, _logger);
                    if (qSole is not null && qSole.FakeCount > 0 && qSole.Score < 30)
                    {
                        _logger.LogWarning(
                            "Sole-survivor candidate {Strategy} for task {Task} REJECTED by binary-quality gate: score {Score}/100 ({Real} real, {Fake} fake, {Neutral} neutral). Returning no winner so caller can re-run baseline.",
                            winner.StrategyId, task.TaskId, qSole.Score, qSole.RealCount, qSole.FakeCount, qSole.NeutralCount);
                        winner = null;
                        tieBreak = "sole-survivor-rejected-binary-quality";
                    }
                    else if (qSole is not null)
                    {
                        _logger.LogInformation(
                            "Sole-survivor candidate {Strategy} for task {Task}: binary-quality {Quality}",
                            winner.StrategyId, task.TaskId, qSole);
                    }
                }

                // Still score the sole survivor for experiment tracking and dashboard display
                if (winner is not null && _judge is not null)
                {
                    var ecfg = _cfg.CurrentValue;
                    var sanitized = survivors.ToDictionary(
                        c => c.StrategyId,
                        c => JudgeInputSanitizer.SanitizePatch(c.Patch, ecfg.Evaluator.MaxJudgePatchChars));
                    var judgeResult = await ScoreJudgeWithTimeoutAsync(new JudgeInput
                    {
                        TaskId = task.TaskId,
                        TaskTitle = task.TaskTitle,
                        TaskDescription = task.TaskDescription,
                        CandidatePatches = sanitized,
                        MaxPatchChars = ecfg.Evaluator.MaxJudgePatchChars,
                        CandidateWorktreePaths = worktreePaths.Count > 0 ? worktreePaths : null,
                        CandidateBuildContext = buildContextMap.Count > 0 ? buildContextMap : null,
                    },
                    TimeSpan.FromMinutes(ecfg.Evaluator.JudgeScoringTimeoutMinutes),
                    task.TaskId, "sole-survivor", ct);

                    if (judgeResult.Scores.TryGetValue(winner.StrategyId, out var score))
                    {
                        winner = winner with { Score = score };
                        results[results.IndexOf(survivors[0])] = winner;
                    }
                }
            }
            else if (_judge is not null)
            {
                // Batch-score survivors, then rank by AC -> Design -> Readability -> tokens -> time -> id.
                var ecfg = _cfg.CurrentValue;
                var sanitized = survivors.ToDictionary(
                    c => c.StrategyId,
                    c => JudgeInputSanitizer.SanitizePatch(c.Patch, ecfg.Evaluator.MaxJudgePatchChars));
                var judgeResult = await ScoreJudgeWithTimeoutAsync(new JudgeInput
                {
                    TaskId = task.TaskId,
                    TaskTitle = task.TaskTitle,
                    TaskDescription = task.TaskDescription,
                    CandidatePatches = sanitized,
                    MaxPatchChars = ecfg.Evaluator.MaxJudgePatchChars,
                    CandidateWorktreePaths = worktreePaths.Count > 0 ? worktreePaths : null,
                    CandidateBuildContext = buildContextMap.Count > 0 ? buildContextMap : null,
                },
                TimeSpan.FromMinutes(ecfg.Evaluator.JudgeScoringTimeoutMinutes),
                task.TaskId, "batch-scoring", ct);

                var scored = survivors.Select(c => judgeResult.Scores.TryGetValue(c.StrategyId, out var s)
                    ? c with { Score = s }
                    : c).ToList();

                // ── Visual scoring — run BEFORE winner selection so VisualsScore
                // participates in the ranking. Previously ran after winner was locked,
                // making the ThenByDescending(VisualsScore) sorts dead code. ──
                scored = await ApplyVisualScoresAsync(task, scored, ct);

                // 2026-05-12 binary-quality gate: when a candidate produced binary deliverables
                // (PNGs/JPGs), inspect them. A candidate with high "binary-quality" (real
                // gpt-image content) wins over a candidate with low binary-quality (Pillow stub
                // primitives) regardless of LLM judge text scores. The judge cannot distinguish
                // a 1.6MB real PNG from a 60KB Pillow stub from patch text alone.
                var binaryQuality = new Dictionary<string, BinaryQualityResult>(StringComparer.Ordinal);
                foreach (var c in scored)
                {
                    if (!worktreePaths.TryGetValue(c.StrategyId, out var wt)) continue;
                    var q = CandidateBinaryQualityCheck.Inspect(wt, _logger);
                    if (q is not null)
                    {
                        binaryQuality[c.StrategyId] = q;
                        _logger.LogInformation(
                            "Candidate {Strategy} for task {Task}: {Quality}",
                            c.StrategyId, task.TaskId, q);
                    }
                }
                // If at least two candidates have binary deliverables AND their quality scores
                // differ by ≥30 points, the binary-quality gate dominates the LLM ranking.
                // 2026-05-26 fix: only activate when at least one candidate has fake evidence
                // (FakeCount > 0). Neutral-only score differences are scoring artifacts from
                // legitimate project images, not evidence of stub/fake generation.
                var withBinaries = binaryQuality.Where(kv => kv.Value.TotalCount > 0).ToList();
                bool anyFakeEvidence = withBinaries.Any(kv => kv.Value.FakeCount > 0);
                bool binaryGateActivated = anyFakeEvidence
                    && withBinaries.Count >= 2
                    && (withBinaries.Max(kv => kv.Value.Score) - withBinaries.Min(kv => kv.Value.Score) >= 30);

                List<CandidateResult> ordered;
                if (binaryGateActivated)
                {
                    ordered = scored
                        .OrderByDescending(c => binaryQuality.TryGetValue(c.StrategyId, out var q) ? q.Score : -1)
                        .ThenByDescending(c =>
                            (c.Score?.AcceptanceCriteriaScore ?? 0)
                            + (c.Score?.DesignScore ?? 0)
                            + (c.Score?.ReadabilityScore ?? 0)
                            + (c.Score?.VisualsScore ?? 0))
                        .ThenBy(c => c.Execution.TokensUsed ?? long.MaxValue)
                        .ThenBy(c => c.Execution.Elapsed)
                        .ThenBy(c => c.StrategyId, StringComparer.Ordinal)
                        .ToList();
                    tieBreak = "binary-quality+llm-rank";
                    _logger.LogInformation(
                        "Binary-quality gate ACTIVATED for task {Task} — winner determined by binary deliverable quality (range: {Min}..{Max})",
                        task.TaskId,
                        withBinaries.Min(kv => kv.Value.Score),
                        withBinaries.Max(kv => kv.Value.Score));
                }
                else
                {
                    // Sort by total combined score (AC + Design + Readability + Visuals).
                    // Visual score adds to the total rather than being a tiebreaker,
                    // so a candidate with an error page (visuals=1) loses to one with
                    // a working UI (visuals=8) even if code scores are close.
                    ordered = scored
                        .OrderByDescending(c =>
                            (c.Score?.AcceptanceCriteriaScore ?? 0)
                            + (c.Score?.DesignScore ?? 0)
                            + (c.Score?.ReadabilityScore ?? 0)
                            + (c.Score?.VisualsScore ?? 0))
                        .ThenByDescending(c => c.Score?.AcceptanceCriteriaScore ?? -1)
                        .ThenBy(c => c.Execution.TokensUsed ?? long.MaxValue)
                        .ThenBy(c => c.Execution.Elapsed)
                        .ThenBy(c => c.StrategyId, StringComparer.Ordinal)
                        .ToList();
                    tieBreak = judgeResult.IsFallback ? "judge-fallback-tokens-time" : "llm-total-score";
                }
                winner = ordered[0];
                // Replace survivors in results with their scored versions for the final record.
                for (int i = 0; i < results.Count; i++)
                {
                    var replacement = ordered.FirstOrDefault(s => s.StrategyId == results[i].StrategyId);
                    if (replacement is not null) results[i] = replacement;
                }
            }
            else
            {
                // No judge configured -> deterministic tiebreak only.
                var ordered = survivors
                    .OrderBy(c => c.Execution.TokensUsed ?? long.MaxValue)
                    .ThenBy(c => c.Execution.Elapsed)
                    .ThenBy(c => c.StrategyId, StringComparer.Ordinal)
                    .ToList();
                winner = ordered[0];
                tieBreak = "no-judge-tokens-then-time";
            }

            sw.Stop();

            // Re-resolve winner from results to ensure it points at the scored instance
            // (ApplyVisualScoresAsync creates new CandidateResult instances).
            if (winner is not null)
            {
                winner = results.FirstOrDefault(r => r.StrategyId == winner.StrategyId) ?? winner;
            }

            // Re-emit CandidateEvaluated with final scores + screenshots for the dashboard.
            // The early emit (line ~101) may have had null ScreenshotBytes if capture was still
            // in progress. Now that scoring is complete, re-emit so the dashboard gets the full data.
            foreach (var r in results.Where(r => r.Survived))
            {
                var screenshotB64 = r.ScreenshotBytes is { Length: > 0 }
                    ? Convert.ToBase64String(r.ScreenshotBytes)
                    : null;
                await _events.EmitAsync(StrategyEvents.CandidateEvaluated, new CandidateEvaluatedEvent(
                    task.RunId, task.TaskId, r.StrategyId,
                    r.Survived, r.FailedGate, r.FailureDetail,
                    screenshotB64,
                    null, // judgeSkippedReason
                    r.VideoPath,
                    r.ScreenshotPaths,
                    r.AnimatedGifPath,
                    r.PreviewSource,
                    r.IncludedAssetPaths,
                    r.SecondaryPreviewBase64,
                    r.SecondaryAssetPaths,
                    r.SecondaryPreviewSource,
                    r.CaptureMetrics,
                    r.PageAnalysis,
                    r.AppBaseUrl), ct);
            }

            // Identify the winner's worktree handle (if any) — we transfer ownership to the caller
            // so they can copy files directly instead of relying on `git apply`.
            string? winnerWorktreePath = null;
            IAsyncDisposable? winnerWorktreeHandle = null;
            if (winner is not null)
            {
                winnerStrategyId = winner.StrategyId;
                var winnerEntry = worktreeHandles.FirstOrDefault(h =>
                    string.Equals(h.strategyId, winner.StrategyId, StringComparison.Ordinal));
                if (winnerEntry.handle is not null)
                {
                    winnerWorktreePath = winnerEntry.handle.Path;
                    winnerWorktreeHandle = winnerEntry.handle;
                }
            }

            return new EvaluationResult
            {
                Candidates = results,
                Winner = winner,
                TieBreakReason = tieBreak,
                EvaluationElapsed = sw.Elapsed,
                WinnerWorktreePath = winnerWorktreePath,
                WinnerWorktreeHandle = winnerWorktreeHandle,
            };
        }
        finally
        {
            // Dispose all worktree handles EXCEPT the winner's (ownership transferred to caller).
            foreach (var (strategyId, handle) in worktreeHandles)
            {
                if (winnerStrategyId is not null
                    && string.Equals(strategyId, winnerStrategyId, StringComparison.Ordinal))
                    continue; // caller owns this handle now
                try { await handle.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to dispose eval worktree handle for {Strategy}", strategyId);
                }
            }
        }
    }

    private async Task<(CandidateResult result, WorktreeHandle? handle)> RunGatesAsync(
        TaskContext task,
        StrategyExecutionResult exec,
        string patch,
        StrategyFrameworkConfig cfg,
        bool cachedPlaywrightReady,
        bool captureScreenshotsEnabled,
        CancellationToken ct)
    {
        // Gate1: OutputProduced
        if (!exec.Succeeded)
        {
            return (Fail(exec, patch, "strategy-failed", exec.FailureReason), null);
        }
        if (string.IsNullOrWhiteSpace(patch))
        {
            // For integration tasks (T-FINAL), empty patch = clean integration success.
            // Strategies are explicitly told "produce NO code changes" if everything integrates cleanly.
            if (task.TaskId.Equals("T-FINAL", StringComparison.OrdinalIgnoreCase))
            {
                // Score the verification work deterministically from the activity log.
                // A candidate that verified builds, tests, and scenarios deserves credit
                // even though it produced no code changes.
                var verificationScore = ScoreVerificationEvidence(exec);
                _logger.LogInformation(
                    "T-FINAL empty patch (verified-clean) for {Strategy}: verification score {Score}/40 " +
                    "(builds={Builds}, tests={Tests}, scenarios={Scenarios})",
                    exec.StrategyId, verificationScore.Total,
                    verificationScore.BuildsVerified, verificationScore.TestsVerified,
                    verificationScore.ScenariosVerified);

                return (new CandidateResult
                {
                    StrategyId = exec.StrategyId,
                    Survived = true,
                    Patch = patch ?? "",
                    PatchSizeBytes = 0,
                    Execution = exec,
                    Score = verificationScore.Total > 0 ? new CandidateScore
                    {
                        AcceptanceCriteriaScore = verificationScore.AcScore,
                        DesignScore = verificationScore.DesignScore,
                        ReadabilityScore = verificationScore.ReadabilityScore,
                        VisualsScore = null, // no code changes = no visual changes to score
                        Reasoning = $"Verified-clean integration: {verificationScore.Summary}",
                        Feedback = "",
                    } : null,
                }, null);
            }
            // No-op acknowledged: the strategy's agent inspected the worktree and
            // reported the task is already complete (prior merged PR / earlier wave).
            // Distinct failure reason so the dashboard shows "no-op (already done)"
            // instead of the generic "empty patch" — the user otherwise sees the
            // candidate as "looks like it never ran".
            if (exec.NoOpAcknowledged)
            {
                return (Fail(exec, patch, "no-op",
                    "task already complete — agent inspected the worktree and reported no work needed"), null);
            }
            return (Fail(exec, patch, "gate1-output", "empty patch"), null);
        }

        // Path safety / reserved-path / .git guard.
        var pathIssue = GitWorktreeManager.ValidatePatchPaths(patch, cfg.Evaluator.ReservedPathPrefix);
        if (pathIssue is not null)
        {
            return (Fail(exec, patch, "gate2-build", $"rejected-path: {pathIssue}"), null);
        }

        // Gate2: Build — apply to a scratch worktree and run dotnet build.
        // Handle is NOT disposed here — caller (EvaluateAsync) manages lifetime so
        // the CLI-native judge can browse the worktree after gates pass.
        var scratch = await _worktree.CreateAsync(
            task.AgentRepoPath, cfg.CandidateDirectoryName + "-eval",
            task.TaskId, exec.StrategyId + "-eval", task.BaseSha, ct);

        // Live progress tracker — created here (not later) so the dashboard's
        // Media strip renders the moment evaluation starts, not minutes later
        // when CaptureAppInteractionAsync emits its first event. The tracker is
        // also reused by PlaywrightRunner via the IMediaCaptureProgressSink param.
        var progressTracker = new MediaCapture.MediaCaptureTracker(
            task.RunId, task.TaskId, exec.StrategyId,
            (eventName, payload) =>
            {
                try { _events.EmitAsync(eventName, payload, ct).GetAwaiter().GetResult(); }
                catch { /* never let progress emission break capture */ }
            });

        progressTracker.StartStep(MediaCapture.MediaCaptureStepId.BuildGate, "applying patch + dotnet build");
        var applyResult = await ApplyPatchAsync(scratch.Path, patch, ct);
        if (!applyResult.ok)
        {
            progressTracker.FailStep(MediaCapture.MediaCaptureStepId.BuildGate, $"apply-failed: {applyResult.detail}");
            progressTracker.AbortRemaining("gate failed — apply-failed");
            await scratch.DisposeAsync();
            return (Fail(exec, patch, "gate2-build", $"apply-failed: {applyResult.detail}"), null);
        }

        var buildOk = await RunBuildAsync(scratch.Path, TimeoutsConfig.ToTimeSpan(cfg.Timeouts.BuildGateSeconds), ct);
        if (!buildOk.ok)
        {
            progressTracker.FailStep(MediaCapture.MediaCaptureStepId.BuildGate, $"build-failed: {buildOk.detail}");
            progressTracker.AbortRemaining("gate failed — build-failed");
            await scratch.DisposeAsync();
            return (Fail(exec, patch, "gate2-build", buildOk.detail), null);
        }
        progressTracker.CompleteStep(MediaCapture.MediaCaptureStepId.BuildGate, "build succeeded");

        // Gate3 / Gate4 are stubs in Phase 1 — they pass unless integrators wire them.
        // (The dashboard still sees the gate:started/completed events.)

        // Capture multi-page interaction screenshots + video (best-effort, never blocks evaluation).
        // Always attempt screenshots regardless of task type — PlaywrightRunner handles non-web
        // apps via static HTML fallback, and console/WPF apps gracefully return null.
        byte[]? screenshotBytes = null;
        IReadOnlyList<string>? screenshotPaths = null;
        string? videoPath = null;
        string? animatedGifPath = null;
        ScreenshotCaptureSummary? captureMetrics = null;
        PageAnalysis? pageAnalysis = null;
        string? appBaseUrl = null;
        // Default to Playwright so any path that doesn't go through the producer chain
        // (legacy 3-arg ctor in tests, NoVisualContent fall-through to inline capture)
        // continues to render the existing 📷 badge — no UI regression.
        CandidatePreviewSource previewSource = CandidatePreviewSource.PlaywrightScreenshot;
        IReadOnlyList<string>? includedAssetPaths = null;

        // T-FINAL optimization: skip media capture only when the patch is empty (clean
        // integration, no fixes needed). When T-FINAL produces real changes (e.g., missing
        // endpoint wiring), screenshots are valuable proof the fix works.
        var skipMediaCapture = task.TaskId.Equals("T-FINAL", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(patch);
        if (skipMediaCapture)
        {
            previewSource = CandidatePreviewSource.NoVisualContent;
            _logger.LogDebug("Skipping media capture for T-FINAL with empty patch — no fixes to verify");
        }
        // ── Mixed-content secondary preview (mixed-content-pr-handling) ──
        // When CandidatePreviewService detects a worktree with BOTH committed assets AND
        // a runnable app (launchSettings present), it returns a primary Playwright preview
        // with a SecondaryPreview attached carrying the asset contact-sheet. We carry that
        // through to the dashboard regardless of whether we end up using the producer's
        // primary or fall through to the richer inline Playwright capture.
        string? secondaryPreviewBase64 = null;
        IReadOnlyList<string>? secondaryAssetPaths = null;
        CandidatePreviewSource? secondaryPreviewSource = null;

        // ── Producer chain (previewproducer-orchestration) ──
        // Try the CandidatePreviewService chain first. If a non-Playwright/non-placeholder
        // producer applies (e.g. ImageAssets, Diagrams), use its result and skip the inline
        // multi-page interaction capture below. Playwright/NoVisualContent results fall through
        // to the existing rich capture path, which is strictly richer than the current
        // PlaywrightCandidatePreviewProducer (video + GIF + multi-screenshot + durable copy).
        //
        // Edge case: if the producer chain throws or the service isn't injected (legacy
        // 3-arg test constructor), the chain is silently skipped and behavior is identical
        // to pre-orchestration code.
        bool producerSuppliedPreview = false;
        if (!skipMediaCapture && _previewService is not null)
        {
            try
            {
                var durableDirForProducer = GetDurableArtifactDir(task.RunId, task.TaskId, exec.StrategyId);
                Directory.CreateDirectory(durableDirForProducer);

                var producerCtx = new CandidatePreviewContext(
                    RunId: task.RunId,
                    TaskId: task.TaskId,
                    StrategyId: exec.StrategyId,
                    CandidateWorktreePath: scratch.Path,
                    ArtifactOutputDir: durableDirForProducer,
                    // PR isn't created yet at evaluation time — pass branch only; PR title/body
                    // are null so producers that key off PR metadata simply decline.
                    PrBranchName: task.PrBranch,
                    PrTitle: null,
                    PrBody: null);

                var producerSw = Stopwatch.StartNew();
                var preview = await _previewService.ProduceAsync(producerCtx, ct);
                producerSw.Stop();
                _logger.LogDebug(
                    "Preview producer chain returned source={Source} producer={Producer} secondary={HasSecondary} in {Ms}ms for {Task}/{Strategy}",
                    preview.Source, preview.SourceProducerId, preview.SecondaryPreview is not null,
                    producerSw.ElapsedMilliseconds, task.TaskId, exec.StrategyId);

                // Always extract a SecondaryPreview if the chain attached one — it represents
                // committed assets that should be shown alongside whatever primary capture we
                // end up with (producer's primary OR inline rich Playwright capture below).
                if (preview.SecondaryPreview is not null)
                {
                    try
                    {
                        var secondaryBytes = Convert.FromBase64String(preview.SecondaryPreview.ScreenshotBase64);
                        if (secondaryBytes.Length > 0)
                        {
                            secondaryPreviewBase64 = preview.SecondaryPreview.ScreenshotBase64;
                            secondaryAssetPaths = preview.SecondaryPreview.IncludedAssetPaths;
                            secondaryPreviewSource = preview.SecondaryPreview.Source;
                            _logger.LogInformation(
                                "Mixed-content secondary preview attached for {Task}/{Strategy}: source={Source}, assets={Count}.",
                                task.TaskId, exec.StrategyId, preview.SecondaryPreview.Source,
                                preview.SecondaryPreview.IncludedAssetPaths?.Count ?? 0);
                        }
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex,
                            "Secondary preview returned malformed base64 for {Task}/{Strategy}; ignoring secondary.",
                            task.TaskId, exec.StrategyId);
                    }
                }

                // Only honour producer output for sources OTHER than Playwright/NoVisualContent.
                // - PlaywrightScreenshot: the current producer is a single-screenshot wrapper;
                //   the inline path below is richer (video, GIF, multi-page). Fall through.
                // - NoVisualContent: no producer applied — fall through to inline capture so
                //   we still attempt Playwright for runnable apps.
                if (preview.Source is CandidatePreviewSource.ImageAssets or CandidatePreviewSource.Diagrams)
                {
                    try
                    {
                        screenshotBytes = Convert.FromBase64String(preview.ScreenshotBase64);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex,
                            "Producer '{Producer}' returned malformed base64 for {Task}/{Strategy}; falling through to Playwright path.",
                            preview.SourceProducerId, task.TaskId, exec.StrategyId);
                    }

                    if (screenshotBytes is { Length: > 0 })
                    {
                        videoPath = preview.VideoPath;
                        animatedGifPath = preview.AnimatedGifPath;
                        previewSource = preview.Source;
                        includedAssetPaths = preview.IncludedAssetPaths;
                        producerSuppliedPreview = true;
                        _logger.LogInformation(
                            "Preview from non-Playwright producer '{Producer}' (source={Source}, assets={AssetCount}) used for {Task}/{Strategy} — skipping inline interaction capture.",
                            preview.SourceProducerId, preview.Source,
                            preview.IncludedAssetPaths?.Count ?? 0,
                            task.TaskId, exec.StrategyId);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Preview producer chain failed for {Task}/{Strategy} — falling through to inline capture.",
                    task.TaskId, exec.StrategyId);
            }
        }

        // 2026-05-12 evening (eval-skip-media-asset-only fix): for asset-only PRs (patches
        // that change ONLY binary asset files — PNG/JPG/MP3/MP4/etc — and zero source code),
        // skip the entire Playwright media capture pipeline. Spinning up the parent app to
        // screenshot a webpage that doesn't even reference the new assets is pure waste
        // (live evidence: PR #1508 hung ~19min on `dotnet restore` for a sprite-only PR
        // before this fix). The artifacts already in the patch ARE the deliverable; the
        // dashboard's CandidateArtifactWatcher already streamed them live.
        if (!skipMediaCapture && !producerSuppliedPreview && IsAssetOnlyPatch(patch))
        {
            _logger.LogInformation(
                "Asset-only patch detected for {Task}/{Strategy} — skipping AppStartup → DependencyRestore → Capture* steps. The committed binary deliverables in the patch ARE the preview.",
                task.TaskId, exec.StrategyId);
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.AppDetection, "asset-only patch — no app to start");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.DependencyRestore, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.AppStartup, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.McpExploration, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.DirectCapture, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.ScreenshotCapture, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.VideoRecording, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.GifGeneration, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.VideoTrimming, "asset-only patch");
            progressTracker.SkipStep(MediaCapture.MediaCaptureStepId.ArtifactStorage, "asset-only patch — artifacts already in patch");
            progressTracker.CompleteStep(MediaCapture.MediaCaptureStepId.Complete, "asset-only — committed assets are the deliverable");
            previewSource = CandidatePreviewSource.ImageAssets;
            producerSuppliedPreview = true; // suppress the Playwright path below
        }

        if (!skipMediaCapture && !producerSuppliedPreview && _screenshotRunner is not null && !cachedPlaywrightReady)
        {
            _logger.LogDebug(
                "Playwright readiness was false at evaluation start ({Reason}) — skipping capture for consistent candidate classification",
                _screenshotRunner.NotReadyReason);
        }
        if (!skipMediaCapture && !producerSuppliedPreview && _screenshotRunner is not null && cachedPlaywrightReady)
        {
            var wsConfig = _appCfg?.CurrentValue?.Workspace;
            if (wsConfig is not null && captureScreenshotsEnabled)
            {
                try
                {
                    // Clone config so interaction capture can mutate AppStartCommand safely
                    var configSnapshot = new WorkspaceConfig
                    {
                        AppStartCommand = wsConfig.AppStartCommand,
                        AppBaseUrl = wsConfig.AppBaseUrl,
                        ScreenshotRenderDelaySeconds = wsConfig.ScreenshotRenderDelaySeconds,
                        BuildCommand = wsConfig.BuildCommand,
                        PlaywrightBrowsersCachePath = wsConfig.PlaywrightBrowsersCachePath,
                        CaptureScreenshots = true,
                        DualCaptureEnabled = wsConfig.DualCaptureEnabled,
                    };

                    // Use the artifact directories under the worktree's test-results
                    var testResultsRoot = Path.Combine(scratch.Path, "test-results");
                    var screenshotDir = Path.Combine(testResultsRoot, "screenshots");
                    var videoDir = Path.Combine(testResultsRoot, "videos");
                    var artifactPrefix = $"framework-{task.TaskId}-{exec.StrategyId}";

                    // Generate a task-specific interaction plan from the diff + task context
                    InteractionPlan? interactionPlan = null;
                    if (_interactionPlanGen is not null && !string.IsNullOrWhiteSpace(patch))
                    {
                        try
                        {
                            var diffAnalysis = DiffAnalyzer.Analyze(patch);

                            // Skip plan generation for non-UI diffs (API-only, backend changes)
                            if (diffAnalysis.NewRoutes.Count > 0 || diffAnalysis.FormElements.Count > 0 ||
                                diffAnalysis.ModifiedComponents.Count > 0 || diffAnalysis.DetectedPattern != UIPatternKind.Unknown)
                            {
                                _logger.LogInformation(
                                    "Diff analysis for {Task}/{Strategy}: pattern={Pattern}, routes={Routes}, forms={Forms}, components={Components}",
                                    task.TaskId, exec.StrategyId, diffAnalysis.DetectedPattern,
                                    diffAnalysis.NewRoutes.Count, diffAnalysis.FormElements.Count,
                                    diffAnalysis.ModifiedComponents.Count);

                                // Wrap in a 30-second timeout so a stalled LLM doesn't block evaluation
                                using var planCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                planCts.CancelAfter(TimeSpan.FromSeconds(30));
                                interactionPlan = await _interactionPlanGen.GenerateAsync(
                                    task.TaskTitle, task.TaskDescription, diffAnalysis, planCts.Token);
                            }
                            else
                            {
                                _logger.LogDebug(
                                    "Skipping interaction plan for {Task}/{Strategy} — no UI signals in diff",
                                    task.TaskId, exec.StrategyId);
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            _logger.LogWarning(
                                "Interaction plan generation timed out for {Task}/{Strategy} — falling back to generic exploration",
                                task.TaskId, exec.StrategyId);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception planEx)
                        {
                            _logger.LogWarning(planEx,
                                "Interaction plan generation failed for {Task}/{Strategy} — falling back to generic exploration",
                                task.TaskId, exec.StrategyId);
                        }
                    }

                    var interactionResult = await CaptureWithTimeoutAsync(
                        () => _screenshotRunner.CaptureAppInteractionAsync(
                            scratch.Path, configSnapshot, videoDir, screenshotDir, artifactPrefix,
                            task.TaskTitle, task.TaskDescription, progressTracker, ct,
                            interactionPlan: interactionPlan),
                        TimeSpan.FromMinutes(_cfg.CurrentValue.Evaluator.MediaCaptureTimeoutMinutes),
                        task.TaskId, exec.StrategyId, "media-capture", ct);

                    if (interactionResult is not null)
                    {
                        // Extract capture metrics to outer scope for CandidateResult
                        captureMetrics = interactionResult.CaptureMetrics;
                        pageAnalysis = interactionResult.PageAnalysis;
                        appBaseUrl = interactionResult.AppBaseUrl;

                        _logger.LogInformation(
                            "CaptureAppInteractionAsync returned: {Count} screenshots, video={Video}, gif={Gif}, appUrl={AppUrl}, screenshotDir={Dir}",
                            interactionResult.Screenshots.Count,
                            interactionResult.VideoWebmPath ?? "NULL",
                            interactionResult.AnimatedGifPath ?? "NULL",
                            appBaseUrl ?? "NULL",
                            screenshotDir);

                        // Capture quality diagnostics
                        if (captureMetrics is not null)
                        {
                            var mcpSrc = captureMetrics.Sources.FirstOrDefault(s => s.Source == ScreenshotCaptureSource.Mcp);
                            var directSrc = captureMetrics.Sources.FirstOrDefault(s => s.Source == ScreenshotCaptureSource.DirectPlaywright);
                            if (mcpSrc?.ArtifactCount == 0 && directSrc?.ArtifactCount > 0)
                                _logger.LogWarning("Capture quality: MCP produced 0 screenshots while Direct got {DirectCount} — MCP exploration may have failed", directSrc.ArtifactCount);
                            if (mcpSrc?.ArtifactCount == 0 && directSrc?.ArtifactCount == 0)
                                _logger.LogWarning("Capture quality: both MCP and Direct produced 0 screenshots — app may not have rendered UI");
                            if (captureMetrics.ExpectedPageCount > 0 && captureMetrics.TotalUniquePages < captureMetrics.ExpectedPageCount)
                                _logger.LogWarning("Capture quality: only {Captured}/{Expected} expected pages captured", captureMetrics.TotalUniquePages, captureMetrics.ExpectedPageCount);
                        }

                        // First screenshot is the landing page (primary thumbnail for backward compat)
                        if (interactionResult.Screenshots.Count > 0)
                        {
                            screenshotBytes = interactionResult.Screenshots[0].Bytes;

                            // Apply blank-screenshot detection — interaction path previously
                            // skipped this, producing white thumbnails on auth/blank pages.
                            var quality = ScreenshotQualityChecker.Check(screenshotBytes);
                            if (quality.IsLikelyBlank)
                            {
                                _logger.LogWarning(
                                    "Interaction screenshot is BLANK ({Size} B): {Reason} — scanning remaining screenshots for non-blank",
                                    quality.FileSize, quality.Reason);
                                screenshotBytes = null;

                                // Dual capture: scan remaining screenshots for a non-blank alternative
                                for (int ssIdx = 1; ssIdx < interactionResult.Screenshots.Count; ssIdx++)
                                {
                                    var altQuality = ScreenshotQualityChecker.Check(interactionResult.Screenshots[ssIdx].Bytes);
                                    if (!altQuality.IsLikelyBlank)
                                    {
                                        screenshotBytes = interactionResult.Screenshots[ssIdx].Bytes;
                                        _logger.LogInformation(
                                            "Found non-blank screenshot at index {Index} (source: {Source})",
                                            ssIdx, interactionResult.Screenshots[ssIdx].CaptureSource);
                                        break;
                                    }
                                }

                                if (screenshotBytes is null)
                                    previewSource = CandidatePreviewSource.CaptureFailed;
                            }
                        }

                        // Collect all screenshot paths (relative to worktree)
                        if (Directory.Exists(screenshotDir))
                        {
                            var paths = Directory.GetFiles(screenshotDir, $"{artifactPrefix}*", SearchOption.AllDirectories)
                                .Select(p => Path.GetRelativePath(scratch.Path, p))
                                .ToList();
                            if (paths.Count > 0)
                                screenshotPaths = paths;
                            else
                                _logger.LogWarning("Screenshot dir exists but no files match prefix '{Prefix}*'. Dir contents: [{Files}]",
                                    artifactPrefix,
                                    string.Join(", ", Directory.GetFiles(screenshotDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName)));
                        }
                        else
                        {
                            _logger.LogWarning("Screenshot dir does not exist: {Dir}", screenshotDir);
                        }

                        // Video and GIF are captured synchronously inside CaptureAppInteractionAsync
                        // (while app is still alive) — no more background Task.Run race condition
                        videoPath = interactionResult.VideoWebmPath;
                        var gifPath = interactionResult.AnimatedGifPath;

                        // Trim video if trimmer is available (best-effort)
                        if (videoPath is not null && _videoTrimmer is not null && _videoTrimmer.IsAvailable)
                        {
                            try
                            {
                                var trimmed = await _videoTrimmer.TrimVideoAsync(videoPath, CancellationToken.None);
                                if (trimmed is not null)
                                    videoPath = trimmed;
                            }
                            catch { /* best effort */ }
                        }
                        else if (videoPath is not null && (_videoTrimmer is null || !_videoTrimmer.IsAvailable))
                        {
                            _logger.LogInformation("Video trimming skipped: ffmpeg not available. Install ffmpeg for trimmed video previews");
                        }

                        // Copy artifacts to durable location (outside scratch worktree that gets disposed)
                        var durableDir = GetDurableArtifactDir(task.RunId, task.TaskId, exec.StrategyId);
                        Directory.CreateDirectory(durableDir);

                        if (videoPath is not null && File.Exists(videoPath))
                        {
                            var durableVideo = Path.GetFullPath(Path.Combine(durableDir, Path.GetFileName(videoPath)));
                            try { File.Copy(videoPath, durableVideo, overwrite: true); videoPath = durableVideo; }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to copy video to durable path"); }
                        }
                        if (gifPath is not null && File.Exists(gifPath))
                        {
                            var durableGif = Path.GetFullPath(Path.Combine(durableDir, Path.GetFileName(gifPath)));
                            try { File.Copy(gifPath, durableGif, overwrite: true); gifPath = durableGif; }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to copy GIF to durable path"); }
                        }

                        // Copy screenshots too
                        var durableScreenshotPaths = new List<string>();
                        foreach (var sp in screenshotPaths ?? Enumerable.Empty<string>())
                        {
                            var absPath = Path.Combine(scratch.Path, sp);
                            if (File.Exists(absPath))
                            {
                                var dest = Path.GetFullPath(Path.Combine(durableDir, Path.GetFileName(absPath)));
                                try { File.Copy(absPath, dest, overwrite: true); durableScreenshotPaths.Add(dest); }
                                catch { durableScreenshotPaths.Add(Path.GetFullPath(absPath)); }
                            }
                        }
                        if (durableScreenshotPaths.Count > 0)
                            screenshotPaths = durableScreenshotPaths;

                        animatedGifPath = gifPath;

                        _logger.LogInformation(
                            "Captured {Count} screenshots, video={HasVideo}, gif={HasGif} for strategy {Strategy} task {Task}",
                            interactionResult.Screenshots.Count,
                            videoPath is not null, gifPath is not null,
                            exec.StrategyId, task.TaskId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Interaction capture failed for strategy {Strategy} task {Task} — " +
                        "this candidate will have no preview in the dashboard gallery",
                        exec.StrategyId, task.TaskId);
                }
            }
        }

        // Log outcome for all candidates, including when screenshots were skipped
        if (screenshotBytes is null)
        {
            var reason = _screenshotRunner is null ? "PlaywrightRunner not available"
                : !cachedPlaywrightReady ? $"PlaywrightRunner not ready at evaluation start ({_screenshotRunner.NotReadyReason ?? "unknown reason"})"
                : !captureScreenshotsEnabled ? "CaptureScreenshots disabled"
                : "capture returned null (app failed to start or screenshots were blank)";
            _logger.LogWarning(
                "No screenshot for strategy {Strategy} task {Task}: {Reason}",
                exec.StrategyId, task.TaskId, reason);

            // Only classify the default Playwright path here. Earlier branches (for example
            // T-FINAL empty patches or blank-screenshot detection) may have already assigned
            // a more specific preview source that we should preserve.
            if (!producerSuppliedPreview && previewSource == CandidatePreviewSource.PlaywrightScreenshot)
            {
                if (_screenshotRunner is null || !cachedPlaywrightReady)
                    previewSource = CandidatePreviewSource.CaptureUnavailable;
                else if (!captureScreenshotsEnabled)
                    previewSource = CandidatePreviewSource.NoVisualContent;
                else
                    previewSource = CandidatePreviewSource.CaptureFailed;
            }
        }

        return (new CandidateResult
        {
            StrategyId = exec.StrategyId,
            Survived = true,
            Patch = patch,
            PatchSizeBytes = System.Text.Encoding.UTF8.GetByteCount(patch),
            Execution = exec,
            ScreenshotBytes = screenshotBytes,
            ScreenshotPaths = screenshotPaths,
            VideoPath = videoPath,
            AnimatedGifPath = animatedGifPath,
            PreviewSource = previewSource,
            IncludedAssetPaths = includedAssetPaths,
            SecondaryPreviewBase64 = secondaryPreviewBase64,
            SecondaryAssetPaths = secondaryAssetPaths,
            SecondaryPreviewSource = secondaryPreviewSource,
            CaptureMetrics = captureMetrics,
            PageAnalysis = pageAnalysis,
            AppBaseUrl = appBaseUrl,
            InteractionContext = InteractionContext.Build(
                pageAnalysis: pageAnalysis,
                appStarted: screenshotBytes is not null || pageAnalysis is not null),
        }, scratch);
    }

    private static CandidateResult Fail(StrategyExecutionResult exec, string patch, string gate, string? detail)
        => new()
        {
            StrategyId = exec.StrategyId,
            Survived = false,
            FailedGate = gate,
            FailureDetail = detail,
            Patch = patch ?? "",
            PatchSizeBytes = string.IsNullOrEmpty(patch) ? 0 : System.Text.Encoding.UTF8.GetByteCount(patch),
            Execution = exec,
        };

    private static async Task<(bool ok, string detail)> ApplyPatchAsync(string worktreePath, string patch, CancellationToken ct)
    {
        // `git apply --check` first, then the real apply. --3way allows fuzz against renamed lines.
        var patchFile = Path.Combine(Path.GetTempPath(), $"strat-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllTextAsync(patchFile, patch, ct);
            // --check validates structure; use --whitespace=nowarn so trailing whitespace
            // doesn't cause check failure — the real apply with --whitespace=fix handles it.
            var check = await RunProcAsync("git", new[] { "apply", "--check", "--3way", "--whitespace=nowarn", patchFile }, worktreePath, ct);
            if (check.exit != 0) return (false, check.stderr.Trim());
            var apply = await RunProcAsync("git", new[] { "apply", "--3way", "--whitespace=fix", patchFile }, worktreePath, ct);
            if (apply.exit != 0) return (false, apply.stderr.Trim());
            return (true, "");
        }
        finally
        {
            try { if (File.Exists(patchFile)) File.Delete(patchFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Gate2 build validation. Uses the configured build command if available,
    /// otherwise falls back to ProjectTypeDetector for auto-detection.
    /// The AI agents handle tech-stack-specific logic in their prompts �
    /// Gate2 just runs whatever build command is configured/detected.
    /// </summary>
    private async Task<(bool ok, string detail)> RunBuildAsync(string worktreePath, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
            cts.CancelAfter(timeout);
        try
        {
            // 1. Try configured build command first (from WorkspaceConfig or ServiceDefinition)
            var configuredCmd = _appCfg?.CurrentValue?.Workspace?.BuildCommand;
            if (!string.IsNullOrWhiteSpace(configuredCmd) && configuredCmd != "dotnet build")
            {
                // User explicitly configured a build command � trust it
                _logger.LogInformation("Gate2: using configured build command: {Cmd}", configuredCmd);
                return await RunShellBuildAsync(configuredCmd, worktreePath, cts.Token);
            }

            // 2. Auto-detect project type and use appropriate default command
            var detectedType = Workspace.ProjectTypeDetector.Detect(worktreePath);
            if (detectedType == Workspace.ProjectTypeDetector.ProjectType.NoBuildableCode)
            {
                _logger.LogInformation("Gate2 build skipped (no buildable code detected) for {Path}", worktreePath);
                return (true, "skipped-no-buildable-code");
            }

            var buildCmd = Workspace.ProjectTypeDetector.GetDefaultBuildCommand(detectedType);
            if (buildCmd is null)
            {
                _logger.LogInformation("Gate2: detected {Type} but no build command needed", detectedType);
                return (true, $"skipped-{detectedType}-no-build");
            }

            // For .NET projects, use the smarter ResolveBuildTarget for sln/csproj discovery
            if (detectedType == Workspace.ProjectTypeDetector.ProjectType.DotNet)
            {
                var buildTarget = ResolveBuildTarget(worktreePath);
                if (buildTarget is null)
                    return (true, "skipped-no-dotnet-target");

                var args = new List<string> { "build" };
                if (buildTarget.Length > 0) args.Add(buildTarget);
                args.Add("--nologo"); args.Add("-v"); args.Add("q");
                var res = await RunProcAsync("dotnet", args.ToArray(), worktreePath, cts.Token);
                if (res.exit != 0)
                    return (false, "dotnet build failed: " + Truncate(res.stderr + res.stdout, 800));
            }
            else
            {
                // For all other stacks, run the detected command directly
                var result = await RunShellBuildAsync(buildCmd, worktreePath, cts.Token);
                if (!result.ok) return result;
            }

            return (true, "");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return (false, $"build timeout after {timeout.TotalSeconds}s");
        }
    }

    /// <summary>Run a shell build command, splitting exe from args.</summary>
    private async Task<(bool ok, string detail)> RunShellBuildAsync(string command, string workingDir, CancellationToken ct)
    {
        var parts = command.Split(' ', 2);
        var exe = parts[0];
        var argStr = parts.Length > 1 ? parts[1] : "";
        var args = argStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        _logger.LogInformation("Gate2: running {Exe} {Args} in {Dir}", exe, argStr, workingDir);
        var res = await RunProcAsync(exe, args, workingDir, ct);
        return res.exit == 0
            ? (true, "")
            : (false, $"{command} failed: " + Truncate(res.stderr + res.stdout, 800));
    }

    /// <summary>
    /// Find the best dotnet build target in the worktree.
    /// Priority: .sln in root > .csproj in root > .sln anywhere > .csproj anywhere.
    /// Returns "" when dotnet build can auto-discover in root, a relative path for subdirectory
    /// targets, or null when no .NET project exists.
    /// </summary>
    private string? ResolveBuildTarget(string worktreePath)
    {
        var rootSlns = Directory.GetFiles(worktreePath, "*.sln", SearchOption.TopDirectoryOnly);
        if (rootSlns.Length > 0) return "";

        var rootCsprojs = Directory.GetFiles(worktreePath, "*.csproj", SearchOption.TopDirectoryOnly);
        if (rootCsprojs.Length > 0) return "";

        var anySlns = Directory.GetFiles(worktreePath, "*.sln", SearchOption.AllDirectories);
        if (anySlns.Length > 0)
        {
            var rel = Path.GetRelativePath(worktreePath, anySlns[0]);
            _logger.LogInformation("Gate2: auto-resolved build target to {Target} (no root sln/csproj)", rel);
            return rel;
        }

        var anyCsprojs = Directory.GetFiles(worktreePath, "*.csproj", SearchOption.AllDirectories);
        if (anyCsprojs.Length > 0)
        {
            var rel = Path.GetRelativePath(worktreePath, anyCsprojs[0]);
            _logger.LogInformation("Gate2: auto-resolved build target to {Target} (no root sln/csproj)", rel);
            return rel;
        }

        return null;
    }

    private static async Task<(int exit, string stdout, string stderr)> RunProcAsync(
        string exe, string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} start failed");
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await so, await se);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…[truncated]";

    /// <summary>
    /// Asset-only patch detector (eval-skip-media-asset-only fix, 2026-05-12). A patch is
    /// considered asset-only when EVERY changed file has an asset extension (PNG/JPG/MP3/MP4/etc)
    /// and ZERO files have a code/markup extension. When true, the media-capture pipeline
    /// (BuildGate→PlaywrightReady→AppDetection→DependencyRestore→AppStartup→McpExploration→
    /// DirectCapture→ScreenshotCapture→VideoRecording→GifGeneration→VideoTrimming→ArtifactStorage)
    /// is skipped — the binary deliverables in the patch ARE the preview, and spinning up
    /// the parent app to screenshot a webpage that doesn't reference the new assets is pure
    /// waste (live evidence: PR #1508 hung 19+ min on `dotnet restore` for a sprite-only PR).
    ///
    /// Conservative: ANY non-asset file in the patch (even a single .cs/.ts/.json/.md change
    /// alongside hundreds of PNGs) returns false → run media capture as usual. False is the
    /// safe default — wasting cycles is better than skipping useful preview for a real
    /// code change. Returns false on empty patch or parse failure (fall through to normal).
    /// </summary>
    internal static bool IsAssetOnlyPatch(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch)) return false;
        // Asset extensions where the file IS the deliverable (no code-level inspection helps).
        // Lowercase comparison; OrdinalIgnoreCase via ToLowerInvariant in caller.
        var assetExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico",
            ".tiff", ".tif", ".avif", ".heic", ".heif",
            ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".opus",
            ".mp4", ".webm", ".mov", ".avi", ".mkv", ".gltf", ".glb",
            ".obj", ".fbx", ".dae", ".blend", ".atlas", ".ttf", ".otf", ".woff", ".woff2",
            ".pdf",
        };
        // Parse `diff --git a/<path> b/<path>` headers and `+++ b/<path>` headers.
        // Skip /dev/null lines (deletions/additions of all-binary files).
        bool sawAnyChange = false;
        bool sawNonAsset = false;
        foreach (var line in patch.Split('\n'))
        {
            string? path = null;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                // Format: diff --git a/<path> b/<path>
                var idx = line.IndexOf(" b/", StringComparison.Ordinal);
                if (idx > 0 && idx + 3 < line.Length)
                    path = line[(idx + 3)..].Trim();
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                path = line[6..].Trim();
            }
            else if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                path = line[6..].Trim();
            }
            if (string.IsNullOrEmpty(path) || path == "/dev/null") continue;

            sawAnyChange = true;
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext) || !assetExts.Contains(ext))
            {
                sawNonAsset = true;
                break; // Conservative early-out — any non-asset file disqualifies.
            }
        }
        return sawAnyChange && !sawNonAsset;
    }

    /// <summary>
    /// Returns a durable artifact directory path (outside scratch worktrees) for strategy media.
    /// </summary>
    private string GetDurableArtifactDir(string runId, string taskId, string strategyId)
    {
        var wsRoot = _appCfg?.CurrentValue?.Workspace?.RootPath ?? ".agents";
        return Path.Combine(wsRoot, "strategy-artifacts", runId, taskId, strategyId);
    }

    /// <summary>
    /// Run the vision judge on all survivors that have screenshots, then merge visual
    /// scores back into each <see cref="CandidateResult"/>.
    /// Non-visual tasks (no screenshots at all) get <c>null</c> (excluded from total).
    /// Visual tasks with missing screenshots get score <c>0</c> (penalized).
    /// When multiple screenshots exist for a candidate, generates a contact sheet composite.
    /// </summary>
    private async Task<List<CandidateResult>> ApplyVisualScoresAsync(
        TaskContext task, List<CandidateResult> results, CancellationToken ct)
    {
        // Work on a copy to prevent "Collection was modified" exceptions if the
        // caller's list is observed concurrently (e.g., by SignalR/dashboard refresh).
        results = new List<CandidateResult>(results);

        if (_visualJudge is null or NullVisualJudge)
        {
            _logger.LogDebug("Visual judge is null/NullVisualJudge — skipping visual scoring for task {TaskId}", task.TaskId);
            return results;
        }

        // Log what we have to work with
        var survivorCount = results.Count(r => r.Survived);
        var withScreenshots = results.Count(r => r.Survived && r.ScreenshotBytes is { Length: > 0 });
        var withPaths = results.Count(r => r.Survived && r.ScreenshotPaths is { Count: > 0 });
        _logger.LogInformation(
            "ApplyVisualScoresAsync for task {TaskId}: {Survivors} survivors, {WithBytes} with ScreenshotBytes, {WithPaths} with ScreenshotPaths",
            task.TaskId, survivorCount, withScreenshots, withPaths);

        // If no survivors have ScreenshotBytes but some have ScreenshotPaths,
        // load the first screenshot from disk as a fallback. This handles the case
        // where media capture wrote files but ScreenshotBytes wasn't populated.
        //
        // IMPORTANT: materialize the query before mutating the list (otherwise we can throw
        // "Collection was modified; enumeration operation may not execute." and wedge scoring).
        var screenshotLoadTargets = results
            .Where(r => r.Survived && r.ScreenshotBytes is null or { Length: 0 } && r.ScreenshotPaths is { Count: > 0 })
            .ToList();

        foreach (var r in screenshotLoadTargets)
        {
            var firstPath = r.ScreenshotPaths!.FirstOrDefault(p => File.Exists(p));
            if (firstPath is not null)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(firstPath, ct);
                    var idx = results.IndexOf(r);
                    if (idx >= 0)
                    {
                        results[idx] = r with { ScreenshotBytes = bytes };
                    }
                    _logger.LogInformation(
                        "Loaded ScreenshotBytes from disk for {Strategy} task {TaskId}: {Path} ({Size} bytes)",
                        r.StrategyId, task.TaskId, firstPath, bytes.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load screenshot from {Path} for {Strategy}", firstPath, r.StrategyId);
                }
            }
        }

        // Build screenshot dictionary. When multiple screenshots exist for a candidate,
        // combine them into a contact sheet so the judge sees all pages in one image.
        var survivorsWithScreenshots = new Dictionary<string, byte[]>();
        foreach (var r in results.Where(r => r.Survived && r.ScreenshotBytes is { Length: > 0 }).ToList())
        {
            byte[]? imageForJudge = r.ScreenshotBytes;

            // If we have multiple screenshot paths and a contact sheet generator, create composite
            if (_contactSheet is not null && r.ScreenshotPaths is { Count: > 1 })
            {
                try
                {
                    // Load all screenshot files from disk with labels derived from filenames
                    var screenshotImages = new List<(string Label, byte[] PngBytes)>();
                    foreach (var path in r.ScreenshotPaths)
                    {
                        var fullPath = path; // paths stored may be relative to worktree
                        if (File.Exists(fullPath))
                        {
                            var label = Path.GetFileNameWithoutExtension(fullPath);
                            // Extract the page label from filename pattern: prefix-index-label.png
                            var parts = label.Split('-');
                            if (parts.Length >= 3)
                                label = string.Join(" ", parts.Skip(parts.Length - 1));
                            screenshotImages.Add((label, await File.ReadAllBytesAsync(fullPath, ct)));
                        }
                    }

                    if (screenshotImages.Count > 1)
                    {
                        var contactSheetBytes = await _contactSheet.GenerateAsync(screenshotImages, columns: 2, ct);
                        if (contactSheetBytes is not null)
                            imageForJudge = contactSheetBytes;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Contact sheet generation failed for {Strategy} — using landing screenshot",
                        r.StrategyId);
                }
            }

            if (imageForJudge is not null)
                survivorsWithScreenshots[r.StrategyId] = imageForJudge;
        }

        bool isVisualTask = survivorsWithScreenshots.Count > 0;
        if (!isVisualTask)
        {
            // Check if any survivor had ScreenshotPaths — meaning media was attempted
            // but failed. Set VisualsScore=0 with reason so the bar shows on the dashboard.
            // CaptureUnavailable leaves ScreenshotPaths empty, so those candidates keep
            // VisualsScore = null and are not penalized against their initial revision pass.
            bool mediaWasAttempted = results.Any(r => r.Survived && r.ScreenshotPaths is { Count: > 0 });
            if (mediaWasAttempted)
            {
                _logger.LogWarning(
                    "Visual scoring: screenshots were captured to disk but ScreenshotBytes is empty for all survivors on task {TaskId}. Setting VisualsScore=0.",
                    task.TaskId);
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    if (!r.Survived) continue;
                    var existingScore = r.Score ?? new CandidateScore();
                    results[i] = r with { Score = existingScore with
                    {
                        VisualsScore = 0,
                        VisualsFeedback = "Screenshots were captured but could not be loaded for visual scoring. Check Playwright/app startup logs."
                    }};
                }
                return results;
            }

            _logger.LogWarning(
                "No survivors with screenshots for task {TaskId} — visual scoring skipped (non-visual task). " +
                "Survivors: {Survivors}, ScreenshotBytes populated: {WithBytes}, ScreenshotPaths: {WithPaths}",
                task.TaskId, survivorCount, withScreenshots, withPaths);
            return results;
        }

        try
        {
            // Build interaction context map for candidates that have it
            var interactionContexts = results
                .Where(r => r.Survived && r.InteractionContext is not null)
                .ToDictionary(r => r.StrategyId, r => r.InteractionContext!);

            var visualTimeout = TimeSpan.FromMinutes(_cfg.CurrentValue.Evaluator.VisualScoringTimeoutMinutes);
            using var visualCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            visualCts.CancelAfter(visualTimeout);
            VisualJudgeResult judgeResult;
            try
            {
                judgeResult = await _visualJudge.ScoreAsync(new VisualJudgeInput
                {
                    TaskId = task.TaskId,
                    TaskTitle = task.TaskTitle,
                    TaskDescription = task.TaskDescription,
                    CandidateScreenshots = survivorsWithScreenshots,
                    CandidateInteractionContexts = interactionContexts.Count > 0 ? interactionContexts : null,
                }, visualCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Real cancellation — propagate
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "⏱️ Visual scoring TIMED OUT after {Minutes}m for task {TaskId} — " +
                    "proceeding without visual scores. VisualsScore will be null for all candidates.",
                    visualTimeout.TotalMinutes, task.TaskId);
                // On timeout, set VisualsScore=null for all survivors and return
                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i].Survived)
                    {
                        var existingScore = results[i].Score ?? new CandidateScore();
                        results[i] = results[i] with { Score = existingScore with
                        {
                            VisualsScore = null,
                            VisualsFeedback = $"Visual scoring timed out after {visualTimeout.TotalMinutes}m"
                        }};
                    }
                }
                return results;
            }

            if (!string.IsNullOrEmpty(judgeResult.Error))
            {
                _logger.LogWarning("Visual judge returned error for task {TaskId}: {Error}", task.TaskId, judgeResult.Error);
            }

            if (judgeResult.Scores.Count == 0)
            {
                _logger.LogWarning(
                    "Visual judge returned no valid scores for task {TaskId} (error: {Error}) — setting VisualsScore=0 with reason",
                    task.TaskId, judgeResult.Error ?? "none");
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    if (!r.Survived) continue;
                    var existingScore = r.Score ?? new CandidateScore();
                    results[i] = r with { Score = existingScore with
                    {
                        VisualsScore = 0,
                        VisualsFeedback = $"Visual judge returned no scores: {judgeResult.Error ?? "model/parse failure"}. Screenshots were available but could not be scored."
                    }};
                }
                return results;
            }

            // Apply scores: survivors with screenshots get judge score (or 0 if judge missed them);
            // survivors without screenshots get 0 (penalized); non-survivors stay null.
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (!r.Survived)
                    continue; // failed candidates don't get visual scores

                int visualScore;
                string? visualFeedback = null;
                if (r.ScreenshotBytes is { Length: > 0 })
                {
                    if (judgeResult.Scores.TryGetValue(r.StrategyId, out var vs))
                    {
                        visualScore = vs.Score;
                        visualFeedback = vs.Reasoning;
                    }
                    else
                    {
                        visualScore = 0;
                    }
                }
                else
                {
                    visualScore = 0; // visual task but no screenshot = penalize
                    visualFeedback = "No screenshot available — visual scoring penalized.";
                }

                // PageAnalysis penalty: if the app produced console errors or failed
                // network requests (e.g., runtime exceptions, 500s), cap the visual score.
                // This catches cases where the screenshot looks fine but the app is broken,
                // or where the video shows errors that the screenshot missed.
                if (r.PageAnalysis is { } pa)
                {
                    var errorCount = pa.ConsoleErrors.Count + pa.FailedRequests.Count;
                    if (errorCount > 0)
                    {
                        var cappedScore = Math.Min(visualScore, 2); // errors cap at 2/10
                        if (cappedScore < visualScore)
                        {
                            _logger.LogInformation(
                                "PageAnalysis penalty for {Strategy} task {TaskId}: {Errors} errors/failures — visual score capped {From}→{To}",
                                r.StrategyId, task.TaskId, errorCount, visualScore, cappedScore);
                            visualFeedback = $"Visual score capped due to {errorCount} runtime error(s): " +
                                string.Join("; ", pa.ConsoleErrors.Take(3).Concat(pa.FailedRequests.Take(3))) +
                                (visualFeedback is not null ? $". Judge feedback: {visualFeedback}" : "");
                        }
                        visualScore = cappedScore;
                    }
                }

                var existingScore = r.Score ?? new CandidateScore();
                results[i] = r with { Score = existingScore with { VisualsScore = visualScore, VisualsFeedback = visualFeedback } };
            }

            _logger.LogInformation(
                "Visual scoring applied for task {TaskId}: {Scores}",
                task.TaskId,
                string.Join(", ", results.Where(r => r.Score?.VisualsScore is not null)
                    .Select(r => $"{r.StrategyId}={r.Score!.VisualsScore}")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Visual judge failed for task {TaskId} — setting VisualsScore=0 with error reason", task.TaskId);
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (!r.Survived) continue;
                var existingScore = r.Score ?? new CandidateScore();
                results[i] = r with { Score = existingScore with
                {
                    VisualsScore = 0,
                    VisualsFeedback = $"Visual judge threw an exception: {ex.GetType().Name}: {ex.Message}"
                }};
            }
        }

        return results;
    }

    /// <summary>
    /// Score a T-FINAL empty-patch candidate based on structured verification evidence
    /// from its execution output. Deterministic — no LLM involved. Points awarded for
    /// each verified aspect (builds, tests, scenarios). A candidate that thoroughly
    /// verified "no fixes needed" deserves credit over one that did minimal checking.
    /// </summary>
    private static VerificationScore ScoreVerificationEvidence(StrategyExecutionResult exec)
    {
        var output = string.Join("\n", exec.Log);
        var score = new VerificationScore();

        // Backend build verified
        if (output.Contains("backend build", StringComparison.OrdinalIgnoreCase) &&
            (output.Contains("✅", StringComparison.Ordinal) || output.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("succeeded", StringComparison.OrdinalIgnoreCase)))
        {
            score.BuildsVerified++;
        }

        // Frontend build verified
        if (output.Contains("frontend build", StringComparison.OrdinalIgnoreCase) &&
            (output.Contains("✅", StringComparison.Ordinal) || output.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("succeeded", StringComparison.OrdinalIgnoreCase)))
        {
            score.BuildsVerified++;
        }

        // Generic build success (fallback for single-stack projects)
        if (score.BuildsVerified == 0 &&
            (output.Contains("build succeeded", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("Build SUCCEEDED", StringComparison.Ordinal)))
        {
            score.BuildsVerified++;
        }

        // Tests verified — look for test count patterns
        var testMatch = System.Text.RegularExpressions.Regex.Match(
            output, @"(\d+)\s+tests?\s+pass", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (testMatch.Success && int.TryParse(testMatch.Groups[1].Value, out var testCount))
        {
            score.TestsVerified = testCount;
        }
        // Fallback: "tests ✅" or "Backend tests ✅"
        if (score.TestsVerified == 0 &&
            output.Contains("tests", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("✅", StringComparison.Ordinal))
        {
            score.TestsVerified = 1; // at least some tests passed
        }

        // Scenarios verified — "N scenarios verified" or "All N scenarios"
        var scenarioMatch = System.Text.RegularExpressions.Regex.Match(
            output, @"(?:all\s+)?(\d+)\s+scenarios?\s+verified", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (scenarioMatch.Success && int.TryParse(scenarioMatch.Groups[1].Value, out var scenarioCount))
        {
            score.ScenariosVerified = scenarioCount;
        }

        // Integration clean
        if (output.Contains("integration is clean", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("no fixes needed", StringComparison.OrdinalIgnoreCase))
        {
            score.IntegrationClean = true;
        }

        // Compute scores (max 10 each for AC/Design/Readability)
        // AC: did it verify what the task asked? builds + tests + scenarios
        score.AcScore = Math.Min(10,
            (score.BuildsVerified >= 2 ? 3 : score.BuildsVerified >= 1 ? 2 : 0) +
            (score.TestsVerified >= 10 ? 4 : score.TestsVerified >= 1 ? 2 : 0) +
            (score.ScenariosVerified >= 3 ? 3 : score.ScenariosVerified >= 1 ? 2 : 0));

        // Design: integration cleanliness
        score.DesignScore = score.IntegrationClean ? 7 :
            (score.BuildsVerified > 0 && score.TestsVerified > 0) ? 5 : 0;

        // Readability: thoroughness of verification (more checks = more confident)
        var evidenceCount = score.BuildsVerified + (score.TestsVerified > 0 ? 1 : 0) +
            (score.ScenariosVerified > 0 ? 1 : 0) + (score.IntegrationClean ? 1 : 0);
        score.ReadabilityScore = Math.Min(10, evidenceCount * 2);

        // Build summary
        var parts = new List<string>();
        if (score.BuildsVerified > 0) parts.Add($"{score.BuildsVerified} build(s) passed");
        if (score.TestsVerified > 0) parts.Add($"{score.TestsVerified} test(s) passed");
        if (score.ScenariosVerified > 0) parts.Add($"{score.ScenariosVerified} scenario(s) verified");
        if (score.IntegrationClean) parts.Add("integration clean");
        score.Summary = parts.Count > 0 ? string.Join(", ", parts) : "no verification evidence found";

        return score;
    }

    /// <summary>
    /// Wraps a media capture call with a timeout. On timeout, logs a warning and returns null
    /// so the candidate can still proceed to scoring without screenshots/video.
    /// </summary>
    private async Task<AppInteractionResult?> CaptureWithTimeoutAsync(
        Func<Task<AppInteractionResult?>> captureFunc,
        TimeSpan timeout,
        string taskId,
        string strategyId,
        string operationName,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);
        try
        {
            return await captureFunc();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate real cancellation (dashboard cancel, shutdown)
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "⏱️ {Operation} TIMED OUT after {Minutes}m for task {TaskId} strategy {StrategyId} — " +
                "proceeding without media. Candidate remains eligible for scoring.",
                operationName, timeout.TotalMinutes, taskId, strategyId);
            return null;
        }
    }

    /// <summary>
    /// Wraps an LLM judge scoring call with a timeout. On timeout, returns an empty result
    /// so candidates proceed with no scores rather than blocking indefinitely.
    /// </summary>
    private async Task<JudgeResult> ScoreJudgeWithTimeoutAsync(
        JudgeInput input,
        TimeSpan timeout,
        string taskId,
        string context,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        // Judge/review CLI processes never generate images — suppress Azure image auth
        // to avoid 8s DefaultAzureCredential timeout per spawned process.
        using var _ = AgentCallContext.PushInvocationContext(
            new CopilotCliInvocationContext(SuppressImageGenEnv: true));

        try
        {
            return await _judge!.ScoreAsync(input, linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "⏱️ Judge scoring TIMED OUT after {Minutes}m for task {TaskId} ({Context}) — " +
                "proceeding without judge scores. Winner selection will use tiebreak logic.",
                timeout.TotalMinutes, taskId, context);
            return new JudgeResult { Scores = new Dictionary<string, CandidateScore>() };
        }
    }

    /// <summary>
    /// Emergency winner selection: picks the best surviving candidate from partial results
    /// when the normal evaluation pipeline crashes. Used by StrategyOrchestrator's catch
    /// block to salvage work instead of losing all candidate execution.
    /// </summary>
    public EvaluationResult? SelectEmergencyWinner(IReadOnlyList<CandidateResult> candidates)
    {
        var evalCfg = _cfg.CurrentValue.Evaluator;
        if (!evalCfg.EmergencyWinnerEnabled)
        {
            _logger.LogInformation("Emergency winner selection is disabled via config");
            return null;
        }

        // Filter: survived (or exec succeeded for pre-evaluation candidates), non-empty patch
        var qualified = candidates
            .Where(c => c.Survived || c.Execution.Succeeded)
            .Where(c => !string.IsNullOrWhiteSpace(c.Patch))
            .ToList();

        if (qualified.Count == 0)
        {
            _logger.LogWarning("🚨 Emergency winner: no qualified candidates (need Survived/Succeeded + non-empty Patch). " +
                "Candidates: {Summary}",
                string.Join(", ", candidates.Select(c =>
                    $"{c.StrategyId}(survived={c.Survived},succeeded={c.Execution.Succeeded},patch={c.PatchSizeBytes}B)")));
            return null;
        }

        // Rank: total judge score → visual score → preferred strategy (Squad > CLI) →
        //        smallest diff → fastest.  Strategy preference is promoted above
        //        patch-size/speed so that Squad wins when no scores differentiate.
        var defaultPref = evalCfg.EmergencyWinnerDefault ?? "squad";
        var winner = qualified
            .OrderByDescending(c =>
                (c.Score?.AcceptanceCriteriaScore ?? 0) +
                (c.Score?.DesignScore ?? 0) +
                (c.Score?.ReadabilityScore ?? 0))
            .ThenByDescending(c => c.Score?.VisualsScore ?? 0)
            .ThenByDescending(c => !string.IsNullOrEmpty(defaultPref)
                && c.StrategyId.Contains(defaultPref, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.PatchSizeBytes)
            .ThenBy(c => c.Execution.Elapsed)
            .First();

        _logger.LogWarning(
            "🚨 EMERGENCY WINNER selected: {StrategyId} (judge={JudgeTotal}, visual={Visual}, patch={PatchBytes}B, elapsed={Elapsed}) " +
            "from {Qualified}/{Total} qualified candidates",
            winner.StrategyId,
            (winner.Score?.AcceptanceCriteriaScore ?? 0) + (winner.Score?.DesignScore ?? 0) + (winner.Score?.ReadabilityScore ?? 0),
            winner.Score?.VisualsScore ?? -1,
            winner.PatchSizeBytes,
            winner.Execution.Elapsed,
            qualified.Count,
            candidates.Count);

        return new EvaluationResult
        {
            Candidates = candidates,
            Winner = winner,
            TieBreakReason = $"EMERGENCY: selected after evaluation crash. {qualified.Count}/{candidates.Count} candidates qualified.",
            EvaluationElapsed = TimeSpan.Zero,
        };
    }

    private class VerificationScore
    {
        public int BuildsVerified { get; set; }
        public int TestsVerified { get; set; }
        public int ScenariosVerified { get; set; }
        public bool IntegrationClean { get; set; }
        public int AcScore { get; set; }
        public int DesignScore { get; set; }
        public int ReadabilityScore { get; set; }
        public string Summary { get; set; } = "";
        public int Total => AcScore + DesignScore + ReadabilityScore;
    }
}