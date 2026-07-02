using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Notifications;

/// <summary>
/// An artifact linked to a gate notification for inline preview on the Approvals page.
/// Agents populate these when raising gates so the UI can show document content and platform links.
/// </summary>
public record GateArtifact(
    /// <summary>Relative file path in the repository (e.g., "AgentDocs/PMSpec.md").</summary>
    string FilePath,
    /// <summary>Commit SHA for building platform URLs. Null = use default branch.</summary>
    string? CommitSha,
    /// <summary>Kind of artifact for rendering decisions.</summary>
    ArtifactKind Kind,
    /// <summary>Optional display name (defaults to filename if null).</summary>
    string? DisplayName = null);

public enum ArtifactKind { Document, Image, Diagram, Data }

/// <summary>
/// Represents a notification about a human gate requiring attention.
/// </summary>
public record GateNotification
{
    public required string Id { get; init; }
    public required string GateId { get; init; }
    public required string GateName { get; init; }
    public required string Context { get; init; }
    public int? ResourceNumber { get; init; }
    public string? ResourceType { get; init; } // "PR" or "Issue"
    public string? GitHubUrl { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
    public bool IsResolved { get; set; }
    public DateTime? ResolvedAt { get; set; }

    /// <summary>True when this notification is a re-submission after rework (agent revised and re-gated).</summary>
    public bool IsReworked { get; set; }

    /// <summary>
    /// Server-truth rework-in-flight state for this gate+resource, populated by
    /// <see cref="GateNotificationService.GetByStatus"/> from <see cref="IGateCheckService.GetReworkInFlight"/>.
    /// Non-null while the operator has requested rework and the agent has not yet re-gated.
    /// The Approvals page renders the rework spinner + iteration count + feedback quote
    /// from this field so the state survives navigating away and back to /approvals.
    /// </summary>
    public ReworkInFlightState? ReworkState { get; set; }

    /// <summary>
    /// Per-scenario playtest verdicts from T-FINAL, attached to integration-review gates.
    /// Null (or empty) for gates that have no scenario-verification context — existing
    /// approvals without this data continue to render correctly (backwards-compatible).
    /// </summary>
    public IReadOnlyList<ScenarioVerdict>? ScenarioVerdicts { get; init; }

    /// <summary>
    /// Artifacts linked to this gate for inline preview. Agents populate these when raising gates.
    /// Persisted as JSON in gate_notifications.artifacts_json.
    /// </summary>
    public IReadOnlyList<GateArtifact>? Artifacts { get; init; }

    /// <summary>
    /// When true, this notification was created by FlowMonitor auto-approval.
    /// Shown on the Approvals page with a Dismiss button (not Approve/Reject)
    /// for operator awareness. The gate/decision is already resolved.
    /// </summary>
    public bool IsFlowMonitorAction { get; init; }
}
