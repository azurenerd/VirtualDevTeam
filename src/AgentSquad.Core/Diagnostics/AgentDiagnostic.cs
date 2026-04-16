namespace AgentSquad.Core.Diagnostics;

/// <summary>
/// A point-in-time self-diagnostic snapshot from an agent explaining what it's
/// doing and whether that aligns with its role's expected behavior.
/// </summary>
public sealed record AgentDiagnostic
{
    /// <summary>Short one-line summary for dashboard cards (≤80 chars).</summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Full multi-sentence justification referencing scenarios/requirements.
    /// Shown in tooltips and the Health Monitor deep-dive page.
    /// </summary>
    public string Justification { get; init; } = "";

    /// <summary>True when the agent believes its current activity matches role expectations.</summary>
    public bool IsCompliant { get; init; } = true;

    /// <summary>If non-compliant, a description of the deviation.</summary>
    public string? ComplianceIssue { get; init; }

    /// <summary>The scenario reference that applies (e.g. "Scenario A step 6").</summary>
    public string? ScenarioRef { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>Event args fired when an agent's diagnostic is refreshed.</summary>
public class DiagnosticChangedEventArgs : EventArgs
{
    public required string AgentId { get; init; }
    public required AgentDiagnostic Diagnostic { get; init; }
    /// <summary>True when Summary or IsCompliant actually changed from previous value.</summary>
    public bool IsChanged { get; init; }
}
