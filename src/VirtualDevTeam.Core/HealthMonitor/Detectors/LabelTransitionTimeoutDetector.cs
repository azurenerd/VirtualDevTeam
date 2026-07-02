using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.3 LabelTransitionTimeoutDetector — an open PR has sat in a label state
/// (`ready-for-review`, `architect-approved`, `architect-approved+tests-added`,
/// `pm-approved+tests-added`) without advancing for longer than the per-phase
/// threshold. Phase-1 (`ready-for-review` → architect) is given a tighter cap
/// because architects are typically fast; later phases (PM, merge) get longer caps
/// because they involve more agents.
///
/// <para>
/// **Conscious overlap:** this is intentionally redundant with
/// <see cref="IdleAgentPhaseStuckDetector"/> (which is keyed on the *agent*) and
/// <see cref="UnmergedApprovedPrDetector"/> (which only fires for the merge phase).
/// Three different angles on the same root condition give the operator a clearer
/// story in the audit log: agent-side, PR-side, and merge-side observations all
/// reinforce that something is stuck.
/// </para>
///
/// <para>
/// We use <see cref="PullRequestView.UpdatedAt"/> as the "label state since" proxy
/// — same trade-off as <see cref="StalePullRequestConflictDetector"/>. A comment
/// can reset UpdatedAt without changing labels; that means the detector
/// occasionally misses true label-stalls — but it never false-fires, which is the
/// safety property we want.
/// </para>
///
/// <para>
/// disable-te-toggle: when <see cref="ReviewConfig.TestEngineerReviews"/> is false the
/// final-merge phase classification "architect-approved + pm-approved + tests-added"
/// is replaced with "architect-approved + pm-approved" (TE-bypass). PRs in that
/// state are handled by <see cref="UnmergedApprovedPrDetector"/>; this detector
/// stops flagging them.
/// </para>
/// </summary>
public sealed class LabelTransitionTimeoutDetector : IFlowDetector
{
    public string DetectorId => "label-transition-timeout";

    private readonly ILogger<LabelTransitionTimeoutDetector> _logger;
    private readonly TimeSpan _phase1Threshold;
    private readonly TimeSpan _laterPhaseThreshold;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _config;
    private readonly IOptionsMonitor<FlowMonitorConfig>? _flowConfig;

    public LabelTransitionTimeoutDetector(
        ILogger<LabelTransitionTimeoutDetector> logger,
        TimeSpan? phase1Threshold = null,
        TimeSpan? laterPhaseThreshold = null,
        IOptionsMonitor<VirtualDevTeamConfig>? config = null,
        IOptionsMonitor<FlowMonitorConfig>? flowConfig = null)
    {
        _logger = logger;
        _phase1Threshold = phase1Threshold ?? TimeSpan.FromMinutes(15);
        _laterPhaseThreshold = laterPhaseThreshold ?? TimeSpan.FromMinutes(30);
        _config = config;
        _flowConfig = flowConfig;
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

                // Skip PRs that have already been escalated.
                if (pr.Labels.Any(l =>
                    string.Equals(l, "agent-stuck", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l, "human-review-required", StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Determine the phase + threshold.
                var (phaseLabel, threshold) = ClassifyPhase(pr);
                if (phaseLabel is null) continue;

                var lastTouched = pr.UpdatedAt ?? pr.CreatedAt;
                var stuckFor = ctx.Now - lastTouched;
                if (stuckFor < threshold) continue;

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = $"pr#{pr.Number}",
                    TargetAgentId = pr.AssignedAgent,
                    Summary = $"PR #{pr.Number} '{Truncate(pr.Title, 60)}' has sat in '{phaseLabel}' state " +
                              $"for {FormatDuration(stuckFor)} (threshold {FormatDuration(threshold)}).",
                    Rationale = "The PR's label state has not advanced for longer than the threshold for this " +
                                "review phase. Phase-1 (ready-for-review → architect-approved) is bounded " +
                                "tightly; later phases get more time because they involve more reviewers. " +
                                "Development phase (in-progress → ready-for-review) detects stalled implementations. " +
                                "Operator should check the target reviewer's loop or escalate via the dashboard.",
                    DedupKey = $"label-transition-timeout:{pr.Number}:{phaseLabel}",
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LabelTransitionTimeoutDetector tick failed (non-fatal)");
        }
        return findings;
    }

    private (string? PhaseLabel, TimeSpan Threshold) ClassifyPhase(PullRequestView pr)
    {
        bool has(string label) => pr.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
        bool teDisabled = _config?.CurrentValue?.Review?.TestEngineerReviews == false;

        // Final phase: all approvals in but unmerged — let UnmergedApprovedPrDetector handle.
        if (teDisabled)
        {
            if (has("architect-approved") && has("pm-approved"))
                return (null, TimeSpan.Zero);
        }
        else
        {
            if (has("architect-approved") && has("pm-approved") && has("tests-added"))
                return (null, TimeSpan.Zero);
        }

        // Phase-3: pm-approved but not all of architect/tests yet (unusual order)
        if (has("pm-approved"))
            return ("pm-approved", _laterPhaseThreshold);

        // Phase-2: architect approved, waiting for PM + TE
        if (has("architect-approved"))
            return ("architect-approved", _laterPhaseThreshold);

        // Phase-1: ready-for-review without architect approval
        if (has("ready-for-review"))
            return ("ready-for-review", _phase1Threshold);

        // Development phase: has in-progress but not yet ready-for-review.
        // Catches PRs where implementation has stalled (e.g., PR #6 stuck 2h).
        if (has("in-progress") && !has("ready-for-review"))
        {
            var devThreshold = _flowConfig?.CurrentValue?.DevelopmentPhaseThresholdMinutes ?? 60;
            return ("in-progress", TimeSpan.FromMinutes(devThreshold));
        }

        return (null, TimeSpan.Zero);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        return $"{(int)ts.TotalHours}h {ts.Minutes}m";
    }
}
