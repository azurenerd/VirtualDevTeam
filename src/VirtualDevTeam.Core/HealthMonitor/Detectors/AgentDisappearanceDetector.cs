using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects when agents unexpectedly disappear during an active run.
/// Fires when the registered agent count drops to 0 (or below a minimum threshold)
/// while the workflow is in an active phase (not Initialization or Completion).
///
/// Common causes: dashboard ResetCaches bug clearing the snapshot registry,
/// runner crash leaving the process alive but agents dead, or a code path
/// that unregisters agents without stopping the run.
/// </summary>
public sealed class AgentDisappearanceDetector : IFlowDetector
{
    public string DetectorId => "agent-disappearance";

    private readonly ILogger<AgentDisappearanceDetector> _logger;

    // Minimum expected agents during active phases (PM + SE + TE at minimum)
    private const int MinExpectedAgents = 3;

    public AgentDisappearanceDetector(ILogger<AgentDisappearanceDetector> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            // Only check during active phases — agents aren't expected during Initialization
            // and may legitimately be gone after Completion.
            var phase = ctx.CurrentPhase ?? "";
            if (string.Equals(phase, "Initialization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(phase, "Completion", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(phase))
            {
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
            }

            var agentCount = ctx.Agents.Count;

            if (agentCount == 0)
            {
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Critical,
                    Summary = $"All agents have disappeared during {phase} phase. " +
                              $"The run is active but 0 agents are registered.",
                    Rationale = "No agents are visible to the dashboard or orchestrator during an active " +
                                "workflow phase. This typically indicates a dashboard ResetCaches bug cleared " +
                                "the agent snapshot registry, or the agents crashed without stopping the run. " +
                                "A runner restart is likely needed to recover.",
                    DedupKey = "agent-disappearance:all",
                });
            }
            else if (agentCount < MinExpectedAgents)
            {
                // Some agents missing but not all — less severe
                var presentRoles = string.Join(", ", ctx.Agents
                    .Select(a => a.DisplayName ?? a.Role ?? a.Id)
                    .Take(5));

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Warning,
                    Summary = $"Only {agentCount} agent(s) registered during {phase} phase " +
                              $"(expected ≥{MinExpectedAgents}). Present: {presentRoles}.",
                    Rationale = "Fewer agents than expected are registered during an active phase. " +
                                "Some agents may have crashed or been unregistered. Check the runner logs " +
                                "for agent initialization failures.",
                    DedupKey = $"agent-disappearance:low-count:{agentCount}",
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentDisappearanceDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }
}
