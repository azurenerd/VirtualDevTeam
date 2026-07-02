using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects pipeline stalls where no forward progress is being made despite work remaining.
/// Two complementary checks:
///
/// <list type="number">
///   <item><b>Stale blocked tasks:</b> Work items with <c>status:blocked</c> but whose linked
///   PRs are all closed/merged (the blocker resolved but the label wasn't cleared). This is
///   the most common cause of silent pipeline stalls — agents correctly skip blocked items,
///   but the block condition has already resolved.</item>
///   <item><b>All-idle stall:</b> Every engineer agent is Idle while the phase is still
///   ParallelDevelopment/Testing/Review. If no open PRs are in-flight either, the pipeline
///   has stalled completely.</item>
/// </list>
///
/// Distinct from <see cref="EmptyQueueDetector"/> (requires open claimable work items) and
/// <see cref="IdleAgentPhaseStuckDetector"/> (requires open PRs awaiting review).
/// </summary>
public sealed class PipelineStallDetector : IFlowDetector
{
    public string DetectorId => "pipeline-stall";

    private readonly ILogger<PipelineStallDetector> _logger;
    private readonly TimeSpan _idleThreshold;
    private readonly AgentCliLogService? _logService;
    private readonly ActiveLlmCallTracker? _llmTracker;
    private static readonly TimeSpan LogActivityWindow = TimeSpan.FromMinutes(5);

    public PipelineStallDetector(
        ILogger<PipelineStallDetector> logger,
        TimeSpan? idleThreshold = null,
        AgentCliLogService? logService = null,
        ActiveLlmCallTracker? llmTracker = null)
    {
        _logger = logger;
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(10);
        _logService = logService;
        _llmTracker = llmTracker;
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            // Only relevant during active engineering phases.
            if (!IsActiveEngineeringPhase(ctx.CurrentPhase))
                return findings;

            var workItems = await ctx.Platform.ListOpenWorkItemsAsync(ct).ConfigureAwait(false);
            var openPRs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);

            // ── Check 1: Stale status:blocked tasks ──
            // Tasks marked blocked but with no corresponding open PRs are stale —
            // the blocking PR was closed/merged but the label wasn't cleared.
            var blockedTasks = workItems
                .Where(wi => wi.Labels.Any(l =>
                    l.Equals("status:blocked", StringComparison.OrdinalIgnoreCase)))
                .Where(wi => wi.Labels.Any(l =>
                    l.Equals("engineering-task", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var task in blockedTasks)
            {
                // A task is legitimately blocked if there's an open PR actively working on it
                // (in-progress PR for this task that hasn't merged yet). If all linked PRs
                // are closed/merged, the block is stale.
                var hasActivePr = openPRs.Any(pr =>
                    pr.Title.Contains(GetTaskFragment(task.Title), StringComparison.OrdinalIgnoreCase) ||
                    pr.Labels.Any(l => l.Equals($"task-{task.Number}", StringComparison.OrdinalIgnoreCase)));

                if (!hasActivePr)
                {
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        TargetResource = $"issue#{task.Number}",
                        Summary = $"Task #{task.Number} '{Truncate(task.Title, 60)}' has stale status:blocked " +
                                  $"label — no open PR is working on it. Engineers will skip it indefinitely.",
                        Rationale = "A work item has the status:blocked label but no open pull request is linked " +
                                    "to it. This typically happens when a PR was closed or merged (resolving the " +
                                    "blocker) but the blocked label wasn't cleared. Engineers correctly skip " +
                                    "blocked items, causing a silent pipeline stall. Fix: remove the status:blocked " +
                                    "label so an engineer can claim the task.",
                        DedupKey = $"pipeline-stall:blocked:{task.Number}",
                        RecommendedFixId = $"remove-label:{task.Number}:status:blocked",
                        RecommendedFixDescription = $"Remove status:blocked label from issue #{task.Number}",
                    });
                }
            }

            // ── Check 2: All-idle stall ──
            // Every engineer is idle + no open PRs in-flight + work items remain = full stall.
            var engineers = ctx.Agents
                .Where(a => IsEngineerRole(a.Role))
                .ToList();

            if (engineers.Count == 0)
                return findings;

            var allIdle = engineers.All(a =>
                string.Equals(a.Status, "Idle", StringComparison.OrdinalIgnoreCase));

