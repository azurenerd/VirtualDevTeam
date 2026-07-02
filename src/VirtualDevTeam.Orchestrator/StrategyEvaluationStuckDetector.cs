using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Detects strategy evaluation pipelines stuck in scoring, media capture, or candidate execution.
/// Complements <see cref="StuckStrategyCandidateDetector"/> which watches process-level hangs —
/// this detector watches higher-level evaluation-phase stalls where the pipeline as a whole
/// is not progressing even though individual processes may still be alive.
///
/// <para>Three conditions, each with its own dedup key suffix:</para>
/// <list type="bullet">
/// <item><b>scoring-stuck</b>: All candidates are Completed/Evaluated but no scoring progress
/// for longer than <c>JudgeScoringTimeoutMinutes</c>.</item>
/// <item><b>media-stuck</b>: At least one candidate completed but the task hasn't moved to
/// scoring phase within <c>MediaCaptureTimeoutMinutes</c>.</item>
/// <item><b>candidate-stuck</b>: Any single candidate has been Running for &gt;60 min.</item>
/// </list>
/// </summary>
public sealed class StrategyEvaluationStuckDetector : IFlowDetector
{
    public string DetectorId => "strategy-evaluation-stuck";

    private readonly CandidateStateStore _stateStore;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly ILogger<StrategyEvaluationStuckDetector> _logger;
    private readonly AgentCliLogService? _logService;
    private readonly ActiveLlmCallTracker? _llmTracker;
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(5);

