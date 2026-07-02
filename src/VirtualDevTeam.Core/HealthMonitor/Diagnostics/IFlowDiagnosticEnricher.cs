using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Core.HealthMonitor.Diagnostics;

/// <summary>
/// Enriches a <see cref="FlowFinding"/> with diagnostic context explaining WHY an agent
/// is stuck — not just that it IS stuck. Runs deterministically (no LLM, no AI) after
/// detection and before action selection.
///
/// Enrichers are registered in DI and called by <see cref="FlowMonitorService"/> for each
/// finding whose detector ID they claim via <see cref="CanEnrich"/>.
/// </summary>
public interface IFlowDiagnosticEnricher
{
    /// <summary>Whether this enricher can provide diagnostics for the given detector.</summary>
    bool CanEnrich(string detectorId);

    /// <summary>
    /// Analyze the finding's context and return an enriched copy with diagnostics,
    /// recommended fix ID, and fix description. Must be fast (&lt;2s), fault-tolerant,
    /// and deterministic (pure logic + cached platform data).
    /// </summary>
    Task<FlowFinding> EnrichAsync(FlowFinding finding, DetectorContext ctx, CancellationToken ct);
}
