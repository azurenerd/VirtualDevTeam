using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects when the workflow phase is "Completion" but one or more agents still report
/// Working status with in-progress reasons. The phase shouldn't have advanced to
/// Completion while live work is happening, so this typically means either:
///   1. An agent missed a state-clear when its task finished (UpdateStatus(Idle) was skipped)
///   2. An agent is doing post-completion polling work that should have already gone Idle
///
/// Either way it's worth surfacing so the operator can see + the FlowMonitor can post
/// a "please confirm Idle" nudge.
/// </summary>
public sealed class PhaseCompletionMismatchDetector : IFlowDetector
{
    public string DetectorId => "phase-completion-mismatch";

    private readonly ILogger<PhaseCompletionMismatchDetector> _logger;

    public PhaseCompletionMismatchDetector(ILogger<PhaseCompletionMismatchDetector> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            if (!string.Equals(ctx.CurrentPhase, "Completion", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

            var workingAgents = ctx.Agents
                .Where(a => string.Equals(a.Status, "Working", StringComparison.OrdinalIgnoreCase))
                // Exclude agents actively running strategy framework candidates — this is
                // legitimate T-FINAL work, not a stale status. The strategy orchestrator
                // will transition to Idle when candidates finish.
                .Where(a => !IsActiveStrategyWork(a.StatusReason))
                .ToList();

            if (workingAgents.Count == 0)
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = ctx.Now,
                DetectorId = DetectorId,
                Severity = FlowFindingSeverity.Warning,
                TargetAgentId = workingAgents[0].Id,
                TargetResource = "workflow-phase",
                TargetDisplayName = workingAgents[0].DisplayName,
                Summary = $"Phase is Completion but {workingAgents.Count} agent(s) still Working: " +
                          string.Join(", ", workingAgents.Select(a => a.DisplayName)),
                Rationale = "When the workflow reaches Completion, all engineering signals have fired and " +
                            "merged-PR detection has succeeded. Agents should be Idle. If any are still Working, " +
                            "either the status-clear was missed, or an agent is mid-call on a recovery loop. " +
                            "Recommend a status-clear nudge or operator review.",
                DedupKey = "phase-completion-mismatch",
            });
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PhaseCompletionMismatchDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    /// <summary>
    /// Returns true when the agent's status reason indicates active strategy framework
    /// execution (candidates running, evaluation in progress, creating integration PR).
    /// These are legitimate Working states during T-FINAL, not stale statuses.
    /// </summary>
    private static bool IsActiveStrategyWork(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        return reason.Contains("Strategy candidates", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Creating integration", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Evaluating gates", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Final Integration", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Resolving merge conflict", StringComparison.OrdinalIgnoreCase);
    }
}
