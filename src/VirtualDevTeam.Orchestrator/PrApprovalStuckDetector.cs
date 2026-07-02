using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Detects open PRs stuck at a specific review-approval stage for longer than
/// <see cref="StuckThreshold"/> and emits a Critical finding so the escalation
/// ladder can propose a targeted action to unstick them.
///
/// <para>
/// Three stages are monitored (in pipeline order):
/// <list type="bullet">
/// <item><c>ready-for-review</c> present but <c>architect-approved</c> absent → Architect overdue.</item>
/// <item><c>architect-approved</c> present but <c>tests-added</c> absent → TestEngineer overdue.</item>
/// <item><c>tests-added</c> present but <c>pm-approved</c> absent → PM overdue.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Relationship to similar detectors:</b>
/// <c>MissingReviewerDetector</c> fires a Warning at 10 min and targets the reviewer
/// agent directly for a bus nudge. This detector fires Critical at 15 min and is
/// intended to trigger the operator-approval flow (proposed FlowActions) rather than
/// an automated nudge. <c>UnmergedApprovedPrDetector</c> fires when a PR has BOTH
/// architect-approved AND pm-approved and still hasn't been merged — a different root cause.
/// </para>
///
/// <para>
/// <b>Cost model:</b> shares the per-tick cached
/// <see cref="IPlatformView.ListOpenPullRequestsAsync"/> result with other detectors —
/// zero additional API calls.
/// </para>
/// </summary>
public sealed class PrApprovalStuckDetector : IFlowDetector
{
    public string DetectorId => "pr-approval-stuck";

    private const string ReadyForReviewLabel   = "ready-for-review";
    private const string ArchitectApprovedLabel = "architect-approved";
    private const string TestsAddedLabel        = "tests-added";
    private const string PmApprovedLabel        = "pm-approved";
    private const string AgentStuckLabel        = "agent-stuck";
    private const string HumanReviewLabel       = "human-review-required";

    private readonly TimeSpan _stuckThreshold;
    private readonly ILogger<PrApprovalStuckDetector> _logger;

    public PrApprovalStuckDetector(
        ILogger<PrApprovalStuckDetector> logger,
        TimeSpan? stuckThreshold = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stuckThreshold = stuckThreshold ?? TimeSpan.FromMinutes(15);
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

                // PRs already escalated or awaiting human review don't need another nudge.
                if (pr.Labels.Contains(AgentStuckLabel, StringComparer.OrdinalIgnoreCase)) continue;
                if (pr.Labels.Contains(HumanReviewLabel, StringComparer.OrdinalIgnoreCase)) continue;

                var (blockingRole, nextLabel, phaseDesc) = ClassifyStage(pr);
                if (blockingRole is null) continue;

                // Use UpdatedAt as the "stuck since" proxy — a comment resets it which
                // means we can under-count true stall duration, but we never false-fire.
                var lastTouched = pr.UpdatedAt ?? pr.CreatedAt;
                var stuckFor = ctx.Now - lastTouched;
                if (stuckFor < _stuckThreshold) continue;

                var reviewerAgent = ctx.Agents.FirstOrDefault(a =>
                    string.Equals(a.Role, blockingRole, StringComparison.OrdinalIgnoreCase));

                findings.Add(new FlowFinding
                {
                    Id               = Guid.NewGuid().ToString("N"),
                    DetectedAt       = ctx.Now,
                    DetectorId       = DetectorId,
                    Severity         = FlowFindingSeverity.Critical,
                    TargetResource   = $"pr#{pr.Number}",
                    TargetAgentId    = reviewerAgent?.Id,
                    TargetDisplayName = reviewerAgent?.DisplayName,
                    Summary          = $"PR #{pr.Number} stuck waiting for {blockingRole} review " +
                                       $"({FormatDuration(stuckFor)}; phase: {phaseDesc})",
                    Rationale        = $"PR has been in '{phaseDesc}' state for {FormatDuration(stuckFor)} " +
                                       $"without receiving the '{nextLabel}' label. " +
                                       $"Last touch at {lastTouched:o}. " +
                                       $"Proposed action: nudge {blockingRole} via bus or post an explicit ask on the PR.",
                    DedupKey         = $"pr-approval-stuck:{pr.Number}:{nextLabel}",
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PrApprovalStuckDetector tick failed (non-fatal)");
        }
        return findings;
    }

    /// <summary>
    /// Identifies the approval stage a PR is stuck in.
    /// Returns (null, null, empty) when the PR is not stuck at a known stage.
    /// </summary>
    private static (string? Role, string? NextLabel, string PhaseDesc) ClassifyStage(PullRequestView pr)
    {
        bool has(string label) => pr.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

        if (has(ReadyForReviewLabel) && !has(ArchitectApprovedLabel))
            return ("Architect", ArchitectApprovedLabel, "awaiting-architect-approval");

        if (has(ArchitectApprovedLabel) && !has(TestsAddedLabel))
            return ("TestEngineer", TestsAddedLabel, "awaiting-tests-added");

        if (has(TestsAddedLabel) && !has(PmApprovedLabel))
            return ("ProgramManager", PmApprovedLabel, "awaiting-pm-approval");

        return (null, null, string.Empty);
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:0}s";
        if (ts.TotalMinutes < 60) return $"{ts.TotalMinutes:0}m";
        if (ts.TotalHours < 24)   return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{(int)ts.TotalDays}d {ts.Hours}h";
    }
}