    public StrategyEvaluationStuckDetector(
        CandidateStateStore stateStore,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        ILogger<StrategyEvaluationStuckDetector> logger,
        AgentCliLogService? logService = null,
        ActiveLlmCallTracker? llmTracker = null)
    {
        _stateStore = stateStore;
        _cfg = cfg;
        _logger = logger;
        _logService = logService;
        _llmTracker = llmTracker;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var activeTasks = _stateStore.GetActiveTasks();
            var evalCfg = _cfg.CurrentValue.Evaluator;

            foreach (var task in activeTasks)
            {
                if (ct.IsCancellationRequested) break;
                if (task.Cancelled || task.WinnerStrategyId is not null) continue;
                if (task.Candidates.IsEmpty) continue;

                var candidates = task.Candidates.Values.ToList();

                // Condition A: scoring-stuck — all candidates done but no scoring progress
                CheckScoringStuck(ctx, task, candidates, evalCfg, findings);

                // Condition B: media-stuck — completed candidates but task not in scoring phase
                CheckMediaStuck(ctx, task, candidates, evalCfg, findings);

                // Condition C: candidate-stuck — any single candidate running too long
                CheckCandidateStuck(ctx, task, candidates, evalCfg, findings);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyEvaluationStuckDetector tick failed (non-fatal)");
        }

        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    private void CheckScoringStuck(
        DetectorContext ctx,
        TaskSnapshot task,
        List<CandidateSnapshot> candidates,
        EvaluatorConfig evalCfg,
        List<FlowFinding> findings)
    {
        // All candidates must be in Completed or Evaluated state (past execution, awaiting scoring)
        var scoringStates = new[] { CandidateState.Completed, CandidateState.Evaluated };
        if (!candidates.All(c => scoringStates.Contains(c.State))) return;

        // Find the most recent completion time
        var lastCompleted = candidates
            .Where(c => c.CompletedAt.HasValue)
            .Select(c => c.CompletedAt!.Value)
            .DefaultIfEmpty()
            .Max();

        if (lastCompleted == default) return;

        var elapsed = ctx.Now - lastCompleted;
        var threshold = TimeSpan.FromMinutes(evalCfg.JudgeScoringTimeoutMinutes);
        if (elapsed < threshold) return;

        // Check if any candidate has recent activity (scoring updates)
        if (HasRecentCandidateActivity(candidates))
        {
            _logger.LogDebug(
                "scoring-stuck check suppressed for {Task}: candidate activity within {Window}",
                task.TaskId, LogActivityWindow);
            return;
        }

        // Check if the owning agent has recent log activity or active LLM calls
        if (HasRecentAgentActivity(ctx, task))
        {
            _logger.LogDebug(
                "scoring-stuck check suppressed for {Task}: agent has recent log/LLM activity",
                task.TaskId);
            return;
        }

        findings.Add(new FlowFinding
        {
            Id = Guid.NewGuid().ToString("N"),
            DetectedAt = ctx.Now,
            DetectorId = DetectorId,
            Severity = FlowFindingSeverity.Critical,
            TargetResource = $"strategy-task:{task.TaskId}",
            Summary = $"Strategy task '{Truncate(task.TaskId, 60)}' scoring stuck: " +
                      $"all {candidates.Count} candidates completed but no scoring progress for {FormatDuration(elapsed)}.",
            Rationale = $"All candidates reached Completed/Evaluated state but judge scoring " +
                        $"has not produced results within the {evalCfg.JudgeScoringTimeoutMinutes}min threshold. " +
                        $"No recent log activity or LLM calls detected. " +
                        $"The paired promote-strategy-winner action will cancel the orchestration " +
                        $"to trigger emergency winner selection.",
            DedupKey = $"strategy-stuck:{task.TaskId}:scoring-stuck",
        });
    }

    private void CheckMediaStuck(
        DetectorContext ctx,
        TaskSnapshot task,
        List<CandidateSnapshot> candidates,
        EvaluatorConfig evalCfg,
        List<FlowFinding> findings)
    {
        // At least one candidate must be completed
        var completedCandidates = candidates.Where(c => c.CompletedAt.HasValue).ToList();
        if (completedCandidates.Count == 0) return;

        // If any candidates are already scored (have AC/Design/Readability scores), the task
        // has progressed past media capture — not stuck.
        if (candidates.Any(c => c.AcScore.HasValue || c.DesignScore.HasValue || c.ReadabilityScore.HasValue))
            return;

        // Check elapsed since the FIRST candidate completed (media capture should start then)
        var firstCompleted = completedCandidates
            .Select(c => c.CompletedAt!.Value)
            .Min();

        var elapsed = ctx.Now - firstCompleted;
        var threshold = TimeSpan.FromMinutes(evalCfg.MediaCaptureTimeoutMinutes);
        if (elapsed < threshold) return;

        // Check if any candidate has recent activity (state store updates during media capture/scoring)
        if (HasRecentCandidateActivity(candidates))
        {
            _logger.LogDebug(
                "media-stuck check suppressed for {Task}: candidate activity within {Window}",
                task.TaskId, LogActivityWindow);
            return;
        }

        // Check if the owning agent has recent log activity or active LLM calls
        if (HasRecentAgentActivity(ctx, task))
        {
            _logger.LogDebug(
                "media-stuck check suppressed for {Task}: agent has recent log/LLM activity",
                task.TaskId);
            return;
        }

        findings.Add(new FlowFinding
        {
            Id = Guid.NewGuid().ToString("N"),
            DetectedAt = ctx.Now,
            DetectorId = DetectorId,
            Severity = FlowFindingSeverity.Critical,
            TargetResource = $"strategy-task:{task.TaskId}",
            Summary = $"Strategy task '{Truncate(task.TaskId, 60)}' media capture stuck: " +
                      $"{completedCandidates.Count} candidate(s) completed but no scoring started after {FormatDuration(elapsed)}.",
            Rationale = $"At least one candidate completed execution but no scoring has begun " +
                        $"within {evalCfg.MediaCaptureTimeoutMinutes}min. Media capture or " +
                        $"app launch may be hung. No recent log activity or LLM calls detected. " +
                        $"The paired promote-strategy-winner action will " +
                        $"cancel the orchestration to trigger emergency winner selection.",
            DedupKey = $"strategy-stuck:{task.TaskId}:media-stuck",
        });
    }

    private void CheckCandidateStuck(
        DetectorContext ctx,
        TaskSnapshot task,
        List<CandidateSnapshot> candidates,
        EvaluatorConfig evalCfg,
        List<FlowFinding> findings)
    {
        var threshold = TimeSpan.FromMinutes(evalCfg.StuckCandidateMinutes);
        foreach (var candidate in candidates)
        {
            if (candidate.State != CandidateState.Running) continue;
            if (!candidate.ProcessStartedAt.HasValue) continue;

            var elapsed = ctx.Now - candidate.ProcessStartedAt.Value;
            if (elapsed < threshold) continue;

            // Check if the candidate itself has recent activity
            if (candidate.LastActivityAt.HasValue &&
                (ctx.Now - candidate.LastActivityAt.Value) < LogActivityWindow)
            {
                _logger.LogDebug(
                    "candidate-stuck check suppressed for {Task}/{Candidate}: recent candidate activity",
                    task.TaskId, candidate.StrategyId);
                continue;
            }

            // Check if the owning agent has recent log activity or active LLM calls
            if (HasRecentAgentActivity(ctx, task))
            {
                _logger.LogDebug(
                    "candidate-stuck check suppressed for {Task}: agent has recent log/LLM activity",
                    task.TaskId);
                continue;
            }

            // Find the strategy ID from the task's candidate dictionary
            var strategyId = task.Candidates
                .FirstOrDefault(kvp => kvp.Value == candidate).Key;

            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = ctx.Now,
                DetectorId = DetectorId,
                Severity = FlowFindingSeverity.Critical,
                TargetResource = $"strategy-task:{task.TaskId}",
                Summary = $"Strategy candidate '{strategyId}' for task '{Truncate(task.TaskId, 40)}' " +
                          $"has been Running for {FormatDuration(elapsed)} (threshold: {evalCfg.StuckCandidateMinutes}min).",
                Rationale = $"A single candidate has exceeded the 60-minute execution threshold. " +
                            $"No recent log activity or LLM calls detected. " +
                            $"The CLI session may be hung or the task may be too complex. " +
                            $"The paired promote-strategy-winner action will cancel the " +
                            $"orchestration to trigger emergency winner selection.",
                DedupKey = $"strategy-stuck:{task.TaskId}:candidate-stuck",
            });
            // Only emit one finding per task for candidate-stuck
            break;
        }
    }

    /// <summary>
    /// Checks if any candidate has recent <see cref="CandidateSnapshot.LastActivityAt"/>
    /// within the activity window, indicating the pipeline is actively progressing.
    /// </summary>
    private bool HasRecentCandidateActivity(List<CandidateSnapshot> candidates)
    {
        var now = DateTimeOffset.UtcNow;
        return candidates.Any(c =>
            c.LastActivityAt.HasValue &&
            (now - c.LastActivityAt.Value) < LogActivityWindow);
    }

    /// <summary>
    /// Checks whether any agent associated with this strategy task has recent CLI log
    /// entries or an active LLM call, indicating the system is still working.
    /// </summary>
    private bool HasRecentAgentActivity(DetectorContext ctx, TaskSnapshot task)
    {
        if (_logService == null && _llmTracker == null) return false;

        // Strategy tasks are owned by agents whose status contains the task ID or title.
        // Rather than trying to identify the exact agent, check ALL agents for activity —
        // if any agent is actively doing work, suppressing the alert is safe because
        // strategy evaluation is orchestrated by the engineer agent.
        foreach (var agent in ctx.Agents)
        {
            if (agent.Status != "Working") continue;

            // Check LLM tracker first (cheapest)
            if (_llmTracker != null)
            {
                var activeCall = _llmTracker.GetActiveCall(agent.Id);
                if (activeCall != null) return true;
            }

            // Check log service for recent entries
            if (_logService != null)
            {
                var latestEntry = _logService.GetLatestEntryTimestamp(agent.Id);
                if (latestEntry.HasValue && (ctx.Now - latestEntry.Value) < LogActivityWindow)
                    return true;
            }
        }

        return false;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        if (ts.TotalHours < 24) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{(int)ts.TotalDays}d {ts.Hours}h";
    }
}
