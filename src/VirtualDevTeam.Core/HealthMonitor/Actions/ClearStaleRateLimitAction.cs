using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.HealthMonitor.Actions;

/// <summary>
/// Rung-1 action for the <c>stale-ratelimit</c> detector: force-clears the
/// <see cref="RateLimitManager"/> pause state. This is the cheapest possible
/// fix — if the pause was genuinely stale, clearing it unblocks all agents
/// immediately. If the actual API IS exhausted, the next API call will
/// re-trigger the pause naturally.
/// </summary>
public sealed class ClearStaleRateLimitAction : IFlowAction
{
    private readonly RateLimitManager _rateLimitManager;
    private readonly ILogger<ClearStaleRateLimitAction> _logger;

    public string ActionType => "clear-stale-ratelimit";
    public int Rung => 1;

    public ClearStaleRateLimitAction(
        RateLimitManager rateLimitManager,
        ILogger<ClearStaleRateLimitAction> logger)
    {
        _rateLimitManager = rateLimitManager;
        _logger = logger;
    }

    public bool CanHandle(FlowFinding finding) =>
        string.Equals(finding.DetectorId, "stale-ratelimit", StringComparison.OrdinalIgnoreCase);

    public Task<FlowActionOutcome> ExecuteAsync(FlowFinding finding, CancellationToken ct)
    {
        _logger.LogInformation(
            "ClearStaleRateLimitAction: force-clearing rate-limit pause for finding {Id}",
            finding.Id);

        _rateLimitManager.ClearPause();

        return Task.FromResult(new FlowActionOutcome
        {
            Result = FlowActionResult.Success,
            Detail = "Rate-limit pause force-cleared. If the API is genuinely exhausted, " +
                "the next API call will re-trigger the pause naturally.",
        });
    }
}
