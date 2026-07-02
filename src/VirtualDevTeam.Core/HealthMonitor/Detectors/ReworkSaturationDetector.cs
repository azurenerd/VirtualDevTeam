using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.4 ReworkSaturationDetector — early warning when an open PR has accumulated
/// many unresolved review threads, indicating the rework cycle is approaching the
/// configured cap before the engineer's force-approve fallback fires. Operator
/// can intervene before AI tokens are wasted on a doomed PR.
///
/// <para>
/// **Heuristic:** we use unresolved-review-thread count as a proxy for accumulated
/// CHANGES_REQUESTED reviews. This isn't a perfect 1:1 mapping (a single review
/// with 5 inline comments shows as 5 threads), but it's monotonic — more threads
/// almost always means more rework. The threshold defaults to 5 unresolved
/// threads, which empirically matches the saturation point observed in
/// finding #1 (PM posted CHANGES_REQUESTED 5 times in 23 minutes on PR #1216).
/// </para>
///
/// <para>
/// **Cost:** uses <see cref="IPlatformView.ListUnresolvedThreadsAsync"/> which is
/// per-PR-cached for the tick. To bound API cost we only query threads for PRs
/// that already have at least one approval-related label (`changes-requested` is
/// not a label today, so we use a presence-of-reviewer-engagement proxy: any of
/// `ready-for-review`, `architect-approved`, `pm-approved`).
/// </para>
/// </summary>
public sealed class ReworkSaturationDetector : IFlowDetector
{
    public string DetectorId => "rework-saturation";

    private readonly ILogger<ReworkSaturationDetector> _logger;
    private readonly int _threadThreshold;

    public ReworkSaturationDetector(
        ILogger<ReworkSaturationDetector> logger,
        int threadThreshold = 5)
    {
        _logger = logger;
        _threadThreshold = Math.Max(2, threadThreshold);
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var prs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            foreach (var pr in prs)
            {
                if (ct.IsCancellationRequested) break;

                // Skip PRs not yet engaged with reviewers — no reviews => no rework.
                if (!pr.Labels.Any(l =>
                    string.Equals(l, "ready-for-review", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l, "architect-approved", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l, "pm-approved", StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Skip PRs already escalated.
                if (pr.Labels.Any(l =>
                    string.Equals(l, "agent-stuck", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l, "human-review-required", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var threads = await ctx.Platform.ListUnresolvedThreadsAsync(pr.Number, ct).ConfigureAwait(false);
                if (threads.Count < _threadThreshold) continue;

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = $"pr#{pr.Number}",
                    TargetAgentId = pr.AssignedAgent,
                    Summary = $"PR #{pr.Number} '{Truncate(pr.Title, 60)}' has {threads.Count} unresolved review " +
                              $"thread(s) — approaching rework saturation.",
                    Rationale = "Many unresolved review threads typically signal an over-rotated rework cycle: " +
                                "engineer is reworking, reviewers find new issues each round, and force-approve " +
                                "is approaching. Early warning lets the operator intervene (escalate, close + " +
                                "split the PR, or accept current state) before more AI tokens are spent.",
                    DedupKey = $"rework-saturation:{pr.Number}:{threads.Count}",
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReworkSaturationDetector tick failed (non-fatal)");
        }
        return findings;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
