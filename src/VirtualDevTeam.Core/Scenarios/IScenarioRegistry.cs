namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Event arguments raised by <see cref="IScenarioRegistry.Changed"/> after a load or write.
/// </summary>
public sealed class ScenarioRegistryChangedEventArgs : EventArgs
{
    /// <summary>The new scenario snapshot that was loaded or written.</summary>
    public IReadOnlyList<Scenario> Scenarios { get; }

    /// <summary>Initializes the event args with the updated scenario list.</summary>
    public ScenarioRegistryChangedEventArgs(IReadOnlyList<Scenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        Scenarios = scenarios;
    }
}

/// <summary>
/// Provides typed access to the project's approved <see cref="Scenario"/> objects, loaded from
/// the authoritative <c># scenarios</c> YAML block in <c>PMSpec.md</c> (with optional fallback
/// to a dedicated <c>Scenarios.md</c> file) and mirrored to a <c>scenarios.json</c> sidecar.
/// </summary>
public interface IScenarioRegistry
{
    // -------------------------------------------------------------------------
    // Data access
    // -------------------------------------------------------------------------

    /// <summary>
    /// The snapshot produced by the most recent <see cref="LoadAsync"/> call.
    /// Returns an empty list before the first load.
    /// </summary>
    IReadOnlyList<Scenario> Current { get; }

    /// <summary>
    /// All <see cref="Current"/> scenarios with <see cref="ScenarioPriority.Critical"/> priority.
    /// </summary>
    IReadOnlyList<Scenario> Critical { get; }

    /// <summary>
    /// Find a scenario by its stable <see cref="Scenario.Id"/>. Returns <see langword="null"/>
    /// when not found.
    /// </summary>
    Scenario? FindById(string id);

    // -------------------------------------------------------------------------
    // I/O
    // -------------------------------------------------------------------------

    /// <summary>
    /// Load scenarios from the project's artifact files.
    /// <para>
    /// Resolution order:
    /// <list type="number">
    ///   <item><description><c>Scenarios.md</c> under the current artifact base path (if present, treated as raw YAML).</description></item>
    ///   <item><description><c># scenarios</c> YAML block extracted from <c>PMSpec.md</c>.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// If a <c>scenarios.json</c> sidecar already exists AND the PMSpec YAML block is also
    /// present, the two are compared. A mismatch is logged at <c>Critical</c> level (a
    /// FlowFinding for drift will be wired by WP-H in Wave 2).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Scenario>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Write a <c>scenarios.json</c> sidecar mirroring the supplied scenario list.
    /// Raises <see cref="Changed"/> after a successful write.
    /// </summary>
    Task WriteSidecarAsync(IReadOnlyList<Scenario> scenarios, CancellationToken ct = default);

    /// <summary>
    /// Update the verification status of a single scenario by ID. Thread-safe — acquires a
    /// lock, reloads current snapshot, updates the target scenario's verification fields,
    /// and writes the sidecar. No-op if the scenario ID is not found.
    /// </summary>
    Task UpdateVerificationStatusAsync(
        string scenarioId,
        VerificationStatus status,
        string? reason = null,
        string? evidenceUrl = null,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk-update implementing tasks for scenarios. Called after task creation to close
    /// the scenario→task mapping gap. Thread-safe.
    /// </summary>
    Task UpdateImplementingTasksAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> scenarioIdToTasks,
        CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates that every non-infrastructure scenario in <see cref="Current"/> is cited by
    /// at least one user story in <c>PMSpec.md</c> (searching for <c>Implements Scenarios: SXX</c>
    /// patterns).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when no orphans are found;
    /// <see langword="false"/> when one or more scenarios are unreferenced
    /// (also logs a warning per orphaned scenario).
    /// </returns>
    Task<bool> ValidateNoOrphans(CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>
    /// Raised after a successful <see cref="LoadAsync"/> or <see cref="WriteSidecarAsync"/>.
    /// </summary>
    event EventHandler<ScenarioRegistryChangedEventArgs>? Changed;

    // -------------------------------------------------------------------------
    // Drift detection
    // -------------------------------------------------------------------------

    /// <summary>
    /// <see langword="true"/> when the most recent <see cref="LoadAsync"/> call detected drift
    /// between the <c>scenarios.json</c> sidecar and the PMSpec <c># scenarios</c> YAML block;
    /// <see langword="false"/> otherwise (including before the first load).
    /// Reset to <see langword="false"/> at the start of each <see cref="LoadAsync"/> call.
    /// </summary>
    bool LastLoadHadDrift { get; }
}
