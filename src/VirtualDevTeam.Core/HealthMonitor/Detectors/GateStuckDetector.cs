using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Core.Notifications;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects gates and decisions that have been pending longer than the configured
/// <see cref="FlowMonitorConfig.AutoApprovalMinutes"/> threshold. Emits findings
/// that pair with <see cref="Actions.AutoApproveGateAction"/>.
///
/// Disabled when <c>AutoApprovalMinutes == 0</c>.
/// </summary>
public sealed class GateStuckDetector : IFlowDetector
{
    private readonly IDecisionLog _decisionLog;
    private readonly GateNotificationService _notificationService;
    private readonly int _thresholdMinutes;
    private readonly ILogger<GateStuckDetector> _logger;

    public string DetectorId => "gate-stuck";

    public GateStuckDetector(
        IDecisionLog decisionLog,
        GateNotificationService notificationService,
        int thresholdMinutes,
        ILogger<GateStuckDetector> logger)
    {
        _decisionLog = decisionLog;
        _notificationService = notificationService;
        _thresholdMinutes = thresholdMinutes;
        _logger = logger;
        _logger.LogInformation("GateStuckDetector initialized with threshold={Threshold}m (0=disabled)", thresholdMinutes);
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();

        if (_thresholdMinutes <= 0)
            return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

        var cutoff = ctx.Now.AddMinutes(-_thresholdMinutes);

        try
        {
            // Check pending decision gates
            var pendingDecisions = _decisionLog.GetPendingDecisions();
            foreach (var decision in pendingDecisions)
            {
                if (decision.CreatedAt < cutoff.UtcDateTime)
                {
                    var pendingMinutes = (int)(ctx.Now - decision.CreatedAt).TotalMinutes;
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Warning,
                        TargetAgentId = decision.AgentId,
                        TargetDisplayName = decision.AgentDisplayName,
                        TargetResource = $"decision:{decision.Id}",
                        Summary = $"Decision gate '{decision.Title}' pending for {pendingMinutes}m (threshold: {_thresholdMinutes}m)",
                        Rationale = $"Decision by {decision.AgentDisplayName} has been waiting for approval since {decision.CreatedAt:HH:mm:ss}. " +
                                    $"Auto-approval threshold is {_thresholdMinutes}m.",
                        DedupKey = $"{FlowMonitorConstants.GateStuckPrefix}:decision:{decision.Id}",
                    });
                }
            }

            // Check pending standard gate notifications
            var openNotifications = _notificationService.GetByStatus(NotificationFilter.Open);
            foreach (var notification in openNotifications)
            {
                // Skip FlowMonitor's own notifications and decision gates (handled above)
                if (notification.IsFlowMonitorAction) continue;
                if (notification.GateId.StartsWith(DecisionGateService.DecisionGatePrefix)) continue;

                if (notification.CreatedAt < cutoff.UtcDateTime)
                {
                    var pendingMinutes = (int)(ctx.Now - notification.CreatedAt).TotalMinutes;
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Warning,
                        TargetResource = $"gate:{notification.GateId}:{notification.ResourceNumber}",
                        Summary = $"Gate '{notification.GateName}' pending for {pendingMinutes}m (threshold: {_thresholdMinutes}m)",
                        Rationale = $"Gate notification for {notification.GateName} ({notification.ResourceType ?? "resource"} #{notification.ResourceNumber}) " +
                                    $"has been waiting since {notification.CreatedAt:HH:mm:ss}. Auto-approval threshold is {_thresholdMinutes}m.",
                        DedupKey = $"{FlowMonitorConstants.GateStuckPrefix}:gate:{notification.GateId}:{notification.ResourceNumber}",
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GateStuckDetector failed (non-fatal)");
        }

        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }
}
