using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects open PRs that have been waiting for a specific reviewer's approval label beyond
/// a threshold, and emits a finding that <c>NudgeReviewerAction</c> can act on with a
/// targeted bus message to the reviewer agent.
///
/// <para>
/// Coverage:
/// <list type="bullet">
/// <item>PR has <c>ready-for-review</c> but no <c>architect-approved</c> → Architect overdue.</item>
/// <item>PR has <c>architect-approved</c> but no <c>pm-approved</c> → PM overdue.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Overlap with LabelTransitionTimeoutDetector (T2.3):</b> intentional redundancy.
/// <c>LabelTransitionTimeoutDetector</c> targets the PR's owning engineer; this detector
/// targets the reviewer directly via a <c>ReviewNudgeMessage</c> bus message.
/// Three different angles on the same root condition give the operator a richer audit trail.
/// </para>
///
/// <para>
/// <b>Cost model:</b> Uses the per-tick cached
/// <see cref="IPlatformView.ListOpenPullRequestsAsync"/> — zero extra API calls when any
/// other detector has already requested the open PR list this tick.
/// </para>
/// </summary>
public sealed class MissingReviewerDetector : IFlowDetector
{
    public string DetectorId => "missing-reviewer";

    private const string ReadyForReviewLabel = "ready-for-review";
    private const string ArchitectApprovedLabel = "architect-approved";
    private const string PmApprovedLabel = "pm-approved";
    private const string AgentStuckLabel = "agent-stuck";
    private const string HumanReviewLabel = "human-review-required";

    // Const not a config field per operator instruction — review latency below 10m is
    // considered normal polling variance; anything over is a genuine missed-review signal.
    private static readonly TimeSpan _stuckThreshold = TimeSpan.FromMinutes(10);

    private readonly ILogger<MissingReviewerDetector> _logger;

    public MissingReviewerDetector(ILogger<MissingReviewerDetector> logger)
    {
        _logger = logger;
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

                // Skip PRs already escalated for human attention — a reviewer nudge would
                // interfere with any ongoing human review cycle.
                if (pr.Labels.Contains(AgentStuckLabel, StringComparer.OrdinalIgnoreCase)) continue;
                if (pr.Labels.Contains(HumanReviewLabel, StringComparer.OrdinalIgnoreCase)) continue;

                // Skip PRs in active rework — the SE reclaimed the PR after reviewer feedback
                // and is making changes. The `in-progress` label alongside `ready-for-review`
                // indicates the engineer is iterating, not that the reviewer is idle.
                if (pr.Labels.Contains("in-progress", StringComparer.OrdinalIgnoreCase)) continue;

                // Determine which reviewer is overdue.
                var (reviewerRole, phaseDescription) = ClassifyMissingReviewer(pr);
                if (reviewerRole is null) continue;

                // Use UpdatedAt as the "in this label state since" proxy (same approach as
                // LabelTransitionTimeoutDetector — a comment resets UpdatedAt without changing
                // labels, which means we occasionally under-count stall duration, but we never
                // false-fire, which is the safety property we want).
                var lastTouched = pr.UpdatedAt ?? pr.CreatedAt;
                var idleFor = ctx.Now - lastTouched;
                if (idleFor < _stuckThreshold) continue;

                // Find the reviewer agent by role to get their bus ID for targeted nudging.
                // If the agent isn't registered yet (e.g., pre-spawn), TargetAgentId is null
                // and NudgeReviewerAction will skip gracefully.
                var reviewerAgent = ctx.Agents.FirstOrDefault(a =>
                    string.Equals(a.Role, reviewerRole, StringComparison.OrdinalIgnoreCase));

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    TargetResource = $"pr#{pr.Number}",
                    TargetAgentId = reviewerAgent?.Id,
                    TargetDisplayName = reviewerAgent?.DisplayName,
                    Summary = $"PR #{pr.Number} '{Truncate(pr.Title, 70)}' awaiting {reviewerRole} review " +
                              $"for {FormatDuration(idleFor)} (phase: {phaseDescription}).",
                    Rationale = $"The PR is in '{phaseDescription}' state and the {reviewerRole} agent " +
                                $"has not applied the expected approval label within the " +
                                $"{_stuckThreshold.TotalMinutes:0}-minute threshold. " +
                                "NudgeReviewerAction will publish a ReviewNudgeMessage directly to the " +
                                "reviewer's bus subscription so it wakes up and processes the PR immediately.",
                    DedupKey = $"missing-reviewer:{pr.Number}:{reviewerRole.ToLowerInvariant()}",
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
            _logger.LogWarning(ex, "MissingReviewerDetector tick failed (non-fatal)");
        }

        return findings;
    }

    /// <summary>
    /// Maps a PR's current label state to the reviewer role that is overdue.
    /// Returns (null, empty) when no reviewer is missing or the PR is not in a reviewable state.
    /// </summary>
    private static (string? Role, string PhaseDescription) ClassifyMissingReviewer(PullRequestView pr)
    {
        bool has(string label) => pr.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

        // Phase 2: architect approved, PM hasn't reviewed yet.
        if (has(ArchitectApprovedLabel) && !has(PmApprovedLabel))
            return ("ProgramManager", "awaiting-pm-approval");

        // Phase 1: ready-for-review, architect hasn't reviewed yet.
        if (has(ReadyForReviewLabel) && !has(ArchitectApprovedLabel))
            return ("Architect", "awaiting-architect-approval");

        return (null, string.Empty);
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
