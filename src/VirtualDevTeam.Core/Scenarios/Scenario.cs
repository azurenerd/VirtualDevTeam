namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// A structured description of one end-to-end journey through the system — the foundational
/// artifact produced in the wizard and threaded through PMSpec → Architecture → Tasks → PRs →
/// T-FINAL verification.
/// </summary>
/// <remarks>
/// <para>
/// Scenarios are the single source of truth for "what the app must do". They apply universally
/// across all app types: front-end UI, REST API, scheduled job, webhook integration, message
/// consumer, CLI tool, and data pipeline. Only the <see cref="JourneyKind"/> and
/// <see cref="ObservationSurfaces"/> change shape per type.
/// </para>
/// <para>
/// The canonical storage location is the <c># scenarios</c> YAML block embedded inside
/// <c>PMSpec.md</c>. A sidecar <c>scenarios.json</c> is generated as a deterministic mirror
/// by <see cref="IScenarioRegistry"/>; the YAML block is always authoritative.
/// </para>
/// </remarks>
public sealed record Scenario
{
    /// <summary>
    /// Stable short identifier (e.g., <c>S01</c>). Must be unique within a project and
    /// MUST NOT be changed once approved — downstream artifacts (tasks, PRs, tests) reference
    /// this ID by value.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Human-readable scenario title (one line).</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Classifies the initiating mechanism. Determines which <see cref="ObservationSurfaces"/>
    /// are applicable.
    /// </summary>
    public required JourneyKind JourneyKind { get; init; }

    /// <summary>
    /// The entity (human, system, or external service) that initiates the journey
    /// (e.g., <c>"Player"</c>, <c>"Stripe webhook"</c>, <c>"Scheduler (cron 02:00)"</c>).
    /// </summary>
    public required string Actor { get; init; }

    /// <summary>
    /// The concrete action or event that starts the scenario
    /// (e.g., <c>"User clicks 'Build Tower' button"</c>).
    /// </summary>
    public required string Trigger { get; init; }

    /// <summary>Conditions that must be true before the scenario can begin.</summary>
    public IReadOnlyList<string> Preconditions { get; init; } = Array.Empty<string>();

    /// <summary>Numbered, terse, observable execution steps.</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Concrete, observable success criteria. The phrasing depends on
    /// <see cref="JourneyKind"/>: DOM state for UI, HTTP status for API, exit code for CLI, etc.
    /// </summary>
    public IReadOnlyList<string> ExpectedTerminalState { get; init; } = Array.Empty<string>();

    /// <summary>
    /// WHERE and HOW the verifier (T-FINAL / playtester) should observe evidence that the
    /// expected terminal state was reached.
    /// </summary>
    public IReadOnlyList<ObservationSurface> ObservationSurfaces { get; init; } = Array.Empty<ObservationSurface>();

    /// <summary>
    /// Every subsystem, component, or service that must be alive and correctly integrated for
    /// this scenario to succeed. Used by the Architect's Scenario→Component Map.
    /// </summary>
    public IReadOnlyList<string> SubsystemsInvolved { get; init; } = Array.Empty<string>();

    /// <summary>Importance classification; defaults to <see cref="ScenarioPriority.Important"/>.</summary>
    public ScenarioPriority Priority { get; init; } = ScenarioPriority.Important;

    /// <summary>Wizard-approval lifecycle status; defaults to <see cref="ScenarioStatus.Proposed"/>.</summary>
    public ScenarioStatus Status { get; init; } = ScenarioStatus.Proposed;

    /// <summary>
    /// Engineering task identifiers (e.g., <c>"T03: Tower placement UI"</c>) that implement
    /// this scenario. Filled in by the SE leader at task-creation time.
    /// </summary>
    public IReadOnlyList<string> ImplementingTasks { get; init; } = Array.Empty<string>();

    /// <summary>
    /// T-FINAL's post-playtest verdict; defaults to <see cref="VerificationStatus.NotYetVerified"/>.
    /// </summary>
    public VerificationStatus VerificationStatus { get; init; } = VerificationStatus.NotYetVerified;

    /// <summary>
    /// Human-readable reason for the current verification status. Especially useful for
    /// Inconclusive verdicts (e.g., "No implementing tasks tagged", "Action plan generation failed").
    /// </summary>
    public string? VerificationReason { get; init; }

    /// <summary>Link to the playtest artifact produced by T-FINAL when verifying this scenario.</summary>
    public string? VerificationEvidenceUrl { get; init; }

    /// <summary>
    /// When <see langword="true"/>, this item represents infrastructure work (migration,
    /// performance tuning, security hardening) that is not tied to a user-facing journey.
    /// Infrastructure scenarios are exempt from the orphan-detection check in
    /// <see cref="IScenarioRegistry.ValidateNoOrphans"/>.
    /// </summary>
    public bool Infrastructure { get; init; }

    /// <summary>
    /// Whether this scenario is safe for automated interactive validation during T-FINAL.
    /// <see langword="true"/> means the playtester can freely interact with the running app.
    /// <see langword="false"/> means the scenario involves destructive or irreversible actions —
    /// the playtester will verify as much as possible but stop before executing the destructive action.
    /// Defaults to <see langword="true"/>. The AI classifies this during generation; the operator
    /// can override it in the wizard scenario review step.
    /// </summary>
    public bool InteractiveValidationSafe { get; init; } = true;
}
