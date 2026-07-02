namespace VirtualDevTeam.Core.Configuration;

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
    /// Approve a gate locally (called from dashboard/REST API).
    /// When <paramref name="resourceNumber"/> is provided, the approval is scoped to that specific
    /// PR/Issue so approving PR #1 does NOT auto-approve PR #2 for multi-fire gates.
    /// </summary>
    void ApproveGate(string gateId, int? resourceNumber = null);

    /// <summary>
    /// Reject a gate locally with optional feedback (called from dashboard "Request Rework" button).
    /// The agent's polling loop picks this up and enters the rework cycle.
    /// </summary>
    void RejectGate(string gateId, string? feedback = null, int? resourceNumber = null);

    /// <summary>
    /// Get a pending local rejection for a gate, if any.
    /// Returns null if no rejection is pending.
    /// </summary>
    GateRejection? GetLocalRejection(string gateId, int? resourceNumber = null);

    /// <summary>
    /// Get the current rework-in-flight state for a gate/resource pair, if any.
    /// Returns null when no rework is currently in flight (either never rejected,
    /// or the agent already re-gated after rework, or the gate was fully approved).
    /// <para>
    /// The Approvals page calls this to render the rework spinner + iteration count
    /// + latest feedback quote from server truth instead of an in-memory Razor field.
    /// </para>
    /// </summary>
    ReworkInFlightState? GetReworkInFlight(string gateId, int? resourceNumber = null);

    /// <summary>
    /// Get ALL currently-in-flight rework states, keyed by the dashboard's notification
    /// resource identity (gate id + optional resource number). The bundled-mode dashboard
    /// uses this for a single batch lookup instead of per-card calls; the standalone-mode
    /// API endpoint uses it to project rework state onto each notification before serializing.
    /// </summary>
    IReadOnlyList<ReworkInFlightState> GetAllReworkInFlight();

    /// <summary>
    /// Check if a gate has been approved locally (via dashboard).
    /// Checks resource-scoped key first, then falls back to global key.
    /// </summary>
    bool IsGateApprovedLocally(string gateId, int? resourceNumber = null);

    /// <summary>
    /// Check if the master human interaction switch is enabled.
    /// When false, all gates auto-proceed.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Hot-reload gate configuration from updated settings.
    /// Called by the dashboard Configuration page after saving so that gate changes
    /// take effect immediately without restarting the runner.
    /// </summary>
    void UpdateGates(HumanInteractionConfig updatedGates);

    /// <summary>
    /// Quick check if a specific gate requires human approval (no side effects).
    /// </summary>
    bool RequiresHuman(string gateId);

    /// <summary>
    /// Returns <c>true</c> when every gate is unconditionally disabled — i.e.
    /// the wizard's master switch in <c>develop-settings.json</c> says
    /// <c>gatePreferences.enabled = false</c> OR the appsettings.json
    /// <c>HumanInteraction.Enabled</c> is <c>false</c>. When this is <c>true</c>
    /// the workflow runs fully autonomously and per-gate flags are ignored.
    /// </summary>
    bool AreAllGatesDisabled();

    /// <summary>
    /// Check the current state of a gate on a PR — whether it's already pending,
    /// already approved, or hasn't been activated yet. Used by agents on restart
    /// to skip re-doing work when a gate is already waiting for human approval.
    /// </summary>
    Task<GateStatus> GetGateStatusAsync(string gateId, int resourceNumber, CancellationToken ct = default);

    /// <summary>
    /// Wait for a gate signal (from dashboard approve/reject) or timeout.
    /// Used by WaitForGateAsync to respond instantly to dashboard actions
    /// instead of blind 30-second polling.
    /// </summary>
    Task WaitForSignalAsync(int timeoutSeconds, CancellationToken ct = default);
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

/// <summary>
/// A pending gate rejection from the dashboard, including optional rework feedback
/// and a cumulative iteration counter so the Approvals page can show
/// "Rework iteration N" without losing state across navigation.
/// </summary>
public record GateRejection(
    string GateId,
    string? Feedback,
    int? ResourceNumber,
    DateTime RejectedAt,
    int IterationCount = 1);

/// <summary>
/// Structured rework-in-flight record surfaced to the dashboard so the
/// "Rework in progress" card state is rendered from server truth on every
/// page load (not from a per-Razor-component in-memory flag).
/// <para>
/// Lifecycle: created when the operator clicks "Request Rework", cleared
/// when the agent re-gates after rework (re-calls CheckGateAsync) or when
/// the gate is fully approved. IterationCount is cumulative across multiple
/// rework cycles on the same (GateId, ResourceNumber) — it only resets
/// when the gate is fully approved.
/// </para>
/// </summary>
public record ReworkInFlightState(
    string GateId,
    int? ResourceNumber,
    DateTime RequestedAt,
    string? Feedback,
    int IterationCount,
    string? LatestCommitSha = null,
    string? ChangesUrl = null);

/// <summary>Extension methods for <see cref="IGateCheckService"/>.</summary>
public static class GateCheckExtensions
{
    /// <summary>
    /// Check a gate and, if human approval is required, poll until approved or rejected.
    /// Uses signal-based waiting for instant response to dashboard actions, with
    /// periodic fallback polling for GitHub comment/label approvals.
    /// </summary>
    public static async Task<GateWaitResult> WaitForGateAsync(
        this IGateCheckService gateCheck,
        string gateId,
        string context,
        int? resourceNumber = null,
        int pollIntervalSeconds = 30,
        CancellationToken ct = default)
    {
        // Master-switch short-circuit: if the wizard or appsettings has disabled all
        // gates, never enter the wait flow. This is defense-in-depth on top of
        // CheckGateAsync's own short-circuit so that callers (or future code paths)
        // that bypass CheckGateAsync still honor the master switch.
        if (gateCheck.AreAllGatesDisabled())
            return new GateWaitResult(WasActivated: false, Decision: GateDecision.Approved);

        var result = await gateCheck.CheckGateAsync(gateId, context, resourceNumber, ct);
        if (result == GateResult.Proceed)
            return new GateWaitResult(WasActivated: false, Decision: GateDecision.Approved);

        // Gate requires human — wait for signal (instant) or timeout (fallback poll)
        if (resourceNumber.HasValue)
        {
            while (!ct.IsCancellationRequested)
            {
                // WaitForSignalAsync returns immediately on dashboard approve/reject,
                // or after pollIntervalSeconds as fallback for GitHub comment approvals
                await gateCheck.WaitForSignalAsync(pollIntervalSeconds, ct);
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
                await gateCheck.WaitForSignalAsync(pollIntervalSeconds, ct);

                if (gateCheck.IsGateApprovedLocally(gateId))
                    return new GateWaitResult(WasActivated: true, Decision: GateDecision.Approved);

                var rejection = gateCheck.GetLocalRejection(gateId);
                if (rejection is not null)
                    return new GateWaitResult(WasActivated: true, Decision: GateDecision.Rejected, Feedback: rejection.Feedback);
            }
        }

        ct.ThrowIfCancellationRequested();
        return new GateWaitResult(WasActivated: true, Decision: GateDecision.Approved);
    }
}
