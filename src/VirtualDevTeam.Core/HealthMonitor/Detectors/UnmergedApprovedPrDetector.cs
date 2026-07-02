using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// post-run3-merge-bottleneck: surfaces open PRs that have all required approval
/// labels (<c>architect-approved</c> AND <c>pm-approved</c>) but have not been merged
/// after sitting idle for a threshold. Designed to break the SE Leader merge bottleneck
/// observed in the 2026-05-10 multi-PR run (cdbb396b), where PR #1394 sat fully-approved
/// for 30+ minutes because the SE Leader's merge loop was blocked running Strategy
/// candidates for a different task.
///
/// <para>
/// **What the detector does NOT do:** force-merge PRs, modify code, or change reviewer
/// gates. It only emits a finding describing the stuck-merge condition. The paired
/// <c>merge-approved-pr</c> action performs the actual safety-net merge, with its own
/// double-check of labels + mergeability at execution time.
/// </para>
///
/// <para>
/// **Safety properties of this detection signal:**
/// 1. Requires BOTH <c>architect-approved</c> AND <c>pm-approved</c> — the same dual-gate
///    SE's own merge loop uses (PullRequestWorkflow.GetRequiredReviewers).
/// 2. Skips PRs whose <c>MergeableState</c> is "dirty" (conflicts) or "blocked"
///    (branch protection) — those need engineer-driven recovery, not a flow nudge.
/// 3. Idle threshold (default 5 min via <see cref="FlowMonitorConfig.MergeApprovedPrStuckMinutes"/>)
///    is well past any legitimate SE merge-poll cycle (~15s).
/// 4. Skips agent-stuck or human-review-required PRs — those have explicit human gates.
/// </para>
///
/// <para>
/// **Cost model:** Uses the per-tick cached <see cref="IPlatformView.ListOpenPullRequestsAsync"/>
/// — zero extra API calls if other detectors already needed the open PR list this tick.
/// </para>
/// </summary>
public sealed class UnmergedApprovedPrDetector : IFlowDetector
{
    public string DetectorId => "unmerged-approved-pr";

    private const string ArchitectApprovedLabel = "architect-approved";
    private const string PmApprovedLabel = "pm-approved";
    private const string ReadyForReviewLabel = PullRequestWorkflow.Labels.ReadyForReview;
    // NoMessyCodePlan Theme 2: reference canonical Core constant.
    private const string AgentStuckLabel = IssueWorkflow.Labels.AgentStuck;
    private const string HumanReviewLabel = "human-review-required";

    private readonly ILogger<UnmergedApprovedPrDetector> _logger;
    private readonly TimeSpan _stuckThreshold;
    private readonly TimeSpan _partialApprovalThreshold;

    public UnmergedApprovedPrDetector(
        ILogger<UnmergedApprovedPrDetector> logger,
        TimeSpan? stuckThreshold = null,
        TimeSpan? partialApprovalThreshold = null)
    {
        _logger = logger;
        // 5m default: SE merge polling cycles run every ~15s, so anything past 5m is
        // a real stuck-merge condition (agent busy / crashed / re-spawning).
        _stuckThreshold = stuckThreshold ?? TimeSpan.FromMinutes(5);
        // 90m default for partial-approval Tier 2: one reviewer approved but others
        // haven't, and the PR has been idle for a long time.
        _partialApprovalThreshold = partialApprovalThreshold ?? TimeSpan.FromMinutes(90);
    }

