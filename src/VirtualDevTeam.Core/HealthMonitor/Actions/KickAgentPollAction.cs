using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Messaging;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// "Kicks" a stuck agent by publishing a low-priority status-update message it can use to
/// re-evaluate its loop. The idea: agents that block on the bus or wait for a poll-tick can
/// notice an inbound message and self-recover. If the agent is genuinely wedged (deadlocked
/// in an AI call), the kick is a no-op — but at least the audit trail records that we tried.
///
/// This action does NOT modify code, restart processes, or force-merge anything. It's the
/// gentlest possible nudge.
/// </summary>
public sealed class KickAgentPollAction : IFlowAction
{
    public string ActionType => "kick-agent-poll";

    private readonly IMessageBus _messageBus;
    private readonly ILogger<KickAgentPollAction> _logger;

    public KickAgentPollAction(IMessageBus messageBus, ILogger<KickAgentPollAction> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    /// <summary>
    /// NoMessyCodePlan post-Tier-2: expanded to handle all agent-targeted detector types so the new
    /// Tier-2 findings actually trigger the escalation ladder (was previously locked to agent-stuck +
    /// phase-completion-mismatch only, leaving idle-agent-phase-stuck / te-false-completion /
    /// handoff-gap / empty-queue / idle-idle-cycle findings unactioned).
    ///
    /// Carve-outs:
    /// - `status-reason-stagnant`: kicking a busy agent doesn't help — skip.
    /// - `idle-idle-cycle`: already cycling, another nudge would compound — skip.
    /// - `ai-anomaly`: Warning-only meta-detector, no specific agent to nudge — skip.
    /// - `deadlock`: separate human-escalation channel — skip per T2.13 design.
    /// </summary>
    private static readonly HashSet<string> _kickableDetectorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent-stuck",
        "phase-completion-mismatch",
        "idle-agent-phase-stuck",
        "te-false-completion",
        "handoff-gap",
        "empty-queue",
        "pr-approval-stuck",
        "external-merge-desync",
    };

    public bool CanHandle(FlowFinding finding) =>
        _kickableDetectorIds.Contains(finding.DetectorId)
        && !string.IsNullOrEmpty(finding.TargetAgentId);

    public async Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(finding.TargetAgentId))
        {
            return new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = "no target agent id" };
        }
        try
        {
            await _messageBus.PublishAsync(new FlowMonitorNudgeMessage
            {
                FromAgentId = "flow-monitor",
                ToAgentId = finding.TargetAgentId,
                MessageType = "FlowMonitorNudge",
                Reason = $"FlowMonitor noticed: {finding.Summary}. Re-check loop state.",
            }, ct);
            _logger.LogInformation("FlowMonitor kick sent to {Agent} for finding {FindingId}",
                finding.TargetAgentId, finding.Id);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Success,
                Target = finding.TargetAgentId,
                Detail = "Published FlowMonitorNudge bus message to target agent",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KickAgentPollAction failed for {Agent}", finding.TargetAgentId);
            return new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = finding.TargetAgentId,
                Detail = $"Exception: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }
}
