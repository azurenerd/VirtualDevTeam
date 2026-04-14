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
    Task<GateResult> CheckGateAsync(string gateId, string context, int? resourceNumber = null, CancellationToken ct = default);

    /// <summary>
    /// Check if a gate has been approved by a human (looks for approval label/comment on the resource).
    /// Uses AI to assess comment intent — handles "not approved", rejection with feedback, etc.
    /// </summary>
    Task<GateCommentAssessment> AssessGateApprovalAsync(string gateId, int resourceNumber, CancellationToken ct = default);

    /// <summary>
    /// Legacy bool check — wraps AssessGateApprovalAsync. Returns true only for clear approval.
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

/// <summary>AI-assessed decision on human gate comments.</summary>
public enum GateDecision
{
    /// <summary>Human approved — proceed with merge.</summary>
    Approved,

    /// <summary>Human rejected or requested changes — agent must revise.</summary>
    Rejected,

    /// <summary>No actionable human comment found yet — keep waiting.</summary>
    Pending,
}

/// <summary>
/// Result of AI assessment of human comments on a gated PR/issue.
/// When rejected, contains the human's feedback for the agent to act on.
/// </summary>
public record GateCommentAssessment(
    GateDecision Decision,
    string? Feedback = null);

/// <summary>
/// Result of waiting for a gate — includes rejection feedback if the human requested changes.
/// </summary>
public record GateWaitResult(
    bool WasActivated,
    GateDecision Decision,
    string? Feedback = null)
{
    /// <summary>True if the human rejected/requested changes and feedback is available.</summary>
    public bool WasRejected => Decision == GateDecision.Rejected;
}

/// <summary>Extension methods for <see cref="IGateCheckService"/>.</summary>
public static class GateCheckExtensions
{
    /// <summary>
    /// Check a gate and, if human approval is required, poll until approved or rejected.
    /// Returns a <see cref="GateWaitResult"/> with the decision and any rejection feedback.
    /// </summary>
    public static async Task<GateWaitResult> WaitForGateAsync(
        this IGateCheckService gateCheck,
        string gateId,
        string context,
        int? resourceNumber = null,
        int pollIntervalSeconds = 30,
        CancellationToken ct = default)
    {
        var result = await gateCheck.CheckGateAsync(gateId, context, resourceNumber, ct);
        if (result == GateResult.Proceed)
            return new GateWaitResult(WasActivated: false, Decision: GateDecision.Approved);

        // Gate requires human — poll for approval or rejection
        if (resourceNumber.HasValue)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
                var assessment = await gateCheck.AssessGateApprovalAsync(gateId, resourceNumber.Value, ct);

                if (assessment.Decision == GateDecision.Approved)
                    return new GateWaitResult(WasActivated: true, Decision: GateDecision.Approved);

                if (assessment.Decision == GateDecision.Rejected)
                    return new GateWaitResult(WasActivated: true, Decision: GateDecision.Rejected, Feedback: assessment.Feedback);
            }
        }
        else
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
                if (gateCheck.IsGateApprovedLocally(gateId))
                    return new GateWaitResult(WasActivated: true, Decision: GateDecision.Approved);
            }
        }

        ct.ThrowIfCancellationRequested();
        return new GateWaitResult(WasActivated: true, Decision: GateDecision.Approved);
    }
}
