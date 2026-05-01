using AgentSquad.Core.Agents;
using AgentSquad.Core.Diagnostics;
using AgentSquad.Orchestrator;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// Manages diagnostic history and builds execution health assessments.
/// Pure data/logic service — no SignalR, no event subscriptions. The facade coordinates events.
/// </summary>
public sealed class DiagnosticSummaryService
{
    private readonly HealthMonitor _healthMonitor;
    private readonly DeadlockDetector _deadlockDetector;
    private readonly WorkflowStateMachine _workflow;
    private readonly AgentSnapshotService _snapshotService;
    private readonly ILogger<DiagnosticSummaryService> _logger;

    private readonly List<DiagnosticHistoryEntry> _diagnosticHistory = new();
    private readonly Dictionary<string, DateTime> _lastDiagnosticRecordTime = new();
    private readonly object _lock = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private const int MaxDiagnosticHistory = 500;

    internal AgentHealthSnapshot? LastHealthSnapshot { get; set; }

    public DiagnosticSummaryService(
        HealthMonitor healthMonitor,
        DeadlockDetector deadlockDetector,
        WorkflowStateMachine workflow,
        AgentSnapshotService snapshotService,
        ILogger<DiagnosticSummaryService> logger)
    {
        _healthMonitor = healthMonitor;
        _deadlockDetector = deadlockDetector;
        _workflow = workflow;
        _snapshotService = snapshotService;
        _logger = logger;
    }

    public AgentHealthSnapshot GetCurrentHealthSnapshot() =>
        LastHealthSnapshot ?? _healthMonitor.GetSnapshot();

    public bool HasDeadlock(out List<string>? cycle) =>
        _deadlockDetector.HasDeadlock(out cycle);

    /// <summary>Refresh health snapshot from HealthMonitor. Returns it for SignalR push.</summary>
    public AgentHealthSnapshot RefreshHealthSnapshot()
    {
        LastHealthSnapshot = _healthMonitor.GetSnapshot();
        return LastHealthSnapshot;
    }

    /// <summary>Record a diagnostic event to history if appropriate (changed or heartbeat).</summary>
    public void RecordDiagnostic(DiagnosticChangedEventArgs e, string displayName, AgentRole role)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var lastRecorded = _lastDiagnosticRecordTime.GetValueOrDefault(e.AgentId, DateTime.MinValue);
            bool shouldRecord = e.IsChanged || (now - lastRecorded).TotalSeconds >= 30;

            if (!shouldRecord) return;

            _lastDiagnosticRecordTime[e.AgentId] = now;
            _diagnosticHistory.Add(new DiagnosticHistoryEntry
            {
                AgentId = e.AgentId,
                AgentDisplayName = displayName,
                Role = role,
                Summary = e.Diagnostic.Summary,
                Justification = e.Diagnostic.Justification,
                IsCompliant = e.Diagnostic.IsCompliant,
                ComplianceIssue = e.Diagnostic.ComplianceIssue,
                ScenarioRef = e.Diagnostic.ScenarioRef,
                Timestamp = e.Diagnostic.Timestamp
            });

