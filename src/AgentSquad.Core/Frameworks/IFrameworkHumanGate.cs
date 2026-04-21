namespace AgentSquad.Core.Frameworks;

/// <summary>
/// Optional human-gating interface for frameworks that support pause/resume
/// at configurable checkpoints. Enables AgentSquad's approval workflow to
/// intercept external framework decisions before they execute.
/// </summary>
public interface IFrameworkHumanGate
{
    /// <summary>
    /// Pause execution at a named checkpoint and wait for human approval.
    /// The framework must suspend its work until <see cref="ResumeAfterApprovalAsync"/> is called.
    /// </summary>
    Task PauseForApprovalAsync(string checkpoint, CancellationToken ct);

    /// <summary>
    /// Resume execution after human review. If <paramref name="approved"/> is false,
    /// the framework should abort the current operation gracefully.
    /// </summary>
    Task ResumeAfterApprovalAsync(string checkpoint, bool approved, string? feedback, CancellationToken ct);
}
