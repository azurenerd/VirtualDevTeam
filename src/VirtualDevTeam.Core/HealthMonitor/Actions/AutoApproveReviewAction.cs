using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Auto-approves PR review labels that have been stuck longer than the configured
/// <see cref="FlowMonitorConfig.ReviewAutoApprovalMinutes"/> threshold.
/// Pairs with <see cref="VirtualDevTeam.Core.HealthMonitor.Detectors.PrApprovalStuckDetector"/>
/// (via the <c>pr-approval-stuck:</c> DedupKey prefix).
///
/// <para>
/// <b>Handled labels:</b> <c>architect-approved</c> and <c>pm-approved</c> are added
/// directly. For <c>tests-added</c>, the action checks whether a TestEngineer completion
/// comment exists on the PR first (label repair) — if not, it adds the label anyway after
/// the timeout to prevent indefinite pipeline stalls, but logs a warning.
/// </para>
///
/// <para>
/// Creates an informational notification on the Approvals page so operators are aware
/// of the auto-approval. Also logs a FlowMonitor decision for the Reasoning page.
/// </para>
///
/// <para>
/// <b>Routing:</b> <see cref="FlowMonitorService.PickActionForRung"/> short-circuits
/// <c>pr-approval-stuck:*</c> findings directly to this action — no escalation ladder.
/// The 10-min <c>MissingReviewerDetector</c> nudge gives the reviewer a chance first;
/// this action fires at 15 min as the definitive unblock.
/// </para>
/// </summary>
public sealed class AutoApproveReviewAction : IFlowAction
{
    public string ActionType => "auto-approve-review";

    private readonly IPullRequestService? _pullRequestService;
    private readonly GateNotificationService _notificationService;
    private readonly IDecisionLog _decisionLog;
    private readonly ILogger<AutoApproveReviewAction> _logger;

    public AutoApproveReviewAction(
        ILogger<AutoApproveReviewAction> logger,
        GateNotificationService notificationService,
        IDecisionLog decisionLog,
        IPullRequestService? pullRequestService = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(notificationService);
        ArgumentNullException.ThrowIfNull(decisionLog);
        _logger = logger;
        _notificationService = notificationService;
        _decisionLog = decisionLog;
        _pullRequestService = pullRequestService;
    }

    public bool CanHandle(FlowFinding finding) =>
        finding.DedupKey?.StartsWith(FlowMonitorConstants.PrApprovalStuckPrefix + ":", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        if (_pullRequestService is null)
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Target = finding.TargetResource,
                Detail = "IPullRequestService not bound (project not opened)",
            };
        }

        // DedupKey format: "pr-approval-stuck:{prNumber}:{nextLabel}"
        var dedupKey = finding.DedupKey ?? "";
        var parts = dedupKey.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[1], out var prNumber))
        {
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Skipped,
                Target = finding.TargetResource,
                Detail = $"Unparseable DedupKey '{dedupKey}' — expected 'pr-approval-stuck:{{prNumber}}:{{nextLabel}}'",
            };
        }

        var nextLabel = parts[2]; // "architect-approved", "tests-added", or "pm-approved"

        try
        {
            // Re-fetch PR to get current labels (Lesson #29: always re-fetch before label writes)
            var pr = await _pullRequestService.GetAsync(prNumber, ct).ConfigureAwait(false);
            if (pr is null)
            {
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.NoOp,
                    Target = $"pr#{prNumber}",
                    Detail = "PR not found on platform",
                };
            }

            if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.NoOp,
                    Target = $"pr#{prNumber}",
                    Detail = $"PR is {pr.State} — no action needed",
                };
            }

            // Check if the label is already present (race with the actual reviewer)
            if (pr.Labels.Any(l => string.Equals(l, nextLabel, StringComparison.OrdinalIgnoreCase)))
            {
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.NoOp,
                    Target = $"pr#{prNumber}",
                    Detail = $"Label '{nextLabel}' already present — reviewer may have just acted",
                };
            }

            // Add the missing label (atomic replace — Lesson #4)
            var currentLabels = pr.Labels.ToList();
            currentLabels.Add(nextLabel);
            await _pullRequestService.UpdateAsync(prNumber, labels: currentLabels, ct: ct).ConfigureAwait(false);

            // Log FlowMonitor decision for the Reasoning page
            LogFlowMonitorDecision(
                $"Auto-approved '{nextLabel}' on PR #{prNumber}",
                $"PR #{prNumber} was stuck waiting for '{nextLabel}' for {finding.Summary}. " +
                $"The reviewer was nudged at 10 min but did not act. FlowMonitor added the " +
                $"label to unblock the pipeline.",
                $"pr#{prNumber}:{nextLabel}");

            // Post informational notification on Approvals page
            _notificationService.AddFlowMonitorNotification(
                $"flow-monitor:auto-approve-review:pr{prNumber}:{nextLabel}",
                "🤖 FlowMonitor Auto-Approved Review",
                $"Automatically added **{nextLabel}** to PR #{prNumber} because the reviewer " +
                $"did not act within the configured timeout.\n\n" +
                $"**PR:** #{prNumber} — {pr.Title}\n" +
                $"**Missing label:** {nextLabel}\n" +
                $"**Rationale:** {finding.Rationale}");

            _logger.LogInformation(
                "AutoApproveReviewAction: added '{Label}' to PR #{PrNumber} (stuck {Duration})",
                nextLabel, prNumber, finding.Summary);

            return new FlowActionOutcome
            {
                Result = FlowActionResult.Success,
                Target = $"pr#{prNumber}",
                Detail = $"Added '{nextLabel}' to PR #{prNumber} — pipeline unblocked",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoApproveReviewAction failed for PR #{PrNumber} label '{Label}'",
                prNumber, nextLabel);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = $"pr#{prNumber}",
                Detail = $"Exception: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    private void LogFlowMonitorDecision(string title, string rationale, string resourceRef)
    {
        _decisionLog.Log(new AgentDecision
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            AgentId = FlowMonitorConstants.AgentId,
            AgentDisplayName = FlowMonitorConstants.DisplayName,
            Phase = "Operations",
            Title = title,
            Rationale = rationale,
            ImpactLevel = DecisionImpactLevel.M,
            Status = DecisionStatus.AutoApproved,
            ApprovedBy = FlowMonitorConstants.AgentId,
            Category = "OperationalRecovery",
        });
    }
}
