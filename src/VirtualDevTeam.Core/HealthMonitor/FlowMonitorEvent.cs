namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Classification of a <see cref="FlowMonitorEvent"/>. Maps onto the visual style
/// in the FlowMonitor live-log terminal:
/// <list type="bullet">
///   <item><c>Lifecycle</c> — service / tick boundaries (gray).</item>
///   <item><c>Detector</c> — detector start / finish (cyan reasoning style).</item>
///   <item><c>Finding</c> — a detector emitted an observation (magenta/purple, mimicking
///       the assistant ● in Copilot CLI).</item>
///   <item><c>Action</c> — an action was started (green ●, mimicking the tool intent).</item>
///   <item><c>ActionResult</c> — an action completed (green for success / NoOp / Skipped,
///       red for Failed).</item>
///   <item><c>Info</c> — generic informational message (gray).</item>
///   <item><c>Error</c> — service error / unhandled exception (red).</item>
/// </list>
/// </summary>
public enum FlowMonitorEventKind
{
    Lifecycle,
    Detector,
    Finding,
    Action,
    ActionResult,
    Info,
    Error,
    /// <summary>A finding's state or severity changed (so dashboard clients know to re-fetch).</summary>
    StatusChange,
}

/// <summary>
/// Verbosity level for the FlowMonitor live log. Mirrors the LangSmith / telegram
/// monitor pattern. The hub fans out every event regardless of level; the client-side
/// terminal filters by the verbosity selector so a single bus serves all viewers.
/// </summary>
public enum FlowMonitorVerbosity
{
    /// <summary>Lifecycle + Findings (assistant) + Errors only — the "executive summary" view.</summary>
    Low = 0,
    /// <summary>Default. Adds Detector + Action + ActionResult — the "operator" view.</summary>
    Medium = 1,
    /// <summary>Adds Info — the full firehose.</summary>
    High = 2,
}

/// <summary>
/// A single observation by the FlowMonitor that the dashboard renders into the
/// Copilot-CLI-style live log. Lightweight, immutable, JSON-friendly.
/// </summary>
/// <remarks>
/// Tag every event with the current async <see cref="VirtualDevTeam.Core.AI.AgentCallContext.CurrentAgentId"/>
/// and <see cref="VirtualDevTeam.Core.AI.AgentCallContext.CurrentSessionId"/> so the UI can attribute the line
/// to the agent that caused it. The convenience helpers on
/// <see cref="FlowMonitorEventBus"/> handle this automatically.
/// </remarks>
public sealed record FlowMonitorEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required FlowMonitorEventKind Kind { get; init; }
    /// <summary>
    /// Logical source of the event. Detector id (e.g., "agent-stuck"), action type
    /// (e.g., "kick-agent-poll"), or "service" for lifecycle / errors.
    /// </summary>
    public required string Source { get; init; }
    public required string Message { get; init; }
    /// <summary>Optional: the finding this event relates to (lets the UI filter / link).</summary>
    public string? FindingId { get; init; }
    /// <summary>Optional: the action this event relates to.</summary>
    public string? ActionId { get; init; }
    /// <summary>Optional: agent attribution (auto-tagged from <c>AgentCallContext</c>).</summary>
    public string? AgentId { get; init; }
    /// <summary>Optional: copilot CLI session attribution (auto-tagged from <c>AgentCallContext</c>).</summary>
    public string? SessionId { get; init; }
    /// <summary>
    /// Optional severity for finding events (so the client can colour Critical findings
    /// red even though they share the magenta "assistant" channel).
    /// </summary>
    public FlowFindingSeverity? Severity { get; init; }
    /// <summary>Optional outcome for action-result events.</summary>
    public FlowActionResult? ActionResult { get; init; }
    /// <summary>Free-form longer payload (rationale, error details). Truncated by the publisher.</summary>
    public string? Detail { get; init; }
}
