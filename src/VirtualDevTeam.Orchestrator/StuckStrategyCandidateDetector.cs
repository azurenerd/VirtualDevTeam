using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Detects strategy candidates that are running but have produced no output for
/// an extended period. Uses <see cref="CandidateStateStore.GetStuckCandidates"/>
/// which checks both <c>ProcessStartedAt</c> and <c>LastActivityAt</c> to
/// distinguish truly stuck processes from slow-but-progressing ones.
///
/// Unlike the process-level <see cref="VirtualDevTeam.Core.AI.AgenticOutputMonitor"/>
/// (which kills the process directly via CTS when stdout goes silent), this detector
/// provides FlowMonitor visibility and can trigger the
/// <see cref="CancelStrategyCandidateAction"/> for cases where the process-level
/// monitor failed or was disabled.
/// </summary>
public sealed class StuckStrategyCandidateDetector : IFlowDetector
{
    public string DetectorId => "stuck-strategy-candidate";

    private readonly CandidateStateStore _store;
    private readonly TimeSpan _threshold;
    private readonly ILogger<StuckStrategyCandidateDetector> _logger;

    public StuckStrategyCandidateDetector(
        CandidateStateStore store,
        ILogger<StuckStrategyCandidateDetector> logger,
        TimeSpan? threshold = null)
    {
        _store = store;
        _logger = logger;
        _threshold = threshold ?? TimeSpan.FromMinutes(10);
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var stuck = _store.GetStuckCandidates(_threshold);
            foreach (var (runId, taskId, strategyId, processId, elapsed) in stuck)
            {
                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Critical,
                    TargetAgentId = null,
                    TargetResource = $"{taskId}/{strategyId}",
                    Summary = $"Strategy candidate '{strategyId}' for task {taskId} has produced no output " +
                              $"for {elapsed.TotalMinutes:F0} minutes (PID {processId})",
                    Rationale =
                        $"The '{strategyId}' candidate process (PID {processId}) has been running for " +
                        $"{elapsed.TotalMinutes:F0} minutes with no stdout activity. This typically indicates " +
                        "the CLI session hung during MCP server initialization, encountered an unhandled " +
                        "interactive prompt, or hit a pipe deadlock. The pipeline is blocked because the " +
                        "strategy orchestrator waits for all candidates to complete before selecting a winner. " +
                        "Cancelling this candidate will allow the orchestrator to proceed with completed " +
                        "candidates (e.g., Squad).",
                    DedupKey = $"stuck-strategy:{runId}:{taskId}:{strategyId}",
                });

                _logger.LogWarning(
                    "Stuck strategy candidate detected: {Strategy} for task {Task} (PID {Pid}, {Elapsed:F0}min no output)",
                    strategyId, taskId, processId, elapsed.TotalMinutes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StuckStrategyCandidateDetector tick failed (non-fatal)");
        }

        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }
}
