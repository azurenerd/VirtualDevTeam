namespace AgentSquad.Core.Agents.Decisions;

/// <summary>
/// Impact classification scale for agent decisions.
/// Higher levels indicate greater potential impact on the project's code, architecture, or direction.
/// </summary>
public enum DecisionImpactLevel
{
    /// <summary>Extra Small: CSS tweaks, comment fixes, formatting changes.</summary>
    XS = 0,

    /// <summary>Small: Adding a utility method, updating a config value, simple bug fix.</summary>
    S = 1,

    /// <summary>Medium: Refactoring a class, changing API signatures, adding new dependency.</summary>
    M = 2,

    /// <summary>Large: Architectural change, new service/module, database schema change.</summary>
    L = 3,

    /// <summary>Extra Large: Project restructure, tech stack change, major feature pivot.</summary>
    XL = 4,
}

/// <summary>
/// Status of a decision through the gating workflow.
/// </summary>
public enum DecisionStatus
{
    /// <summary>Decision is pending human review (gated).</summary>
    Pending,

    /// <summary>Decision was approved by a human reviewer.</summary>
    Approved,

    /// <summary>Decision was rejected by a human reviewer.</summary>
    Rejected,

    /// <summary>Decision was below the gate threshold and auto-approved.</summary>
    AutoApproved,
}