    public async Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var openPrs = await ctx.Platform.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
            foreach (var pr in openPrs)
            {
                if (ct.IsCancellationRequested) break;

                // Hard gates: must have BOTH dual-reviewer approvals.
                var hasArchitect = pr.Labels.Contains(ArchitectApprovedLabel, StringComparer.OrdinalIgnoreCase);
                var hasPm = pr.Labels.Contains(PmApprovedLabel, StringComparer.OrdinalIgnoreCase);
                if (!hasArchitect || !hasPm) continue;

                // Skip PRs already escalated for human attention — a flow action would
                // step on the human's review here. EXCEPTION: if ALL approval labels are
                // present (architect + PM + tests-added), the PR is ready to merge despite
                // the stuck label — the agent-stuck detector fires on "awaiting review"
                // status but the review is actually complete. Don't let it block the merge.
                var hasTestsAdded = pr.Labels.Contains("tests-added", StringComparer.OrdinalIgnoreCase);
                var allApprovalsPresent = hasArchitect && hasPm && hasTestsAdded;
                if (pr.Labels.Contains(AgentStuckLabel, StringComparer.OrdinalIgnoreCase) && !allApprovalsPresent) continue;
                if (pr.Labels.Contains(HumanReviewLabel, StringComparer.OrdinalIgnoreCase)) continue;

                // Skip PRs with known merge problems — those are handled by
                // StalePullRequestConflictDetector and need engineer-driven rebase.
                var state = pr.MergeableState ?? string.Empty;
                if (string.Equals(state, "dirty", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(state, "blocked", StringComparison.OrdinalIgnoreCase)) continue;

                // Idle threshold: UpdatedAt is the best portable signal for "no recent
                // attention" — a fresh approval comment counts as activity, so anything
                // older than threshold means the merging agent hasn't picked it up.
                var lastTouched = pr.UpdatedAt ?? pr.CreatedAt;
                var idleFor = ctx.Now - lastTouched;
                if (idleFor < _stuckThreshold) continue;

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    // Warning, not Critical: the action handler exists and is safe;
                    // no FixRecommendation /plan should auto-generate for this.
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = $"pr#{pr.Number}",
                    TargetAgentId = pr.AssignedAgent,
                    Summary = $"PR #{pr.Number} '{Truncate(pr.Title, 80)}' fully approved " +
                              $"(architect + PM) but unmerged for {FormatDuration(idleFor)}.",
                    Rationale = "Both required approval labels are present and the PR is " +
                                "mergeable, but the engineer agent that would normally merge it " +
                                $"hasn't done so in {FormatDuration(_stuckThreshold)} " +
                                "(likely busy on another task or restarted). The paired " +
                                "merge-approved-pr action will safely merge it, respecting the " +
                                "same approval gates the engineer uses.",
                    DedupKey = $"unmerged-approved-pr:{pr.Number}",
                });
            }

            // ── Tier 2: Partial-approval escalation ─────────────────────────
            // PRs with ready-for-review + exactly one of (architect-approved XOR
            // pm-approved) that have been idle beyond PrMergeEscalationMinutes.
            // These are stuck waiting for a second reviewer; we escalate to
            // human attention before the delay becomes excessive.
            foreach (var pr in openPrs)
            {
                if (ct.IsCancellationRequested) break;

                var hasReady = pr.Labels.Contains(ReadyForReviewLabel, StringComparer.OrdinalIgnoreCase);
                if (!hasReady) continue;

                var hasArchitect = pr.Labels.Contains(ArchitectApprovedLabel, StringComparer.OrdinalIgnoreCase);
                var hasPm = pr.Labels.Contains(PmApprovedLabel, StringComparer.OrdinalIgnoreCase);

                // Skip if both approved (Tier 1 handles that) or neither approved
                if (hasArchitect == hasPm) continue;

                // Skip PRs already escalated for human attention
                if (pr.Labels.Contains(AgentStuckLabel, StringComparer.OrdinalIgnoreCase)) continue;
                if (pr.Labels.Contains(HumanReviewLabel, StringComparer.OrdinalIgnoreCase)) continue;

                var lastTouched = pr.UpdatedAt ?? pr.CreatedAt;
                var idleFor = ctx.Now - lastTouched;
                if (idleFor < _partialApprovalThreshold) continue;

                var approvedBy = hasArchitect ? "Architect" : "PM";
                var missingFrom = hasArchitect ? "PM" : "Architect";

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = $"pr#{pr.Number}",
                    TargetAgentId = pr.AssignedAgent,
                    Summary = $"PR #{pr.Number} '{Truncate(pr.Title, 80)}' has {approvedBy} approval " +
                              $"but is waiting for {missingFrom} review for {FormatDuration(idleFor)}.",
                    Rationale = $"The PR has been marked ready-for-review and received {approvedBy} " +
                                $"approval, but the {missingFrom} agent has not reviewed it in " +
                                $"{FormatDuration(_partialApprovalThreshold)}. This may indicate " +
                                "the reviewing agent is stuck, busy with another task, or crashed.",
                    DedupKey = $"pr-merge-escalation:{pr.Number}",
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
            _logger.LogWarning(ex, "UnmergedApprovedPrDetector tick failed (non-fatal)");
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
