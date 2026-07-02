using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// FlowMonitor detector that surfaces deadlock cycles found by <see cref="DeadlockDetector"/>
/// as <see cref="FlowFinding"/>s so they appear on the Health Monitor Flow Monitor card and
/// are persisted to SQLite via <c>FlowMonitorPersistence</c>.
///
/// The underlying <see cref="DeadlockDetector"/> raises an event whenever a cycle is observed
/// in the wait-for graph; this wrapper subscribes to that event, caches the events with a
/// 5-minute TTL, and replays them as findings on each FlowMonitor tick. We do not modify the
/// <see cref="DeadlockDetector"/> itself — pure observation.
///
/// The detector lives in the Orchestrator project (rather than Core) because Core does not
/// reference Orchestrator, and the detector takes a hard dependency on the
/// <see cref="DeadlockDetector"/> concrete type.
/// </summary>
public sealed class DeadlockFlowDetector : IFlowDetector
{
    public string DetectorId => "deadlock";

    private static readonly TimeSpan EventTtl = TimeSpan.FromMinutes(5);

    private readonly DeadlockDetector _deadlockDetector;
    private readonly ILogger<DeadlockFlowDetector> _logger;

    private readonly object _eventsLock = new();
    private readonly List<DeadlockDetectedEventArgs> _recentEvents = new();

    public DeadlockFlowDetector(DeadlockDetector deadlockDetector, ILogger<DeadlockFlowDetector> logger)
    {
        _deadlockDetector = deadlockDetector ?? throw new ArgumentNullException(nameof(deadlockDetector));
        _logger = logger;
        _deadlockDetector.DeadlockDetected += OnDeadlockDetected;
    }

    private void OnDeadlockDetected(object? sender, DeadlockDetectedEventArgs e)
    {
        try
        {
            lock (_eventsLock)
            {
                _recentEvents.Add(e);
            }
            _logger.LogWarning(
                "Deadlock detected in wait-graph (cycle of {Count} agents): {Cycle}",
                e.AgentCycle.Count, string.Join(" → ", e.AgentCycle));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeadlockFlowDetector failed to record DeadlockDetected event (non-fatal)");
        }
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var cutoffUtc = ctx.Now.UtcDateTime - EventTtl;
            List<DeadlockDetectedEventArgs> active;
            lock (_eventsLock)
            {
                _recentEvents.RemoveAll(e => e.DetectedAt < cutoffUtc);
                active = _recentEvents.ToList();
            }

            foreach (var evt in active)
            {
                var distinctAgents = evt.AgentCycle.Distinct().ToList();
                var cycleHash = ComputeCycleHash(distinctAgents);
                var cycleStr = string.Join(" → ", evt.AgentCycle);

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = new DateTimeOffset(DateTime.SpecifyKind(evt.DetectedAt, DateTimeKind.Utc), TimeSpan.Zero),
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Critical,
                    TargetAgentId = distinctAgents.FirstOrDefault(),
                    TargetResource = "wait-graph",
                    Summary = $"Deadlock detected in agent wait-graph: {cycleStr}",
                    Rationale = $"The DeadlockDetector found a cycle in the wait-for graph involving " +
                                $"{distinctAgents.Count} agent(s): {string.Join(", ", distinctAgents)}. " +
                                "Each agent is blocked waiting on the next, so none can make progress until " +
                                "an external nudge breaks the cycle (e.g., releasing a resource, clearing a wait, " +
                                "or restarting one of the agents).",
                    DedupKey = $"deadlock:{cycleHash}",
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeadlockFlowDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    /// <summary>
    /// Cycles are equivalent regardless of starting node — A→B→C→A is the same cycle as
    /// B→C→A→B. Normalize by sorting the distinct agent ids before hashing so that repeated
    /// observations of the same cycle dedup against each other.
    /// </summary>
    private static string ComputeCycleHash(IEnumerable<string> distinctAgents)
    {
        var sorted = distinctAgents.OrderBy(s => s, StringComparer.Ordinal);
        var joined = string.Join("|", sorted);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
