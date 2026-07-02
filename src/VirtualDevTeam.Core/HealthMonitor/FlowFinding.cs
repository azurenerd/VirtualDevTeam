namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Severity of a flow finding. Used to gate which findings emit notifications and
/// which are silently logged for trend analysis.
/// </summary>
public enum FlowFindingSeverity
{
    /// <summary>Trivia: detector observed a normal-but-noteworthy event.</summary>
    Info,
    /// <summary>Soft anomaly: investigate but no action needed yet.</summary>
    Warning,
    /// <summary>Hard stuck-state: action recommended.</summary>
    Critical
}

/// <summary>
/// State of a finding's lifecycle. Allows the dashboard to distinguish between
/// findings still being investigated, those acted on, and those that auto-resolved.
/// </summary>
public enum FlowFindingState
{
    /// <summary>Detected, not yet acted on or acknowledged.</summary>
    Open,
    /// <summary>An action has been initiated for this finding.</summary>
    ActedOn,
    /// <summary>The condition cleared on its own before action ran.</summary>
    Resolved,
    /// <summary>Too old / superseded by a newer finding for the same target.</summary>
    Expired
}

/// <summary>
/// A single observation by the FlowMonitor that something looks stuck or off-track.
/// Findings are persisted to SQLite and surfaced on the Health Monitor dashboard.
/// </summary>
public sealed record FlowFinding
{
    public required string Id { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required string DetectorId { get; init; }
    public required FlowFindingSeverity Severity { get; init; }
    public string? TargetAgentId { get; init; }
    /// <summary>
    /// Free-form target identifier (PR number, issue number, gate id, etc.) so a
    /// detection can be deduplicated against repeats for the same resource.
    /// </summary>
    public string? TargetResource { get; init; }
    /// <summary>
    /// Human-friendly display name for the target agent (e.g. "Software Engineer 1").
    /// Used by escalation actions (T1.2) that need to look up the target's PRs/issues
    /// by display name (the platform stores PR titles/labels keyed on display name,
    /// not internal agent id). Optional — actions degrade gracefully when null.
    /// </summary>
    public string? TargetDisplayName { get; init; }
    public required string Summary { get; init; }
    public required string Rationale { get; init; }
    public FlowFindingState State { get; init; } = FlowFindingState.Open;
    /// <summary>
    /// Optional dedup key. The FlowMonitor refuses to re-record an identical finding
    /// (same DedupKey) within a configurable window to keep the log signal-only.
    /// </summary>
    public string? DedupKey { get; init; }

    /// <summary>
    /// Diagnostic checks that explain WHY the agent is stuck — not just that it IS stuck.
    /// Populated by <see cref="Diagnostics.IFlowDiagnosticEnricher"/> after detection.
    /// Persisted as JSON in <c>flow_findings.diagnostics_json</c>.
    /// </summary>
    public List<FlowDiagnostic> Diagnostics { get; init; } = new();

    /// <summary>
    /// Machine-readable identifier for the recommended fix action (e.g., "nudge-agent:PM",
    /// "add-label:1628:tests-added"). Null when no automated fix is available.
    /// </summary>
    public string? RecommendedFixId { get; init; }

    /// <summary>
    /// Human-readable description of the recommended fix (e.g., "Nudge TE to assess PR #1628
    /// for testing — TE has not posted a completion comment").
    /// </summary>
    public string? RecommendedFixDescription { get; init; }
}

/// <summary>
/// A single diagnostic check result explaining one aspect of why an agent is stuck.
/// </summary>
public sealed record FlowDiagnostic(
    /// <summary>Name of the check (e.g., "TE completion comment", "architect-approved label").</summary>
    string CheckName,
    /// <summary>Whether this check passed (true) or failed (false = this is a blocker).</summary>
    bool Passed,
    /// <summary>Human-readable detail (e.g., "No comment from TestEngineer found on PR #1628").</summary>
    string Detail);
