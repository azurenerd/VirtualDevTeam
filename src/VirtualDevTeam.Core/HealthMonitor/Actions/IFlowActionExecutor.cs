namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Executes an operator-approved ProposedFlowAction. Dispatches by Type to the appropriate
/// concrete handler (e.g. AddPrLabel → calls IPullRequestService.AddLabelAsync).
/// </summary>
public interface IFlowActionExecutor
{
    /// <summary>
    /// Run the action. Throws on failure — caller is responsible for catching and
    /// updating the proposal state via <see cref="IFlowActionProposalStore.UpdateStateAsync"/>.
    /// Returns a human-readable summary of what happened (saved to ExecutionResult).
    /// </summary>
    Task<string> ExecuteAsync(ProposedFlowAction proposal, CancellationToken ct);
}
