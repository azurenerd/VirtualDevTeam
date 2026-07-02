namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Top-level scenario-pipeline and application-liveness signal constants.
/// These supplements the <c>WorkflowStateMachine.Signals</c> nested class and are intended
/// for use by FlowMonitor detectors and other Orchestrator components that need to reference
/// scenario signals without going through the outer <c>WorkflowStateMachine</c> type.
/// </summary>
/// <remarks>
/// Inside <c>WorkflowStateMachine</c> itself the identically-named nested class is in scope and
/// shadows this top-level class; the same string constants are added to both to keep the
/// existing public API (<c>WorkflowStateMachine.Signals.*</c>) intact.
/// </remarks>
public static class Signals
{
    /// <summary>PM has approved the full scenario list from the wizard (signal: scenarios.approved).</summary>
    public const string ScenariosApproved = "scenarios.approved";

    /// <summary>
    /// Architect has produced a Scenario→Component map in Architecture.md
    /// (signal: scenarios.architecture.mapped).
    /// </summary>
    public const string ScenariosArchitectureMapped = "scenarios.architecture.mapped";

    /// <summary>
    /// SE leader has linked every critical scenario to at least one engineering task
    /// (signal: scenarios.tasks.assigned).
    /// </summary>
    public const string ScenariosTasksAssigned = "scenarios.tasks.assigned";

    /// <summary>
    /// T-FINAL has verified all priority=critical scenarios (signal: scenarios.all_critical_verified).
    /// </summary>
    public const string ScenariosAllCriticalVerified = "scenarios.all_critical_verified";

    /// <summary>
    /// ScenarioRegistry detected drift between scenarios.json sidecar and PMSpec # scenarios block
    /// (signal: scenarios.drift_detected). Emitted by <see cref="ScenariosDriftDetector"/>.
    /// </summary>
    public const string ScenariosDriftDetected = "scenarios.drift_detected";

    /// <summary>
    /// The integrated application booted successfully and passed a smoke test
    /// (signal: testing.app.alive).
    /// </summary>
    public const string TestingAppAlive = "testing.app.alive";
}
