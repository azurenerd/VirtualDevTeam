using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Actions;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// FlowMonitor action that cancels a stuck strategy candidate (rung 3 — nuclear option).
/// Only fires when the candidate has already been reset 2+ times without recovery.
/// For rung 1-2, <see cref="ResetStrategyCandidateAction"/> handles reset+retry.
/// </summary>
public sealed class CancelStrategyCandidateAction : IFlowAction
{
    public string ActionType => "cancel-stuck-strategy";

    private readonly IOrchestrationCancellationService _cancellation;
    private readonly CandidateStateStore _store;
    private readonly ILogger<CancelStrategyCandidateAction> _logger;

    /// <summary>Minimum reset count before cancellation is allowed. Below this, reset action handles it.</summary>
    private const int MinResetCountForCancel = 2;

    public CancelStrategyCandidateAction(
        IOrchestrationCancellationService cancellation,
        CandidateStateStore store,
        ILogger<CancelStrategyCandidateAction> logger)
    {
        _cancellation = cancellation;
        _store = store;
        _logger = logger;
    }

    public bool CanHandle(FlowFinding finding)
    {
        if (!finding.DedupKey.StartsWith("stuck-strategy:", StringComparison.Ordinal))
            return false;

        // Only handle at rung 3 (ResetCount >= 2)
        var parts = finding.DedupKey.Split(':');
        if (parts.Length < 4) return false;

        var taskId = parts[2];
        var strategyId = parts[3];
        var resetCount = _store.GetResetCount(taskId, strategyId);
        return resetCount >= MinResetCountForCancel;
    }

    public Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        var parts = finding.DedupKey.Split(':');
        if (parts.Length < 4)
            return Task.FromResult(new FlowActionOutcome { Result = FlowActionResult.Skipped, Detail = "Bad dedup key" });

        var runId = parts[1];
        var taskId = parts[2];
        var strategyId = parts[3];

        _logger.LogWarning(
            "Cancelling stuck strategy candidate (rung 3 — nuclear): {Strategy} for task {Task} after {Resets} resets",
            strategyId, taskId, _store.GetResetCount(taskId, strategyId));

        try
        {
            var cancelled = _cancellation.RequestCandidateCancellation(runId, taskId, strategyId);
            if (!cancelled)
                cancelled = _cancellation.RequestCancellation(runId, taskId);

            return Task.FromResult(new FlowActionOutcome
            {
                Result = cancelled ? FlowActionResult.Success : FlowActionResult.Skipped,
                Target = $"{taskId}/{strategyId}",
                Detail = cancelled
                    ? $"Cancelled stuck candidate '{strategyId}' (rung 3). Orchestrator proceeds with remaining candidates."
                    : "No cancellation registration found — candidate may have already completed.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel strategy candidate {Strategy}", strategyId);
            return Task.FromResult(new FlowActionOutcome
            {
                Result = FlowActionResult.Failed,
                Target = $"{taskId}/{strategyId}",
                Detail = ex.Message,
            });
        }
    }
}
