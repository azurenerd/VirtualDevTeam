namespace VirtualDevTeam.Orchestrator;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;
using VirtualDevTeam.Core.Scenarios;

/// <summary>
/// FlowMonitor detector that surfaces <c>scenarios.json</c> ↔ PMSpec drift as a
/// <see cref="FlowFinding"/> with <see cref="FlowFindingSeverity.Critical"/> severity.
///
/// <para>
/// The underlying <see cref="IScenarioRegistry"/> already performs drift detection inside
/// <c>LoadAsync</c> and logs at <c>Critical</c> level. This detector bridges that event into
/// the FlowMonitor pipeline so the finding is persisted to SQLite and surfaced on the Health
/// Monitor dashboard — enabling escalation-ladder and operator-approval flows.
/// </para>
///
/// <para>
/// Design: the detector subscribes to <see cref="IScenarioRegistry.Changed"/>. On each event,
/// if <see cref="IScenarioRegistry.LastLoadHadDrift"/> is <see langword="true"/>, a drift event
/// is recorded. On each FlowMonitor tick (<see cref="DetectAsync"/>), drift events within a
/// 5-minute TTL window are replayed as findings. The dedup key
/// <c>"scenarios-drift"</c> ensures the FlowMonitor's own dedup window prevents log spam.
/// </para>
/// </summary>
public sealed class ScenariosDriftDetector : IFlowDetector
{
    public string DetectorId => "scenarios-drift";

    private static readonly TimeSpan EventTtl = TimeSpan.FromMinutes(5);

    private readonly IScenarioRegistry _registry;
    private readonly ILogger<ScenariosDriftDetector> _logger;

    private readonly object _eventsLock = new();
    private readonly List<DriftEvent> _recentDriftEvents = new();

    /// <param name="registry">Scenario registry; the detector subscribes to its Changed event.</param>
    /// <param name="logger">Structured logger for non-fatal internal failures.</param>
    public ScenariosDriftDetector(IScenarioRegistry registry, ILogger<ScenariosDriftDetector> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
        _registry.Changed += OnRegistryChanged;
    }

    private void OnRegistryChanged(object? sender, ScenarioRegistryChangedEventArgs e)
    {
        try
        {
            if (!_registry.LastLoadHadDrift)
                return;

            var scenarioIds = e.Scenarios.Select(s => s.Id).ToList();
            var driftEvent = new DriftEvent(DateTimeOffset.UtcNow, scenarioIds);

            lock (_eventsLock)
            {
                _recentDriftEvents.Add(driftEvent);
            }

            _logger.LogWarning(
                "ScenariosDriftDetector recorded a drift event. " +
                "Loaded scenario IDs in registry: [{Ids}]. " +
                "Check Critical-level ScenarioRegistry logs for the exact diverging IDs.",
                string.Join(", ", scenarioIds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScenariosDriftDetector failed to record Changed event (non-fatal)");
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            var cutoff = ctx.Now - EventTtl;
            List<DriftEvent> active;
            lock (_eventsLock)
            {
                _recentDriftEvents.RemoveAll(e => e.OccurredAt < cutoff);
                active = _recentDriftEvents.ToList();
            }

            foreach (var evt in active)
            {
                // All drift events within the TTL window share the same dedup key so the
                // FlowMonitor suppresses duplicates and only escalates once per window.
                var idsStr = evt.ScenarioIds.Count > 0
                    ? string.Join(", ", evt.ScenarioIds)
                    : "(none loaded)";

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = evt.OccurredAt,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Critical,
                    TargetResource = "scenarios.json",
                    Summary = "scenarios.json sidecar has drifted from PMSpec.md # scenarios block.",
                    Rationale =
                        "The ScenarioRegistry detected that the IDs in scenarios.json differ from " +
                        "the IDs in the PMSpec.md # scenarios YAML block. " +
                        $"Registry snapshot scenario IDs at detection time: [{idsStr}]. " +
                        "The PMSpec YAML block is always authoritative. " +
                        "Regenerate the sidecar by calling IScenarioRegistry.WriteSidecarAsync, " +
                        "or check the Critical-level ScenarioRegistry log entries for exact diverging IDs.",
                    DedupKey = "scenarios-drift"
                });

                // Only emit one finding per tick even if multiple drift events are queued;
                // they share the same dedup key so extras would be suppressed anyway.
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScenariosDriftDetector tick failed (non-fatal)");
        }

        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    private sealed record DriftEvent(DateTimeOffset OccurredAt, IReadOnlyList<string> ScenarioIds);
}
