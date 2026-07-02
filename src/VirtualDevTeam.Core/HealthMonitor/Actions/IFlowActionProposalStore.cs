namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Persistence contract for operator-gated FlowMonitor action proposals.
/// Implementation backed by SQLite (flow_action_proposals table) — see plan task 1.4.
/// </summary>
public interface IFlowActionProposalStore
{
    /// <summary>Insert a new pending proposal. Returns the persisted ID.</summary>
    Task<string> InsertAsync(ProposedFlowAction proposal, CancellationToken ct);

    /// <summary>List all proposals currently in Pending state, ordered by CreatedAt ASC.</summary>
    Task<IReadOnlyList<ProposedFlowAction>> ListPendingAsync(CancellationToken ct);

    /// <summary>Get a single proposal by ID, or null if not found.</summary>
    Task<ProposedFlowAction?> GetAsync(string id, CancellationToken ct);

    /// <summary>Update the state + operator-action fields. Returns true if the update applied.</summary>
    Task<bool> UpdateStateAsync(
        string id,
        ProposedFlowActionState newState,
        string? operatorRationale,
        string? executionResult,
        CancellationToken ct);

    /// <summary>Mark expired proposals (older than ExpiresAt). Returns count marked.</summary>
    Task<int> MarkExpiredAsync(CancellationToken ct);
}