            var allIdleLongEnough = allIdle && engineers.All(a =>
                a.StatusChangedAt is not null &&
                ctx.Now - a.StatusChangedAt.Value >= _idleThreshold &&
                !HasRecentActivity(a.Id));

            if (allIdleLongEnough && openPRs.Count == 0)
            {
                // Check if there are still open engineering tasks (not blocked).
                var claimableTasks = workItems
                    .Where(wi => wi.Labels.Any(l =>
                        l.Equals("engineering-task", StringComparison.OrdinalIgnoreCase)))
                    .Where(wi => !wi.Labels.Any(l =>
                        l.Equals("status:done", StringComparison.OrdinalIgnoreCase) ||
                        l.Equals("status:blocked", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (claimableTasks.Count > 0)
                {
                    // Claimable tasks exist but no one is picking them up.
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Critical,
                        TargetResource = "pipeline",
                        Summary = $"Pipeline stall: all {engineers.Count} engineers idle for ≥{(int)_idleThreshold.TotalMinutes}m, " +
                                  $"0 open PRs, but {claimableTasks.Count} claimable task(s) remain " +
                                  $"(e.g. #{claimableTasks[0].Number} '{Truncate(claimableTasks[0].Title, 50)}').",
                        Rationale = "Every engineer agent is Idle with no open pull requests, yet open engineering " +
                                    "tasks exist that should be claimable. The SE lead's task assignment loop isn't " +
                                    "finding eligible work. Common causes: label mismatch (task has unexpected labels), " +
                                    "dependency resolution treating tasks as blocked, or agent claim-query predicate " +
                                    "bug. Operator should check SE lead status and task labels.",
                        DedupKey = "pipeline-stall:all-idle",
                    });
                }
                else if (workItems.Count == 0 || workItems.All(wi =>
                    wi.Labels.Any(l => l.Equals("status:done", StringComparison.OrdinalIgnoreCase))))
                {
                    // All tasks done but phase hasn't advanced — phase advancement watchdog
                    // should catch this, so we skip to avoid noise.
                }
                else if (blockedTasks.Count > 0 && findings.Count == 0)
                {
                    // All remaining tasks are blocked — this is a stall too, but only if
                    // we didn't already emit stale-blocked findings above.
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Warning,
                        TargetResource = "pipeline",
                        Summary = $"Pipeline stall: all {engineers.Count} engineers idle, " +
                                  $"{blockedTasks.Count} remaining task(s) all marked status:blocked.",
                        Rationale = "Every remaining engineering task is blocked and all engineers are idle. " +
                                    "If the blockers have resolved (PRs merged/closed), the blocked labels are stale. " +
                                    "If blockers are genuine, human intervention is needed to resolve them.",
                        DedupKey = "pipeline-stall:all-blocked",
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineStallDetector tick failed (non-fatal)");
        }
        return findings;
    }

    private static bool IsActiveEngineeringPhase(string phase) =>
        string.Equals(phase, "ParallelDevelopment", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(phase, "Testing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(phase, "Review", StringComparison.OrdinalIgnoreCase);

    private static bool IsEngineerRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        return role.Contains("SoftwareEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("Software Engineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("FrontendEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("BackendEngineer", StringComparison.OrdinalIgnoreCase)
            || role.Contains("Specialist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extract a meaningful fragment from a task title for PR title matching.
    /// Strips common prefixes like "[T-16]" and agent name prefixes.
    /// </summary>
    private static string GetTaskFragment(string title)
    {
        var t = title;
        // Strip [T-N] prefix
        var bracketEnd = t.IndexOf(']');
        if (bracketEnd >= 0 && bracketEnd < t.Length - 1)
            t = t[(bracketEnd + 1)..].TrimStart();
        // Strip "AgentName: " prefix
        var colonIdx = t.IndexOf(':');
        if (colonIdx > 0 && colonIdx < t.Length - 1)
        {
            var prefix = t[..colonIdx].Trim();
            if (prefix.Contains(' ') && !prefix.StartsWith('['))
                t = t[(colonIdx + 1)..].TrimStart();
        }
        return Truncate(t, 40);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private bool HasRecentActivity(string agentId)
    {
        if (_logService is not null)
        {
            var lastLog = _logService.GetLatestEntryTimestamp(agentId);
            if (lastLog.HasValue && (DateTime.UtcNow - lastLog.Value) < LogActivityWindow)
                return true;
        }
        return _llmTracker?.GetActiveCall(agentId) is not null;
    }
}
