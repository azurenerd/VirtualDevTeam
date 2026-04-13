namespace AgentSquad.Core.Configuration;

/// <summary>
/// Service that evaluates human interaction gates at workflow touchpoints.
/// When a gate requires human approval, the service signals the need (via GitHub labels/comments)
/// and returns a result indicating the workflow should wait.
/// </summary>
public interface IGateCheckService
{
    /// <summary>
    /// Check whether a gate requires human approval and act accordingly.
    /// If the gate doesn't require human approval, returns Proceed immediately.
    /// If human approval is required, posts a notification and returns WaitingForHuman.
    /// </summary>
    /// <param name="gateId">The gate identifier (use <see cref="GateIds"/> constants).</param>
    /// <param name="context">Human-readable description of what's being gated (e.g., "PMSpec.md review").</param>
    /// <param name="resourceNumber">Optional PR or Issue number to label/comment on.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GateResult> CheckGateAsync(string gateId, string context, int? resourceNumber = null, CancellationToken ct = default);

    /// <summary>
    /// Check if a gate has been approved by a human (looks for approval label/comment on the resource).
    /// </summary>
    Task<bool> IsGateApprovedAsync(string gateId, int resourceNumber, CancellationToken ct = default);

    /// <summary>
    /// Approve a gate locally (called from dashboard/REST API when no GitHub resource is involved).
    /// </summary>
    void ApproveGate(string gateId);

    /// <summary>
    /// Check if a gate has been approved locally (via dashboard).
    /// </summary>
    bool IsGateApprovedLocally(string gateId);

    /// <summary>
    /// Check if the master human interaction switch is enabled.
    /// When false, all gates auto-proceed.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Quick check if a specific gate requires human approval (no side effects).
    /// </summary>
    bool RequiresHuman(string gateId);

    /// <summary>
    /// Check the current state of a gate on a PR — whether it's already pending,
    /// already approved, or hasn't been activated yet. Used by agents on restart
    /// to skip re-doing work when a gate is already waiting for human approval.
    /// </summary>
    Task<GateStatus> GetGateStatusAsync(string gateId, int resourceNumber, CancellationToken ct = default);
}

/// <summary>Status of a gate on a specific resource.</summary>
public enum GateStatus
{
    /// <summary>Gate hasn't been activated on this resource yet.</summary>
    NotActivated,

    /// <summary>Gate is active and waiting for human approval (has awaiting-human-review label).</summary>
    AwaitingApproval,

    /// <summary>Gate was already approved (has human-approved label, approval comment, or PR is merged).</summary>
    Approved,
}

/// <summary>Result of a gate check.</summary>
public enum GateResult
{
    /// <summary>Gate does not require human approval or is already approved — proceed.</summary>
    Proceed,

    /// <summary>Gate requires human approval — workflow should pause until approved.</summary>
    WaitingForHuman,

    /// <summary>Gate timed out and fallback action was applied.</summary>
    TimedOutWithFallback,
}

/// <summary>Extension methods for <see cref="IGateCheckService"/>.</summary>
public static class GateCheckExtensions
{
    /// <summary>
    /// Check a gate and, if human approval is required, poll until approved.
    /// This is the primary method agents should call at gate points.
    /// </summary>
    /// <param name="gateCheck">The gate check service.</param>
    /// <param name="gateId">Gate identifier from <see cref="GateIds"/>.</param>
    /// <param name="context">Human-readable description of what's gated.</param>
    /// <param name="resourceNumber">PR or Issue number for label/comment notifications.</param>
    /// <param name="pollIntervalSeconds">Seconds between approval polls (default 30).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if gate was activated (human was involved), false if auto-proceeded.</returns>
    public static async Task<bool> WaitForGateAsync(
        this IGateCheckService gateCheck,
        string gateId,
        string context,
        int? resourceNumber = null,
        int pollIntervalSeconds = 30,
        CancellationToken ct = default)
    {
        var result = await gateCheck.CheckGateAsync(gateId, context, resourceNumber, ct);
        if (result == GateResult.Proceed)
            return false;

        // Gate requires human — poll for approval
        if (resourceNumber.HasValue)
        {
            // Poll GitHub for label/comment approval
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
                if (await gateCheck.IsGateApprovedAsync(gateId, resourceNumber.Value, ct))
                    return true;
            }
        }
        else
        {
            // No GitHub resource — poll local approval (dashboard/REST API)
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
                if (gateCheck.IsGateApprovedLocally(gateId))
                    return true;
            }
        }

        ct.ThrowIfCancellationRequested();
        return true;
    }
}
