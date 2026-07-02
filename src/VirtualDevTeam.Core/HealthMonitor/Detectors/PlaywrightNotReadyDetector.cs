using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.HealthMonitor.Detectors;

/// <summary>
/// Detects when <see cref="PlaywrightRunner.IsReady"/> is false during phases
/// where candidate previews matter (ParallelDevelopment and later). All strategy
/// framework candidates silently produce NoVisualContent when Playwright can't
/// launch — this detector surfaces the issue so the operator can fix it.
///
/// Common root cause: browser version mismatch — the installed Chromium revision
/// doesn't match what the Playwright .NET package expects. The auto-heal in
/// <see cref="PlaywrightRunner.ValidateAsync"/> should fix this, but if it fails
/// this detector ensures visibility.
/// </summary>
public sealed class PlaywrightNotReadyDetector : IFlowDetector
{
    public string DetectorId => "playwright-not-ready";

    private readonly PlaywrightRunner _playwright;
    private readonly ILogger<PlaywrightNotReadyDetector> _logger;

    /// <summary>Phases where Playwright matters for candidate previews.</summary>
    private static readonly HashSet<string> RelevantPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "ParallelDevelopment", "Testing", "Review", "Completion"
    };

    public PlaywrightNotReadyDetector(
        PlaywrightRunner playwright,
        ILogger<PlaywrightNotReadyDetector> logger)
    {
        _playwright = playwright;
        _logger = logger;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();

        try
        {
            // Only fire during phases where previews are generated
            if (!RelevantPhases.Contains(ctx.CurrentPhase))
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

            if (_playwright.IsReady)
                return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);

            var reason = _playwright.NotReadyReason ?? "unknown reason";
            _logger.LogDebug("PlaywrightNotReadyDetector: Playwright not ready — {Reason}", reason);

            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = ctx.Now,
                DetectorId = DetectorId,
                Severity = FlowFindingSeverity.Critical,
                Summary = "Playwright not ready — all candidate previews will show 'Capture unavailable'",
                Rationale = $"PlaywrightRunner.IsReady=false: {reason}. " +
                    "This means the strategy framework cannot take app screenshots, record video, " +
                    "or generate GIFs for any candidate. All previews will silently show the " +
                    "'Capture unavailable' badge. " +
                    "Fix: run 'pwsh playwright.ps1 install chromium' from the Runner's bin directory, " +
                    "or restart the Runner (the auto-heal in ValidateAsync should reinstall on next startup).",
                DedupKey = "playwright-not-ready",
                State = FlowFindingState.Open,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PlaywrightNotReadyDetector: check failed (non-fatal)");
        }

        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }
}
