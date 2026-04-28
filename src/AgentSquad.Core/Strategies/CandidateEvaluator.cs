using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Workspace;

namespace AgentSquad.Core.Strategies;

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
    private readonly IOptionsMonitor<AgentSquadConfig>? _appCfg;

    public CandidateEvaluator(
        ILogger<CandidateEvaluator> logger,
        GitWorktreeManager worktree,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        ILlmJudge? judge = null,
        IVisualJudge? visualJudge = null,
        PlaywrightRunner? screenshotRunner = null,
        IOptionsMonitor<AgentSquadConfig>? appCfg = null)
    {
        _logger = logger;
        _worktree = worktree;
        _cfg = cfg;
        _judge = judge;
        _visualJudge = visualJudge;
        _screenshotRunner = screenshotRunner;
        _appCfg = appCfg;
    }

    public async Task<EvaluationResult> EvaluateAsync(
        TaskContext task,
        IReadOnlyList<(StrategyExecutionResult exec, string patch)> strategyOutputs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<CandidateResult>(strategyOutputs.Count);
        var cfg = _cfg.CurrentValue;

        foreach (var (exec, patch) in strategyOutputs)
        {
            var res = await RunGatesAsync(task, exec, patch, cfg, ct);
            results.Add(res);
        }

        var survivors = results.Where(r => r.Survived).ToList();
        CandidateResult? winner = null;
        string? tieBreak = null;

        if (survivors.Count == 0)
        {
            // No winner — evaluator caller will fall through to baseline re-run or blocker.
        }
        else if (survivors.Count == 1)
        {
            winner = survivors[0];
            tieBreak = "sole-survivor";
        }
        else if (_judge is not null)
        {
            // Batch-score survivors, then rank by AC -> Design -> Readability -> tokens -> time -> id.
            var ecfg = _cfg.CurrentValue;
            var sanitized = survivors.ToDictionary(
                c => c.StrategyId,
                c => JudgeInputSanitizer.SanitizePatch(c.Patch, ecfg.Evaluator.MaxJudgePatchChars));
            var judgeResult = await _judge.ScoreAsync(new JudgeInput
            {
                TaskId = task.TaskId,
                TaskTitle = task.TaskTitle,
                TaskDescription = task.TaskDescription,
                CandidatePatches = sanitized,
                MaxPatchChars = ecfg.Evaluator.MaxJudgePatchChars,
            }, ct);

            var scored = survivors.Select(c => judgeResult.Scores.TryGetValue(c.StrategyId, out var s)
                ? c with { Score = s }
                : c).ToList();

            var ordered = scored
                .OrderByDescending(c => c.Score?.AcceptanceCriteriaScore ?? -1)
                .ThenByDescending(c => c.Score?.DesignScore ?? -1)
                .ThenByDescending(c => c.Score?.ReadabilityScore ?? -1)
                .ThenByDescending(c => c.Score?.VisualsScore ?? -1)
                .ThenBy(c => c.Execution.TokensUsed ?? long.MaxValue)
                .ThenBy(c => c.Execution.Elapsed)
                .ThenBy(c => c.StrategyId, StringComparer.Ordinal)
                .ToList();
            winner = ordered[0];
            tieBreak = judgeResult.IsFallback ? "judge-fallback-tokens-time" : "llm-rank";
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

        // ── Visual scoring (runs for ALL survivors, even sole survivor) ──
        results = await ApplyVisualScoresAsync(task, results, ct);

        return new EvaluationResult
        {
            Candidates = results,
            Winner = winner,
            TieBreakReason = tieBreak,
            EvaluationElapsed = sw.Elapsed,
        };
    }

    private async Task<CandidateResult> RunGatesAsync(
        TaskContext task, StrategyExecutionResult exec, string patch, StrategyFrameworkConfig cfg, CancellationToken ct)
    {
        // Gate1: OutputProduced
        if (!exec.Succeeded)
        {
            return Fail(exec, patch, "strategy-failed", exec.FailureReason);
        }
        if (string.IsNullOrWhiteSpace(patch))
        {
            return Fail(exec, patch, "gate1-output", "empty patch");
        }

        // Path safety / reserved-path / .git guard.
        var pathIssue = GitWorktreeManager.ValidatePatchPaths(patch, cfg.Evaluator.ReservedPathPrefix);
        if (pathIssue is not null)
        {
            return Fail(exec, patch, "gate2-build", $"rejected-path: {pathIssue}");
        }

        // Gate2: Build — apply to a scratch worktree and run dotnet build.
        await using var scratch = await _worktree.CreateAsync(
            task.AgentRepoPath, cfg.CandidateDirectoryName + "-eval",
            task.TaskId, exec.StrategyId + "-eval", task.BaseSha, ct);

        var applyResult = await ApplyPatchAsync(scratch.Path, patch, ct);
        if (!applyResult.ok)
        {
            return Fail(exec, patch, "gate2-build", $"apply-failed: {applyResult.detail}");
        }

        var buildOk = await RunBuildAsync(scratch.Path, TimeSpan.FromSeconds(cfg.Timeouts.BuildGateSeconds), ct);
        if (!buildOk.ok)
        {
            return Fail(exec, patch, "gate2-build", buildOk.detail);
        }

        // Gate3 / Gate4 are stubs in Phase 1 — they pass unless integrators wire them.
        // (The dashboard still sees the gate:started/completed events.)

        // Capture a preview screenshot of the running app (best-effort, never blocks evaluation).
        byte[]? screenshotBytes = null;
        if (task.IsWebTask && _screenshotRunner is not null && _screenshotRunner.IsReady)
        {
            var wsConfig = _appCfg?.CurrentValue?.Workspace;
            if (wsConfig is not null && wsConfig.CaptureScreenshots)
            {
                try
                {
                    // Clone config so CaptureAppScreenshotAsync can mutate AppStartCommand safely
                    var configSnapshot = new WorkspaceConfig
                    {
                        AppStartCommand = wsConfig.AppStartCommand,
                        AppBaseUrl = wsConfig.AppBaseUrl,
                        ScreenshotRenderDelaySeconds = wsConfig.ScreenshotRenderDelaySeconds,
                        BuildCommand = wsConfig.BuildCommand,
                        PlaywrightBrowsersCachePath = wsConfig.PlaywrightBrowsersCachePath,
                        CaptureScreenshots = true,
                    };
                    screenshotBytes = await _screenshotRunner.CaptureAppScreenshotAsync(
                        scratch.Path, configSnapshot, ct);
                    if (screenshotBytes is not null)
                    {
                        _logger.LogInformation(
                            "Captured {Size}-byte preview screenshot for strategy {Strategy} task {Task}",
                            screenshotBytes.Length, exec.StrategyId, task.TaskId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Screenshot capture failed for strategy {Strategy} task {Task} — " +
                        "this candidate will have no preview in the dashboard gallery",
                        exec.StrategyId, task.TaskId);
                }
            }
        }

        // Log outcome for all candidates, including when screenshots were skipped
        if (screenshotBytes is null)
        {
            var reason = !task.IsWebTask ? "not a web task"
                : _screenshotRunner is null ? "PlaywrightRunner not available"
                : !_screenshotRunner.IsReady ? "PlaywrightRunner not ready (browser not installed)"
                : _appCfg?.CurrentValue?.Workspace?.CaptureScreenshots != true ? "CaptureScreenshots disabled"
                : "capture returned null (app may have failed to start)";
            _logger.LogWarning(
                "No screenshot for strategy {Strategy} task {Task}: {Reason}",
                exec.StrategyId, task.TaskId, reason);
        }

        return new CandidateResult
        {
            StrategyId = exec.StrategyId,
            Survived = true,
            Patch = patch,
            PatchSizeBytes = System.Text.Encoding.UTF8.GetByteCount(patch),
            Execution = exec,
            ScreenshotBytes = screenshotBytes,
        };
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
            var check = await RunProcAsync("git", new[] { "apply", "--check", "--3way", "--whitespace=fix", patchFile }, worktreePath, ct);
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

    private async Task<(bool ok, string detail)> RunBuildAsync(string worktreePath, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // Find the best build target (sln/csproj) — may be in a subdirectory
            var buildTarget = ResolveBuildTarget(worktreePath);
            if (buildTarget is null)
            {
                _logger.LogInformation("Gate2 build skipped (no sln/csproj) for {Path}", worktreePath);
                return (true, "skipped-no-dotnet");
            }

            var args = new List<string> { "build" };
            if (buildTarget.Length > 0)
                args.Add(buildTarget);
            args.Add("--nologo");
            args.Add("-v");
            args.Add("q");

            var res = await RunProcAsync("dotnet", args.ToArray(), worktreePath, cts.Token);
            if (res.exit != 0) return (false, "dotnet build failed: " + Truncate(res.stderr + res.stdout, 800));
            return (true, "");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return (false, $"build timeout after {timeout.TotalSeconds}s");
        }
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
    /// Run the vision judge on all survivors that have screenshots, then merge visual
    /// scores back into each <see cref="CandidateResult"/>.
    /// Non-visual tasks (no screenshots at all) get <c>null</c> (excluded from total).
    /// Visual tasks with missing screenshots get score <c>0</c> (penalized).
    /// </summary>
    private async Task<List<CandidateResult>> ApplyVisualScoresAsync(
        TaskContext task, List<CandidateResult> results, CancellationToken ct)
    {
        if (_visualJudge is null or NullVisualJudge) return results;

        // Determine if this is a visual task: at least one survivor captured a screenshot.
        var survivorsWithScreenshots = results
            .Where(r => r.Survived && r.ScreenshotBytes is { Length: > 0 })
            .ToDictionary(r => r.StrategyId, r => r.ScreenshotBytes!);

        bool isVisualTask = survivorsWithScreenshots.Count > 0;
        if (!isVisualTask)
        {
            _logger.LogDebug("No survivors with screenshots for task {TaskId} — skipping visual scoring", task.TaskId);
            return results;
        }

        try
        {
            var judgeResult = await _visualJudge.ScoreAsync(new VisualJudgeInput
            {
                TaskId = task.TaskId,
                TaskTitle = task.TaskTitle,
                TaskDescription = task.TaskDescription,
                CandidateScreenshots = survivorsWithScreenshots,
            }, ct);

            if (!string.IsNullOrEmpty(judgeResult.Error))
            {
                _logger.LogWarning("Visual judge returned error for task {TaskId}: {Error}", task.TaskId, judgeResult.Error);
            }

            // Apply scores: survivors with screenshots get judge score (or 0 if judge missed them);
            // survivors without screenshots get 0 (penalized); non-survivors stay null.
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (!r.Survived)
                    continue; // failed candidates don't get visual scores

                int visualScore;
                if (r.ScreenshotBytes is { Length: > 0 })
                {
                    visualScore = judgeResult.Scores.TryGetValue(r.StrategyId, out var vs) ? vs.Score : 0;
                }
                else
                {
                    visualScore = 0; // visual task but no screenshot = penalize
                }

                var existingScore = r.Score ?? new CandidateScore();
                results[i] = r with { Score = existingScore with { VisualsScore = visualScore } };
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
            _logger.LogWarning(ex, "Visual judge failed for task {TaskId} — visual scores will be null", task.TaskId);
        }

        return results;
    }
}
