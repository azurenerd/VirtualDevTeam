using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects when <see cref="RateLimitManager"/> is stuck in a paused state
/// even though the actual API has remaining quota. Emits a Critical finding
/// so the escalation ladder can auto-clear the stale pause via
/// <see cref="RateLimitManager.ClearPause"/>.
///
/// The 5-minute cap on <c>SetGlobalPause</c> prevents indefinite stalls,
/// but this detector provides faster recovery (30s detection vs 5min wait)
/// and surfaces the anomaly in the FlowMonitor log / Approvals page.
/// </summary>
public sealed class StaleRateLimitDetector : IFlowDetector
{
    public string DetectorId => "stale-ratelimit";

    private readonly RateLimitManager _rateLimitManager;
    private readonly ILogger<StaleRateLimitDetector> _logger;

    public StaleRateLimitDetector(
        RateLimitManager rateLimitManager,
        ILogger<StaleRateLimitDetector> logger)
    {
        _rateLimitManager = rateLimitManager;
        _logger = logger;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();

        try
        {
            var (remaining, resetAt, _, isPaused) = _rateLimitManager.GetRateLimitSummary();

            if (!isPaused)
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

            // The manager thinks we're rate-limited. Check if it's stale:
            // If remaining > 100 (we have plenty of quota), the pause is stale.
            // If resetAt is in the past, the window already reset — pause is stale.
            var isStale = remaining > 100 || resetAt < DateTime.UtcNow;

            if (isStale)
            {
                _logger.LogWarning(
                    "StaleRateLimitDetector: manager is paused but remaining={Remaining}, resetAt={ResetAt:HH:mm:ss} UTC — stale pause",
                    remaining, resetAt);

                findings.Add(new FlowFinding
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DetectedAt = ctx.Now,
                    DetectorId = DetectorId,
                    Severity = FlowFindingSeverity.Critical,
                    Summary = $"Rate-limit manager paused but API has {remaining} calls remaining — stale pause state",
                    Rationale = $"RateLimitManager.IsRateLimited=true, but remaining={remaining} (threshold: >100 = stale). " +
                        $"ResetAt={resetAt:HH:mm:ss} UTC. This blocks ALL GitHub/ADO API calls across all agents. " +
                        "Auto-clearing the pause should unblock the pipeline immediately.",
                    DedupKey = "stale-ratelimit",
                    State = FlowFindingState.Open,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StaleRateLimitDetector: check failed (non-fatal)");
        }

        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }
}
