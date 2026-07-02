namespace VirtualDevTeam.Core.MissingWork;

/// <summary>
/// Generates a <see cref="ProposedIssue"/> from a <see cref="MissingWorkFinding"/> by
/// invoking the Copilot CLI in JSON-output mode. The resulting proposal is persisted to
/// the <c>proposed_issues</c> table and surfaced on the Approvals page (Phase 1.9).
/// </summary>
public interface IMissingWorkPlanner
{
    /// <summary>
    /// Builds and persists a proposed issue for <paramref name="finding"/>.
    /// Returns <c>null</c> if the finding confidence is below the configured threshold,
    /// the CLI call fails, or JSON parsing fails.
    /// Never throws — all exceptions are caught and logged internally.
    /// </summary>
    Task<ProposedIssue?> PlanProposalAsync(MissingWorkFinding finding, CancellationToken ct);
}
