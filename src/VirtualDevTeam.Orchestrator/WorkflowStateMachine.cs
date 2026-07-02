namespace VirtualDevTeam.Orchestrator;

using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Scenarios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

public enum ProjectPhase
{
    Initialization,
    Research,
    Architecture,
    EngineeringPlanning,
    ParallelDevelopment,
    Testing,
    Review,
    Completion
}

public class PhaseTransitionEventArgs : EventArgs
{
    public required ProjectPhase OldPhase { get; init; }
    public required ProjectPhase NewPhase { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Reason { get; init; }
}

public record GateCondition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsMet { get; init; }
}

/// <summary>
/// Manages the project workflow as a linear phase pipeline with gate conditions
/// that must be satisfied before advancing. Agents and external systems signal
/// readiness via <see cref="Signal"/>; the PM can bypass gates with
/// <see cref="ForcePhase"/>.
/// </summary>
public class WorkflowStateMachine
{
    private readonly AgentRegistry _registry;
    private readonly AgentStateStore _stateStore;
    private readonly IGateCheckService _gateCheck;
    private readonly ILogger<WorkflowStateMachine> _logger;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _config;
    private readonly IScenarioRegistry? _scenarioRegistry;

    private readonly object _lock = new();
    private ProjectPhase _currentPhase = ProjectPhase.Initialization;
    private readonly HashSet<string> _signals = new();
    private readonly List<PhaseTransitionEventArgs> _history = new();

    // post-mon-cannot-advance-spam: dedupe the "Cannot advance from X to Y" log spam by tracking
    // the last-logged blocker reason per phase-pair. When the reason changes (gate met, gate
    // unmet differently), we log Information; otherwise Trace.
    private readonly Dictionary<string, string> _lastLoggedBlockerByPair = new();

    // scenarios-absent-degrade: one-time warning flag so we only log the "no scenarios" warning once.
    private bool _scenarioAbsenceWarningLogged;

    /// <summary>
    /// The run ID for scoping workflow state persistence.
    /// Set by <see cref="RunCoordinator"/> when starting/recovering a run.
    /// Defaults to "_global" for backward compatibility.
    /// </summary>
    public string RunId { get; set; } = "_global";

    // Well-known signal constants
    public static class Signals
    {
        public const string ResearchComplete = "research.complete";
        public const string ResearchDocReady = "research.doc.ready";
        public const string ArchitectureComplete = "architecture.complete";
        public const string ArchitectureDocReady = "architecture.doc.ready";
        public const string EngineeringPlanReady = "engineering.plan.ready";
        public const string SoftwareEngineerReady = "software-engineer.ready";
        public const string AllEngineeringComplete = "engineering.all.complete";
        public const string TestCoverageMet = "testing.coverage.met";
        public const string AllReviewsApproved = "reviews.all.approved";

        // Scenario-pipeline signals (also exposed top-level in Signals.cs for external consumers)
        public const string ScenariosApproved = "scenarios.approved";
        public const string ScenariosArchitectureMapped = "scenarios.architecture.mapped";
        public const string ScenariosTasksAssigned = "scenarios.tasks.assigned";
        public const string ScenariosAllCriticalVerified = "scenarios.all_critical_verified";
        public const string ScenariosDriftDetected = "scenarios.drift_detected";
        public const string TestingAppAlive = "testing.app.alive";
    }

    public WorkflowStateMachine(
        AgentRegistry registry,
        AgentStateStore stateStore,
        IGateCheckService gateCheck,
        ILogger<WorkflowStateMachine> logger,
        IOptionsMonitor<VirtualDevTeamConfig>? config = null,
        IScenarioRegistry? scenarioRegistry = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _gateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config; // optional — older test harnesses construct without config
        _scenarioRegistry = scenarioRegistry; // optional — pre-scenarios projects omit this
    }

    /// <summary>Current project phase.</summary>
    public ProjectPhase CurrentPhase
    {
        get { lock (_lock) { return _currentPhase; } }
    }

    /// <summary>Raised after every successful phase transition.</summary>
    public event EventHandler<PhaseTransitionEventArgs>? PhaseChanged;

