using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// FlowMonitor action that escalates partially-approved PRs that have stalled without merging.
/// Handles findings from <see cref="Core.HealthMonitor.Detectors.UnmergedApprovedPrDetector"/>
/// Tier 2 (dedup key prefix <c>pr-merge-escalation:</c>).
///
/// <para>
/// This action emits a notification for human attention with gate-aware context — it tells
/// the operator whether the FinalPRApproval gate requires human review (so they know the
/// remaining approval must come from a human, not an agent). It does NOT auto-merge.
/// </para>
/// </summary>
public sealed class MergeEscalationAction : IFlowAction
{
    public string ActionType => "merge-escalation";

    private readonly GateNotificationService? _notifications;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _vdtConfig;
    private readonly IOptionsMonitor<FlowMonitorConfig>? _flowConfig;
    private readonly ILogger<MergeEscalationAction> _logger;

    public MergeEscalationAction(
        ILogger<MergeEscalationAction> logger,
        GateNotificationService? notifications = null,
        IOptionsMonitor<VirtualDevTeamConfig>? vdtConfig = null,
        IOptionsMonitor<FlowMonitorConfig>? flowConfig = null)
    {
        _logger = logger;
        _notifications = notifications;
        _vdtConfig = vdtConfig;
        _flowConfig = flowConfig;
    }

    public bool CanHandle(FlowFinding finding)
        => finding.DedupKey.StartsWith("pr-merge-escalation:", StringComparison.Ordinal);

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        // Parse PR number from dedup key: "pr-merge-escalation:{prNumber}"
        var parts = finding.DedupKey.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var prNumber))
        {
            _logger.LogWarning("MergeEscalationAction: unexpected dedup key format '{Key}'", finding.DedupKey);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = finding.TargetResource,
                Detail = $"Could not parse PR number from dedup key: {finding.DedupKey}",
            };
        }

        // Determine human gate status for actionable notification context
        var humanGateRequired = _vdtConfig?.CurrentValue.HumanInteraction.RequiresHuman(GateIds.FinalPRApproval) ?? false;
        var autoMergeEnabled = _flowConfig?.CurrentValue.EnableAutoMerge ?? false;

        string gateContext;
        if (humanGateRequired)
        {
            gateContext = "⚠️ **FinalPRApproval gate requires human review.** " +
                          "The remaining approval(s) must come from a human — agents cannot unblock this. " +
                          "Consider applying the `agent-stuck` label if the author agent needs attention.";
        }
        else if (autoMergeEnabled)
        {
            gateContext = "ℹ️ FinalPRApproval gate is auto-approved and auto-merge is enabled. " +
                          "The fully-approved PR merger (MergeApprovedPrAction) should handle this once all labels arrive. " +
                          "If labels are stuck, investigate reviewer agent status.";
        }
        else
        {
            gateContext = "ℹ️ FinalPRApproval gate is auto-approved but auto-merge is disabled. " +
                          "Once all reviewer agents apply their labels, merge will proceed via normal engineer flow.";
        }

        var notificationBody =
            $"PR #{prNumber} has partial approval but has stalled — " +
            $"it has one reviewer approval but is missing additional required approvals.\n\n" +
            gateContext;

        _logger.LogWarning(
            "MergeEscalationAction: PR #{PrNumber} partially approved but stalled " +
            "(humanGateRequired={HumanGate}, autoMerge={AutoMerge})",
            prNumber, humanGateRequired, autoMergeEnabled);

        if (_notifications is not null)
        {
            try
            {
                await _notifications.AddNotificationAsync(
                    "pr-merge-escalation",
                    notificationBody,
                    prNumber,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MergeEscalationAction: failed to send notification for PR #{PrNumber} (non-fatal)",
                    prNumber);
            }
        }
        else
        {
            _logger.LogInformation(
                "MergeEscalationAction: GateNotificationService not available, " +
                "escalation for PR #{PrNumber} logged only", prNumber);
        }

        return new FlowActionOutcome
        {
            Result = FlowActionResult.Success,
            Target = $"pr#{prNumber}",
            Detail = $"Notification emitted for partially-approved PR #{prNumber}. " +
                     $"Human gate: {(humanGateRequired ? "required" : "auto")}, " +
                     $"auto-merge: {(autoMergeEnabled ? "enabled" : "disabled")}.",
        };
    }

    public Task UndoAsync(FlowFinding finding, CancellationToken ct)
        => Task.CompletedTask;
}
