namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// A FlowMonitor action is a vetted, safe corrective step that can be taken
/// in response to a finding. Actions are intentionally narrow: they do NOT
/// restart processes, recompile code, force-merge PRs, or modify code. Their
/// job is to surface stuck-ness (post a comment, kick a poll, refresh a gate)
/// so the existing pipeline can pick it up.
/// </summary>
public interface IFlowAction
{
    /// <summary>Stable id for logging, config gating, and audit trails.</summary>
    string ActionType { get; }

    /// <summary>True when this action knows how to remediate the finding.</summary>
    bool CanHandle(FlowFinding finding);

    /// <summary>
    /// Execute the action. Must be idempotent — running twice for the same finding
    /// must not have additional side effects. Must complete quickly (≤10s) and
    /// never throw.
    /// </summary>
    Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct);

    /// <summary>
    /// Reverse a side-effect that this action previously applied to the platform
    /// (e.g., remove a label, delete a status comment). Called by the FlowMonitor's
    /// verification-after-action loop (T1.3) when the originating finding's condition
    /// is confirmed cleared and the finding is being marked Resolved.
    ///
    /// Default implementation is a no-op — most actions don't have side effects worth
    /// undoing (bus-message nudges and informational comments are part of the audit trail
    /// and should remain). Only platform-state mutations (labels, sticky markers) should
    /// override this.
    ///
    /// Must be idempotent and never throw — failures are logged and swallowed by the caller.
    /// </summary>
    Task UndoAsync(FlowFinding finding, FlowAction priorAction, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Result of a single action attempt — feeds into the persisted FlowAction record.</summary>
public sealed record FlowActionOutcome
{
    public required FlowActionResult Result { get; init; }
    public string? Target { get; init; }
    public string? Detail { get; init; }
}
