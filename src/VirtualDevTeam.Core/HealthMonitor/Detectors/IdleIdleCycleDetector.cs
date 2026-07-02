using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// T2.11 IdleIdleCycleDetector — an agent that rapidly transitions Idle↔Working
/// many times within a short window indicates a label-gate predicate mismatch
/// (the agent picks up an issue, immediately determines it doesn't qualify, drops
/// it, and re-polls — ad infinitum). Distinct from AgentStuckDetector (which
/// catches *too-long Working*) and StatusReasonStagnationDetector (catches
/// *unchanging reason*) — this catches *thrashing*.
///
/// <para>
/// State model: per-agent transition log of (timestamp, status) tuples capped at
/// the window. Each tick, we add a new entry only if the agent's status differs
/// from the most recent log entry. When the alternation count in the window
/// exceeds the threshold, fire a Warning. Dedup key includes the count bucket so
/// we don't spam on every tick once threshold is crossed.
/// </para>
/// </summary>
public sealed class IdleIdleCycleDetector : IFlowDetector
{
    public string DetectorId => "idle-idle-cycle";

    private readonly ILogger<IdleIdleCycleDetector> _logger;
    private readonly TimeSpan _window;
    private readonly int _transitionsThreshold;
    private readonly int _historyCap;

    private readonly ConcurrentDictionary<string, List<(DateTimeOffset At, string Status)>> _history = new();

    public IdleIdleCycleDetector(
        ILogger<IdleIdleCycleDetector> logger,
        TimeSpan? window = null,
        int transitionsThreshold = 6,
        int historyCap = 40)
    {
        _logger = logger;
        _window = window ?? TimeSpan.FromMinutes(5);
        _transitionsThreshold = Math.Max(4, transitionsThreshold);
        _historyCap = Math.Max(10, historyCap);
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            foreach (var agent in ctx.Agents)
            {
                var log = _history.GetOrAdd(agent.Id, _ => new List<(DateTimeOffset, string)>());
                lock (log)
                {
                    var status = agent.Status ?? string.Empty;
                    if (log.Count == 0 || !string.Equals(log[^1].Status, status, StringComparison.OrdinalIgnoreCase))
                    {
                        log.Add((ctx.Now, status));
                        if (log.Count > _historyCap) log.RemoveRange(0, log.Count - _historyCap);
                    }

                    var cutoff = ctx.Now - _window;
                    var recent = log.Where(e => e.At >= cutoff).ToList();
                    if (recent.Count < _transitionsThreshold) continue;

                    // Count only alternations that include at least one Idle and one Working.
                    var idleCount = recent.Count(e => string.Equals(e.Status, "Idle", StringComparison.OrdinalIgnoreCase));
                    var workingCount = recent.Count(e => string.Equals(e.Status, "Working", StringComparison.OrdinalIgnoreCase));
                    if (idleCount < 2 || workingCount < 2) continue;

                    var bucket = recent.Count / 2;
                    findings.Add(new FlowFinding
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DetectedAt = ctx.Now,
                        DetectorId = DetectorId,
                        Severity = FlowFindingSeverity.Warning,
                        TargetAgentId = agent.Id,
                        TargetDisplayName = agent.DisplayName,
                        TargetResource = agent.Id,
                        Summary = $"Agent {agent.DisplayName} is thrashing — {recent.Count} status transitions " +
                                  $"in the last {(int)_window.TotalMinutes}m (idle={idleCount}, working={workingCount}).",
                        Rationale = "Rapid Idle↔Working alternation typically means the agent's claim-loop is " +
                                    "picking work it then drops on a second-look predicate. Common causes: " +
                                    "stale labels, race between the assignment query and an in-flight commit, " +
                                    "or a buggy gate-condition. Operator should inspect the agent's recent logs.",
                        DedupKey = $"idle-idle-cycle:{agent.Id}:b{bucket}",
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — propagate so the tick loop can break cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IdleIdleCycleDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }
}