    // ── Signals ──────────────────────────────────────────────────────

    /// <summary>
    /// Record a readiness signal (e.g. <c>Signals.ResearchComplete</c>).
    /// Agents or external systems call this to indicate a gate criterion is met.
    /// Returns true if the signal was newly added, false if it was already present.
    /// </summary>
    public bool Signal(string signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);

        bool added;
        lock (_lock)
        {
            added = _signals.Add(signal);
        }

        if (added)
        {
            _logger.LogInformation("Signal recorded: '{Signal}'.", signal);
            _ = CheckpointAsync();
        }

        return added;
    }

    /// <summary>Returns true if the given signal has been raised.</summary>
    public bool HasSignal(string signal)
    {
        lock (_lock) { return _signals.Contains(signal); }
    }

    /// <summary>
    /// Snapshot of all currently-raised workflow signals. Read-only copy — safe to share.
    /// Used by the FlowMonitor to populate <c>DetectorContext.WorkflowSignals</c> so detectors
    /// can reason about which gate criteria have fired (e.g., "engineering.all.complete" without
    /// matching merged-PR count).
    /// </summary>
    public IReadOnlyList<string> GetSignals()
    {
        lock (_lock) { return _signals.ToArray(); }
    }

    // ── Phase transitions ────────────────────────────────────────────

    /// <summary>
    /// Attempt to advance to the next phase. All gate conditions for the
    /// current-to-next transition must be met.
    /// </summary>
    /// <returns>True if the transition succeeded.</returns>
    public bool TryAdvancePhase(out string? blockerReason)
    {
        lock (_lock)
        {
            var nextPhase = GetNextPhase(_currentPhase);
            if (nextPhase is null)
            {
                blockerReason = "Already in the final phase (Completion).";
                return false;
            }

            var gates = EvaluateGates(_currentPhase);
            var unmet = gates.Where(g => !g.IsMet).ToList();

            if (unmet.Count > 0)
            {
                blockerReason = string.Join("; ", unmet.Select(g => g.Description));

                // post-mon-cannot-advance-spam fix: only log at Information level when the blocker
                // CHANGES from the last tick (so the operator sees real transitions). Otherwise log
                // at Trace so the noise is hidden by default but still queryable when needed.
                var key = $"{_currentPhase}->{nextPhase}";
                if (_lastLoggedBlockerByPair.TryGetValue(key, out var last) && last == blockerReason)
                {
                    _logger.LogTrace(
                        "Cannot advance from {Current} to {Next}: {Blockers} (unchanged since last tick)",
                        _currentPhase, nextPhase, blockerReason);
                }
                else
                {
                    _logger.LogInformation(
                        "Cannot advance from {Current} to {Next}: {Blockers}",
                        _currentPhase, nextPhase, blockerReason);
                    _lastLoggedBlockerByPair[key] = blockerReason;
                }
                return false;
            }

            // === Gate: FinalReview — human approves phase transition to completion ===
            if (_currentPhase == ProjectPhase.Review && _gateCheck.RequiresHuman(GateIds.FinalReview))
            {
                _logger.LogWarning(
                    "FinalReview gate requires human approval before transitioning to Completion. " +
                    "Use async gate check or approve via GitHub to proceed.");
                blockerReason = "FinalReview gate requires human approval before transitioning to Completion.";
                return false;
            }

            var transition = Transition(_currentPhase, nextPhase.Value, reason: null);
            blockerReason = null;

            _logger.LogInformation(
                "Phase advanced: {Old} → {New}.",
                transition.OldPhase, transition.NewPhase);

            // Clear the spam-suppression cache for this pair — the next time we get blocked, log it.
            _lastLoggedBlockerByPair.Remove($"{transition.OldPhase}->{transition.NewPhase}");

            return true;
        }
    }

    /// <summary>
    /// Force an immediate transition to the specified phase (PM override).
    /// Gate conditions are bypassed.
    /// </summary>
    public void ForcePhase(ProjectPhase phase, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_lock)
        {
            if (_currentPhase == phase)
                return;

            var transition = Transition(_currentPhase, phase, reason);

            _logger.LogWarning(
                "Phase FORCED: {Old} → {New}. Reason: {Reason}",
                transition.OldPhase, transition.NewPhase, reason);
        }
    }

    /// <summary>
    /// Evaluate and return the gate conditions for the current phase's
    /// transition to the next phase.
    /// </summary>
    public IReadOnlyList<GateCondition> GetCurrentGates()
    {
        lock (_lock)
        {
            return EvaluateGates(_currentPhase);
        }
    }

    /// <summary>Returns true if the workflow has reached (or passed) the given phase.</summary>
    public bool HasReachedPhase(ProjectPhase phase)
    {
        lock (_lock) { return _currentPhase >= phase; }
    }

    /// <summary>Ordered list of all transitions that have occurred.</summary>
    public IReadOnlyList<PhaseTransitionEventArgs> GetTransitionHistory()
    {
        lock (_lock) { return _history.ToList().AsReadOnly(); }
    }

    // ── Reset ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reset the workflow to its initial state (Initialization phase, no signals).
    /// Clears the SQLite checkpoint so the next startup begins fresh.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _currentPhase = ProjectPhase.Initialization;
            _signals.Clear();
            _history.Clear();
        }

        await _stateStore.ClearAllCheckpointsAsync(ct);
        _logger.LogWarning("Workflow state machine reset to Initialization (signals and checkpoints cleared)");
    }

    // ── Checkpoint / Recovery ────────────────────────────────────────

    /// <summary>
    /// Persist current phase and signals to SQLite for crash recovery.
    /// Called automatically on every signal and phase transition.
    /// </summary>
    public async Task CheckpointAsync()
    {
        string phase;
        string[] signals;
        lock (_lock)
        {
            phase = _currentPhase.ToString();
            signals = _signals.ToArray();
        }

        try
        {
            await _stateStore.SaveWorkflowStateAsync(phase, signals, RunId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to checkpoint workflow state");
        }
    }

    /// <summary>
    /// Recover phase and signals from SQLite on startup.
    /// Returns true if a checkpoint was found and restored.
    /// </summary>
    public async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        try
        {
            var checkpoint = await _stateStore.LoadWorkflowStateAsync(ct);
            if (checkpoint is null)
                return false;

            if (!Enum.TryParse<ProjectPhase>(checkpoint.Phase, out var phase))
            {
                _logger.LogWarning("Invalid phase '{Phase}' in checkpoint, ignoring", checkpoint.Phase);
                return false;
            }

            var signals = JsonSerializer.Deserialize<List<string>>(checkpoint.SignalsJson) ?? [];

            lock (_lock)
            {
                var oldPhase = _currentPhase;
                _currentPhase = phase;
                foreach (var signal in signals)
                    _signals.Add(signal);

                // Restore run ID from checkpoint for state consistency
                RunId = checkpoint.RunId;

                // Synthesize transition history for all phases from Initialization
                // through the restored phase. The checkpoint only stores the current
                // phase + signals, not the full history. Without this, the Dashboard
                // timeline shows only "Session Started" after a restart because
                // GetTransitionHistory() returns empty and no phase-transition
                // milestones are seeded.
                var synthesizedPhase = ProjectPhase.Initialization;
                while (synthesizedPhase != phase)
                {
                    var next = GetNextPhase(synthesizedPhase);
                    if (next is null) break;
                    _history.Add(new PhaseTransitionEventArgs
                    {
                        OldPhase = synthesizedPhase,
                        NewPhase = next.Value,
                        Reason = "Recovered from checkpoint",
                        Timestamp = checkpoint.Timestamp // Use checkpoint time for all (best available)
                    });
                    synthesizedPhase = next.Value;
                }

                _logger.LogInformation(
                    "Workflow recovered from checkpoint: {Phase} with {SignalCount} signals, run {RunId} (checkpoint age: {Age})",
                    phase, _signals.Count, RunId, DateTime.UtcNow - checkpoint.Timestamp);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recover workflow state from checkpoint");
            return false;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    private PhaseTransitionEventArgs Transition(ProjectPhase from, ProjectPhase to, string? reason)
    {
        var args = new PhaseTransitionEventArgs
        {
            OldPhase = from,
            NewPhase = to,
            Reason = reason
        };

        _currentPhase = to;
        _history.Add(args);

        // Invoke outside the lock would be ideal, but keep simple for now;
        // handlers should be fast and non-blocking.
        PhaseChanged?.Invoke(this, args);

        // Persist the new phase to SQLite for crash recovery
        _ = CheckpointAsync();

        return args;
    }

    private static ProjectPhase? GetNextPhase(ProjectPhase current)
    {
        return current switch
        {
            ProjectPhase.Initialization => ProjectPhase.Research,
            ProjectPhase.Research => ProjectPhase.Architecture,
            ProjectPhase.Architecture => ProjectPhase.EngineeringPlanning,
            ProjectPhase.EngineeringPlanning => ProjectPhase.ParallelDevelopment,
            ProjectPhase.ParallelDevelopment => ProjectPhase.Testing,
            ProjectPhase.Testing => ProjectPhase.Review,
            ProjectPhase.Review => ProjectPhase.Completion,
            _ => null
        };
    }

    private List<GateCondition> EvaluateGates(ProjectPhase current)
    {
        return current switch
        {
            ProjectPhase.Initialization => new List<GateCondition>
            {
                new()
                {
                    Name = "PM Online",
                    Description = "Program Manager agent must be active.",
                    IsMet = _registry.GetAgentsByRole(AgentRole.ProgramManager)
                                .Any(a => a.Status is AgentStatus.Online or AgentStatus.Working or AgentStatus.Idle)
                }
            },

            ProjectPhase.Research => new List<GateCondition>
            {
                new()
                {
                    Name = "Research Document Ready",
                    Description = "Research document must be produced (signal: research.doc.ready).",
                    IsMet = _signals.Contains(Signals.ResearchDocReady)
                },
                new()
                {
                    Name = "Researcher Complete",
                    Description = "Researcher must signal completion (signal: research.complete).",
                    IsMet = _signals.Contains(Signals.ResearchComplete)
                }
            },

            ProjectPhase.Architecture => new List<GateCondition>
            {
                new()
                {
                    Name = "Architecture Document Ready",
                    Description = "Architecture document must be produced (signal: architecture.doc.ready).",
                    IsMet = _signals.Contains(Signals.ArchitectureDocReady)
                },
                new()
                {
                    Name = "Architect Complete",
                    Description = "Architect must signal completion (signal: architecture.complete).",
                    IsMet = _signals.Contains(Signals.ArchitectureComplete)
                },
                new()
                {
                    Name = "Scenarios Architecture Mapped",
                    Description = "All approved scenarios must have at least one component owner per Architecture.md " +
                                  "Scenario→Component Map (signal: scenarios.architecture.mapped).",
                    IsMet = _signals.Contains(Signals.ScenariosArchitectureMapped) || IsScenariosMechanismAbsent()
                }
            },

            ProjectPhase.EngineeringPlanning => new List<GateCondition>
            {
                new()
                {
                    Name = "Engineering Plan Ready",
                    Description = "Engineering plan must be produced (signal: engineering.plan.ready).",
                    IsMet = _signals.Contains(Signals.EngineeringPlanReady)
                },
                new()
                {
                    Name = "Software Engineer Ready",
                    Description = "Software Engineer must signal readiness (signal: software-engineer.ready).",
                    IsMet = _signals.Contains(Signals.SoftwareEngineerReady)
                },
                new()
                {
                    Name = "Scenarios Tasks Assigned",
                    Description = "Every critical scenario must have at least one engineering task implementing it " +
                                  "(signal: scenarios.tasks.assigned).",
                    IsMet = _signals.Contains(Signals.ScenariosTasksAssigned) || IsScenariosMechanismAbsent()
                }
            },

            ProjectPhase.ParallelDevelopment => new List<GateCondition>
            {
                new()
                {
                    Name = "All Engineering Complete",
                    Description = "All engineering tasks must be complete (signal: engineering.all.complete).",
                    IsMet = _signals.Contains(Signals.AllEngineeringComplete)
                }
            },

            ProjectPhase.Testing => new List<GateCondition>
            {
                new()
                {
                    Name = "Test Coverage Met",
                    Description = "Test coverage must meet threshold (signal: testing.coverage.met). " +
                                  "Auto-satisfied when Review.TestEngineerReviews is OFF (TE not participating).",
                    // disable-te-toggle: when TE disabled, auto-satisfy this gate so the workflow can
                    // advance Testing → Review without a TE ever firing the signal.
                    IsMet = _signals.Contains(Signals.TestCoverageMet) || IsTestEngineerDisabled()
                }
            },

            ProjectPhase.Review => new List<GateCondition>
            {
                new()
                {
                    Name = "All Reviews Approved",
                    Description = "All PRs must be reviewed and approved (signal: reviews.all.approved).",
                    IsMet = _signals.Contains(Signals.AllReviewsApproved)
                }
            },

            // Completion — gates represent "done done" criteria (displayed by GetCurrentGates;
            // not used to block a phase transition since there is no phase after Completion).
            ProjectPhase.Completion => new List<GateCondition>
            {
                new()
                {
                    Name = "All Critical Scenarios Verified",
                    Description = "All priority=critical scenarios must have verification_status=verified " +
                                  "(signal: scenarios.all_critical_verified).",
                    IsMet = _signals.Contains(Signals.ScenariosAllCriticalVerified) || IsScenariosMechanismAbsent()
                },
                new()
                {
                    Name = "Application Alive",
                    Description = "Integrated app must boot and pass smoke test (signal: testing.app.alive).",
                    IsMet = _signals.Contains(Signals.TestingAppAlive) || IsScenariosMechanismAbsent()
                }
            },

            _ => new List<GateCondition>()
        };
    }

    /// <summary>
    /// disable-te-toggle: returns true when the operator has set Review.TestEngineerReviews=false.
    /// Used to auto-satisfy the Testing-phase gate so the workflow can advance without TE.
    /// Defaults to false (gate enforced as normal) when no config monitor is bound.
    /// </summary>
    private bool IsTestEngineerDisabled()
    {
        try
        {
            return _config?.CurrentValue?.Review?.TestEngineerReviews == false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the Researcher agent is disabled in config (Agents.Researcher.Enabled=false).
    /// Used by HealthMonitor for crash-recovery: when Researcher is disabled and Research.md exists,
    /// auto-fire research.complete without requiring a Researcher completion phrase.
    /// </summary>
    public bool IsResearcherDisabled()
    {
        try
        {
            return _config?.CurrentValue?.Agents?.Researcher?.Enabled == false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// scenarios-absent-degrade: returns true when no scenarios have been registered AND the
    /// workflow is past the Initialization phase, indicating a project that pre-dates the scenarios
    /// mechanism (or one whose PMSpec has no <c># scenarios</c> YAML block yet).
    /// <para>
    /// When true, all scenario-pipeline gate conditions are auto-satisfied so the workflow can
    /// advance without blocking indefinitely. A one-time Warning is logged so the operator can
    /// decide whether to add scenarios or accept the bypass.
    /// </para>
    /// </summary>
    private bool IsScenariosMechanismAbsent()
    {
        // Registry present with loaded scenarios → mechanism is active, gates enforced normally.
        if (_scenarioRegistry is not null && _scenarioRegistry.Current.Count > 0)
            return false;

        if (_currentPhase == ProjectPhase.Initialization)
            return false;

        if (!_scenarioAbsenceWarningLogged)
        {
            _scenarioAbsenceWarningLogged = true;
            _logger.LogWarning(
                "No scenarios are registered (scenarios.json absent and PMSpec has no # scenarios block). " +
                "All scenario gate conditions will be auto-satisfied. " +
                "Run the Develop wizard to define scenarios if this project requires them.");
        }

        return true;
    }
}