            if (_diagnosticHistory.Count > MaxDiagnosticHistory)
                _diagnosticHistory.RemoveRange(0, _diagnosticHistory.Count - MaxDiagnosticHistory);
        }
    }

    public IReadOnlyList<DiagnosticHistoryEntry> GetDiagnosticHistory(
        string? agentIdFilter = null, bool? compliantFilter = null, int limit = 200)
    {
        lock (_lock)
        {
            IEnumerable<DiagnosticHistoryEntry> query = _diagnosticHistory;

            if (agentIdFilter is not null)
                query = query.Where(e => e.AgentId == agentIdFilter);
            if (compliantFilter.HasValue)
                query = query.Where(e => e.IsCompliant == compliantFilter.Value);

            return query.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
        }
    }

    public ExecutionHealthAssessment GetExecutionHealthAssessment()
    {
        var snapshot = LastHealthSnapshot ?? _healthMonitor.GetSnapshot();
        var hasDeadlock = _deadlockDetector.HasDeadlock(out var deadlockCycle);
        var currentPhase = _workflow.CurrentPhase;
        var gates = _workflow.GetCurrentGates();
        var transitionHistory = _workflow.GetTransitionHistory();

        var agents = _snapshotService.GetAll();

        var workingCount = agents.Count(a => a.Status == AgentStatus.Working);
        var compliantCount = agents.Count(a => a.DiagnosticCompliant);
        var nonCompliantCount = agents.Count(a => !a.DiagnosticCompliant);
        var errorCount = agents.Count(a => a.ErrorCount > 0);

        var observations = new List<string>();

        // Phase assessment
        observations.Add($"Workflow is in the **{FormatPhase(currentPhase)}** phase.");
        if (transitionHistory.Count > 0)
        {
            var lastTransition = transitionHistory[^1];
            var sinceTransition = DateTime.UtcNow - lastTransition.Timestamp;
            observations.Add($"Entered current phase {FormatTimeAgo(sinceTransition)} ago.");
        }

        // Gate conditions
        var metGates = gates.Count(g => g.IsMet);
        var totalGates = gates.Count;
        if (currentPhase < ProjectPhase.Completion)
            observations.Add($"Next phase gates: {metGates}/{totalGates} met.");

        // Agent health
        if (nonCompliantCount > 0)
        {
            var ncAgents = agents.Where(a => !a.DiagnosticCompliant).Select(a => a.DisplayName);
            observations.Add($"⚠️ {nonCompliantCount} agent(s) report non-compliant behavior: {string.Join(", ", ncAgents)}.");
        }
        else if (agents.Count > 0)
        {
            observations.Add("All agents report compliant behavior.");
        }

        if (hasDeadlock && deadlockCycle is not null)
            observations.Add($"🔴 Deadlock detected involving: {string.Join(" → ", deadlockCycle)}.");

        if (errorCount > 0)
        {
            var errAgents = agents.Where(a => a.ErrorCount > 0).Select(a => $"{a.DisplayName} ({a.ErrorCount})");
            observations.Add($"⚠️ {errorCount} agent(s) have errors: {string.Join(", ", errAgents)}.");
        }

        // Working agents
        if (workingCount > 0)
        {
            var workingNames = agents.Where(a => a.Status == AgentStatus.Working).Select(a => a.DisplayName);
            observations.Add($"Currently working: {string.Join(", ", workingNames)}.");
        }
        else
        {
            observations.Add("No agents are currently working — all idle or waiting.");
        }

        // Blocked agents
        var blockedAgents = agents.Where(a => a.Status == AgentStatus.Blocked).ToList();
        if (blockedAgents.Count > 0)
        {
            var blocked = blockedAgents.Select(a => $"{a.DisplayName}: {a.StatusReason ?? "unknown"}");
            observations.Add($"🟡 Blocked agents: {string.Join("; ", blocked)}.");
        }

        // Overall status determination
        var overallStatus = "Healthy";
        if (hasDeadlock) overallStatus = "Critical";
        else if (nonCompliantCount > 0 || blockedAgents.Count > 0) overallStatus = "Warning";
        else if (errorCount > 0) overallStatus = "Caution";

        // Build phase timeline
        var allPhases = Enum.GetValues<ProjectPhase>();
        var completedTransitions = transitionHistory
            .GroupBy(t => t.OldPhase)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Timestamp).Last().Timestamp);

        var timeline = allPhases.Select(p => new PhaseTimelineEntry
        {
            Phase = FormatPhase(p),
            IsCompleted = p < currentPhase,
            IsCurrent = p == currentPhase,
            CompletedAt = completedTransitions.GetValueOrDefault(p)
        }).ToList();

        return new ExecutionHealthAssessment
        {
            OverallStatus = overallStatus,
            Phase = FormatPhase(currentPhase),
            Uptime = DateTime.UtcNow - _startedAt,
            TotalAgents = agents.Count,
            WorkingAgents = workingCount,
            CompliantAgents = compliantCount,
            NonCompliantAgents = nonCompliantCount,
            ErrorAgents = errorCount,
            HasDeadlock = hasDeadlock,
            Observations = observations,
            NextPhaseGates = gates.ToList(),
            PhaseTimeline = timeline
        };
    }

    /// <summary>Clear all diagnostic data. Called by facade during project reset.</summary>
    public void ResetCaches()
    {
        lock (_lock)
        {
            _diagnosticHistory.Clear();
            _lastDiagnosticRecordTime.Clear();
        }
    }

    internal static string FormatPhase(ProjectPhase phase) => phase switch
    {
        ProjectPhase.EngineeringPlanning => "Engineering Planning",
        ProjectPhase.ParallelDevelopment => "Parallel Development",
        _ => phase.ToString()
    };

    internal static string FormatTimeAgo(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
