namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Lifecycle status of a scenario within the wizard-approval workflow.
/// Only <see cref="Approved"/> scenarios are consumed by downstream agents.
/// </summary>
public enum ScenarioStatus
{
    /// <summary>AI-generated draft — awaiting operator review.</summary>
    Proposed,

    /// <summary>Operator has explicitly approved this scenario for implementation.</summary>
    Approved,

    /// <summary>Operator has edited the AI proposal; content reflects operator intent.</summary>
    Edited,

    /// <summary>Operator has rejected this scenario; excluded from implementation scope.</summary>
    Rejected,
}
