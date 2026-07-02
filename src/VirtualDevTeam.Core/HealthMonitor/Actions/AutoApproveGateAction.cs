using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Auto-approves gates and decisions that have exceeded the configured
/// <see cref="FlowMonitorConfig.AutoApprovalMinutes"/> threshold.
/// Pairs with <see cref="Detectors.GateStuckDetector"/>.
///
/// Creates an informational notification on the Approvals page with a Dismiss button
/// (not Approve/Reject) so operators are aware of the auto-approval.
/// </summary>
public sealed class AutoApproveGateAction : IFlowAction
{
    private readonly IGateCheckService _gateCheck;
    private readonly DecisionGateService _decisionGate;
    private readonly IDecisionLog _decisionLog;
    private readonly GateNotificationService _notificationService;
    private readonly ILogger<AutoApproveGateAction> _logger;

    public string ActionType => "auto-approve-gate";

    public AutoApproveGateAction(
        IGateCheckService gateCheck,
        DecisionGateService decisionGate,
        IDecisionLog decisionLog,
        GateNotificationService notificationService,
        ILogger<AutoApproveGateAction> logger)
    {
        _gateCheck = gateCheck;
        _decisionGate = decisionGate;
        _decisionLog = decisionLog;
        _notificationService = notificationService;
        _logger = logger;
    }

    public bool CanHandle(FlowFinding finding) =>
        finding.DedupKey?.StartsWith(FlowMonitorConstants.GateStuckPrefix) == true;

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        try
        {
            var dedupKey = finding.DedupKey ?? "";

            if (dedupKey.StartsWith($"{FlowMonitorConstants.GateStuckPrefix}:decision:"))
            {
                // Auto-approve a decision gate
                var decisionId = dedupKey[$"{FlowMonitorConstants.GateStuckPrefix}:decision:".Length..];
                var decision = _decisionLog.GetDecision(decisionId);
                if (decision is null)
                    return new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = "Decision not found" };

                if (decision.Status is not DecisionStatus.Pending)
                    return new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = $"Already {decision.Status}" };

                var feedback = $"Auto-approved by FlowMonitor after pending for >{finding.Summary}";
                _decisionGate.ApproveDecision(decisionId, feedback, FlowMonitorConstants.AgentId);

                // Log FlowMonitor's own decision (bypasses gating)
                LogFlowMonitorDecision($"Auto-approved decision: {decision.Title}", finding.Rationale, decisionId);

                // Create informational notification on Approvals page
                PostFlowMonitorNotification(
                    $"flow-monitor:auto-approve:decision:{decisionId}",
                    $"🤖 FlowMonitor Auto-Approved Decision",
                    $"Automatically approved **{decision.Title}** (by {decision.AgentDisplayName}) because it was waiting too long.\n\n" +
                    $"**Why:** {decision.Rationale}\n**Impact:** {decision.ImpactLevel}");

                _logger.LogInformation("FlowMonitor auto-approved decision {Id}: {Title}", decisionId, decision.Title);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Success,
                    Target = $"decision:{decisionId}",
                    Detail = $"Auto-approved decision '{decision.Title}'"
                };
            }
            else if (dedupKey.StartsWith($"{FlowMonitorConstants.GateStuckPrefix}:gate:"))
            {
                // Auto-approve a standard gate
                var parts = dedupKey[$"{FlowMonitorConstants.GateStuckPrefix}:gate:".Length..].Split(':');
                var gateId = parts.Length > 0 ? parts[0] : "";
                int? resourceNumber = parts.Length > 1 && int.TryParse(parts[1], out var rn) ? rn : null;

                if (string.IsNullOrEmpty(gateId))
                    return new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = "No gate ID" };

                _gateCheck.ApproveGate(gateId, resourceNumber);

                // Resolve the original gate notification now that we've auto-approved it,
                // so GateStuckDetector doesn't re-detect it on the next tick.
                _notificationService.Resolve(gateId, resourceNumber);

                // Build a descriptive resource label from the original notification
                var resourceLabel = BuildResourceLabel(gateId, resourceNumber);

                LogFlowMonitorDecision(
                    $"Auto-approved gate: {gateId} ({resourceLabel})",
                    finding.Rationale,
                    $"gate:{gateId}:{resourceNumber}");

                PostFlowMonitorNotification(
                    $"flow-monitor:auto-approve:gate:{gateId}:{resourceNumber}",
                    $"🤖 FlowMonitor Auto-Approved Gate",
                    $"Automatically approved the '{gateId}' gate ({resourceLabel}) because it was waiting too long.");

                _logger.LogInformation("FlowMonitor auto-approved gate {GateId} {ResourceLabel}", gateId, resourceLabel);
                return new FlowActionOutcome
                {
                    Result = FlowActionResult.Success,
                    Target = $"gate:{gateId}:{resourceNumber}",
                    Detail = $"Auto-approved gate '{gateId}' ({resourceLabel})"
                };
            }

            return new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = "Unknown dedup key format" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoApproveGateAction failed for finding {Id}", finding.Id);
            return new FlowActionOutcome { Result = FlowActionResult.Failed, Detail = ex.Message };
        }
    }

    private void LogFlowMonitorDecision(string title, string rationale, string resourceRef)
    {
        // FlowMonitor decisions are logged with AutoApproved status — never gated.
        // They appear on the Reasoning page but bypass the decision gating system.
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

    private void PostFlowMonitorNotification(string gateId, string gateName, string context)
    {
        _notificationService.AddFlowMonitorNotification(gateId, gateName, context);
    }

    /// <summary>
    /// Builds a human-readable resource label by looking up the original gate notification.
    /// Falls back to "resource #N" if the notification can't be found.
    /// </summary>
    private string BuildResourceLabel(string gateId, int? resourceNumber)
    {
        if (resourceNumber is null)
            return "automatic pipeline gate";

        try
        {
            // Find the original notification to get resource type and context
            var notifications = _notificationService.GetByStatus(NotificationFilter.All);
            var original = notifications.FirstOrDefault(n =>
                string.Equals(n.GateId, gateId, StringComparison.OrdinalIgnoreCase) &&
                n.ResourceNumber == resourceNumber);

            if (original is not null)
            {
                var type = original.ResourceType ?? "resource";
                // Extract a short title from context (first line, truncated)
                var contextLine = original.Context.Split('\n', 2)[0];
                if (contextLine.Length > 80) contextLine = contextLine[..77] + "...";
                return $"{type} #{resourceNumber}: {contextLine}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not look up notification details for gate {GateId} resource {Resource}", gateId, resourceNumber);
        }

        return $"resource #{resourceNumber}";
    }
}
