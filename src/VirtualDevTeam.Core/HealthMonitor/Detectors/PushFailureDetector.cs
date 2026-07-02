namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

using Microsoft.Extensions.Logging;

/// <summary>
/// Detects repeated git push/rebase failures for agents. When push retries fail
/// repeatedly, the agent is likely stuck in a conflict loop. This detector surfaces
/// the issue for operator attention and recommends a rebase reset.
/// </summary>
public sealed class PushFailureDetector : IFlowDetector
{
    public string DetectorId => "push-failure";

    private readonly PushFailureTracker _tracker;
    private readonly ILogger<PushFailureDetector> _logger;
    private readonly TimeSpan _window;
    private readonly int _warningThreshold;
    private readonly int _criticalThreshold;

    public PushFailureDetector(
        PushFailureTracker tracker,
        ILogger<PushFailureDetector> logger,
        TimeSpan? window = null,
        int warningThreshold = 2,
        int criticalThreshold = 5)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _window = window ?? TimeSpan.FromMinutes(5);
        _warningThreshold = warningThreshold;
        _criticalThreshold = criticalThreshold;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var since = ctx.Now - _window;
            var recentFailures = _tracker.GetFailuresSince(since);
            if (recentFailures.Count == 0)
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

            // Group by agent
            var byAgent = recentFailures
                .GroupBy(f => f.AgentId)
                .Where(g => g.Count() >= _warningThreshold);

            foreach (var group in byAgent)
            {
                var count = group.Count();
                var severity = count >= _criticalThreshold
                    ? FlowFindingSeverity.Critical
                    : FlowFindingSeverity.Warning;

                var latest = group.OrderByDescending(f => f.OccurredAt).First();
                var displayName = latest.DisplayName ?? group.Key;
                var branches = string.Join(", ", group.Select(f => f.Branch).Where(b => b is not null).Distinct().Take(3));

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = severity,
                    TargetAgentId = group.Key,
                    TargetDisplayName = displayName,
                    TargetResource = branches,
                    Summary = $"{displayName}: {count} push failures in {_window.TotalMinutes:F0}m" +
                              (string.IsNullOrEmpty(branches) ? "" : $" on branch(es) {branches}"),
                    Rationale = $"Agent has failed {count} push/rebase attempts in the last {_window.TotalMinutes:F0} minutes. " +
                                $"Latest error: {latest.Error}. This typically indicates merge conflicts or " +
                                "diverged tracking branches that need manual resolution.",
                    DedupKey = $"push-failure:{group.Key}",
                    RecommendedFixId = $"rebase-reset:{group.Key}",
                    RecommendedFixDescription = $"Run 'git rebase --abort && git reset --hard origin/{latest.Branch ?? "main"}' " +
                                                $"in {displayName}'s worktree to reset to the remote state.",
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PushFailureDetector: error during detection");
        }

        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }
}
