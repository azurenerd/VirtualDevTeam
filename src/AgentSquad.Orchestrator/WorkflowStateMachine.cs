namespace AgentSquad.Orchestrator;

using AgentSquad.Core.Agents;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<WorkflowStateMachine> _logger;

    private readonly object _lock = new();
    private ProjectPhase _currentPhase = ProjectPhase.Initialization;
    private readonly HashSet<string> _signals = new();
    private readonly List<PhaseTransitionEventArgs> _history = new();

    // Well-known signal constants
    public static class Signals
    {
        public const string ResearchComplete = "research.complete";
        public const string ResearchDocReady = "research.doc.ready";
        public const string ArchitectureComplete = "architecture.complete";
        public const string ArchitectureDocReady = "architecture.doc.ready";
        public const string EngineeringPlanReady = "engineering.plan.ready";
        public const string PrincipalEngineerReady = "principal.ready";
        public const string AllEngineeringComplete = "engineering.all.complete";
        public const string TestCoverageMet = "testing.coverage.met";
        public const string AllReviewsApproved = "reviews.all.approved";
    }

    public WorkflowStateMachine(
        AgentRegistry registry,
        AgentStateStore stateStore,
        ILogger<WorkflowStateMachine> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    /// </summary>
    public void Signal(string signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);

        lock (_lock)
        {
            _signals.Add(signal);
        }

        _logger.LogInformation("Signal recorded: '{Signal}'.", signal);
        _ = CheckpointAsync();
    }

    /// <summary>Returns true if the given signal has been raised.</summary>
    public bool HasSignal(string signal)
    {
        lock (_lock) { return _signals.Contains(signal); }
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
                _logger.LogInformation(
                    "Cannot advance from {Current} to {Next}: {Blockers}",
                    _currentPhase, nextPhase, blockerReason);
                return false;
            }

            var transition = Transition(_currentPhase, nextPhase.Value, reason: null);
            blockerReason = null;

            _logger.LogInformation(
                "Phase advanced: {Old} → {New}.",
                transition.OldPhase, transition.NewPhase);

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
            await _stateStore.SaveWorkflowStateAsync(phase, signals);
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

                _logger.LogInformation(
                    "Workflow recovered from checkpoint: {Phase} with {SignalCount} signals (checkpoint age: {Age})",
                    phase, _signals.Count, DateTime.UtcNow - checkpoint.Timestamp);
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
                    Name = "Principal Engineer Ready",
                    Description = "Principal Engineer must signal readiness (signal: principal.ready).",
                    IsMet = _signals.Contains(Signals.PrincipalEngineerReady)
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
                    Description = "Test coverage must meet threshold (signal: testing.coverage.met).",
                    IsMet = _signals.Contains(Signals.TestCoverageMet)
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

            // Completion — no further gates
            _ => new List<GateCondition>()
        };
    }
}
