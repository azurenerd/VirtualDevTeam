using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// post-run-pr-merge-conflict-detector: surfaces open PRs whose <c>MergeableState</c>
/// has been "dirty" (i.e., conflicting with the base branch) longer than a threshold.
/// In the 2026-05-10 run, PR #1347 sat in this state for 3 hours because the SE
/// Leader's auto-recovery (TryCloseAndRecreatePRAsync) only fires while the agent's
/// merge loop is active — once the SE Leader moved on to "Waiting for integration
/// PR", the conflict went unattended.
///
/// **No paired action ships today.** The finding is intentionally Critical, which
/// triggers the existing T1.5 FixRecommendation flow (a /plan + rubber-duck plan is
/// generated and surfaced to the Approvals page). Operators rebase manually based on
/// that plan. Future enhancement: add a <c>rebase-pr</c> action that wakes SE Leader
/// for the specific PR, or runs git rebase + force-push directly.
///
/// **Cost model**: relies on T1.1's <see cref="IPlatformView.ListOpenPullRequestsAsync"/>
/// — that's already cached per-tick so multiple detectors sharing the context only pay
/// the API cost once. <see cref="PullRequestView.MergeableState"/> may be null on ADO
/// (which doesn't surface a directly equivalent field today); detector treats null as
/// "unknown, skip" rather than firing false positives.
/// </summary>
public sealed class StalePullRequestConflictDetector : IFlowDetector
{
    public string DetectorId => "pr-merge-conflict";

    private readonly ILogger<StalePullRequestConflictDetector> _logger;
    private readonly TimeSpan _threshold;

    public StalePullRequestConflictDetector(
        ILogger<StalePullRequestConflictDetector> logger,
        TimeSpan? threshold = null)
    {
        _logger = logger;
        // Default 15m: long enough to ride out transient "behind" states during a
        // merge race; short enough to catch real conflicts before the operator does.
        _threshold = threshold ?? TimeSpan.FromMinutes(15);
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var openPrs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            foreach (var pr in openPrs)
            {
                // GitHub: "dirty" = conflicts with base. "behind" = base advanced but mergeable
                // after rebase (NOT a hard conflict). "unknown" / null = transient — skip.
                // ADO: MergeableState is generally not surfaced; null → skip.
                var state = pr.MergeableState ?? string.Empty;
                if (!string.Equals(state, "dirty", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Use UpdatedAt as a proxy for "stuck since". Not perfect — a PR can be
                // updated by a comment without resolving the conflict — but it's the only
                // platform-portable signal we have. If UpdatedAt is recent, give the PR a
                // grace period before flagging.
                var lastTouched = pr.UpdatedAt ?? pr.CreatedAt;
                var stuckFor = ctx.Now - lastTouched;
                if (stuckFor < _threshold) continue;

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    // Critical so the T1.5 FixRecommendation flow generates a plan automatically.
                    Severity = FlowFindingSeverity.Critical,
                    TargetResource = $"pr#{pr.Number}",
                    TargetAgentId = pr.AssignedAgent,
                    Summary = $"PR #{pr.Number} '{Truncate(pr.Title, 80)}' has had merge conflicts for " +
                              $"{FormatDuration(stuckFor)} (no auto-rebase triggered).",
                    Rationale = "The PR's MergeableState is 'dirty' (base branch has changes that conflict " +
                                "with the PR's commits), and the PR hasn't been updated in over " +
                                $"{FormatDuration(_threshold)}. Engineer agents auto-rebase only while " +
                                "actively in their merge loop; if the conflict surfaced after the agent moved " +
                                "to a different state (e.g., waiting for integration PR), the rebase never runs. " +
                                "Operator should rebase manually OR an automated rebase-pr action should be wired.",
                    DedupKey = $"pr-merge-conflict:{pr.Number}",
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
            _logger.LogWarning(ex, "StalePullRequestConflictDetector tick failed (non-fatal)");
        }

        return findings;
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
