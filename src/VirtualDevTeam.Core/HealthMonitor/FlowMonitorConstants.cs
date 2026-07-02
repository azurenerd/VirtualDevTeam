namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Constants for FlowMonitor identity and decision tracking.
/// </summary>
public static class FlowMonitorConstants
{
    /// <summary>
    /// Reserved agent ID for FlowMonitor operational decisions.
    /// Decisions logged with this ID are never gated — they bypass
    /// the approval threshold to prevent recursive deadlock.
    /// </summary>
    public const string AgentId = "flow-monitor";

    /// <summary>Display name shown in decision logs and Approvals page.</summary>
    public const string DisplayName = "FlowMonitor";

    /// <summary>Dedup key prefix for gate-stuck findings.</summary>
    public const string GateStuckPrefix = "gate-stuck";

    /// <summary>Dedup key prefix for PR-approval-stuck findings.</summary>
    public const string PrApprovalStuckPrefix = "pr-approval-stuck";
}
