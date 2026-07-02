using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// <see cref="ICandidatePreviewProducer"/> implementation that wraps the existing
/// <see cref="PlaywrightRunner.CaptureAppScreenshotAsync"/> flow. Runs LAST in the
/// chain (<see cref="Priority"/> = 100) so future image-asset / diagram producers win
/// when the PR's deliverable is content rather than a runnable app.
/// </summary>
/// <remarks>
/// <para>
/// This producer is intentionally thin so the orchestration swap-in (the follow-up
/// <c>previewproducer-orchestration</c> todo) is mostly a one-line change in
/// <c>CandidateEvaluator</c>. The full multi-page interaction / video / contact-sheet
/// pipeline currently in <c>CandidateEvaluator</c> stays where it is for now — this
/// producer just exposes the single-screenshot path through the new abstraction.
/// </para>
/// <para>
/// Returns <c>null</c> when Playwright is not ready, screenshot capture is disabled,
/// or the capture itself yields no bytes — the orchestrator then falls through to the
/// next producer (or the <c>NoVisualContent</c> placeholder).
/// </para>
/// </remarks>
public sealed class PlaywrightCandidatePreviewProducer : ICandidatePreviewProducer
{
    private readonly PlaywrightRunner? _playwright;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _appCfg;
    private readonly ILogger<PlaywrightCandidatePreviewProducer> _logger;

    public PlaywrightCandidatePreviewProducer(
        ILogger<PlaywrightCandidatePreviewProducer> logger,
        PlaywrightRunner? playwright = null,
        IOptionsMonitor<VirtualDevTeamConfig>? appCfg = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _playwright = playwright;
        _appCfg = appCfg;
    }

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public string Id => "playwright";

    /// <inheritdoc />
    public async Task<CandidatePreview?> TryProduceAsync(CandidatePreviewContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_playwright is null)
        {
            _logger.LogDebug("PlaywrightRunner not registered — skipping playwright preview producer.");
            return null;
        }

        if (!_playwright.IsReady)
        {
            _logger.LogDebug(
                "Playwright not ready ({Reason}) — skipping playwright preview producer.",
                _playwright.NotReadyReason ?? "unknown");
            return null;
        }

        var workspaceCfg = _appCfg?.CurrentValue?.Workspace;
        if (workspaceCfg is null || !workspaceCfg.CaptureScreenshots)
        {
            _logger.LogDebug("Screenshot capture disabled by workspace config — skipping playwright preview producer.");
            return null;
        }

        // Clone config so capture can mutate AppStartCommand safely (matches existing pattern in CandidateEvaluator).
        var configSnapshot = new WorkspaceConfig
        {
            AppStartCommand = workspaceCfg.AppStartCommand,
            AppBaseUrl = workspaceCfg.AppBaseUrl,
            ScreenshotRenderDelaySeconds = workspaceCfg.ScreenshotRenderDelaySeconds,
            BuildCommand = workspaceCfg.BuildCommand,
            PlaywrightBrowsersCachePath = workspaceCfg.PlaywrightBrowsersCachePath,
            CaptureScreenshots = true,
        };

        PlaywrightRunner.AppScreenshotResult? result;
        try
        {
            result = await _playwright.CaptureAppScreenshotAsync(
                context.CandidateWorktreePath, configSnapshot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Playwright capture threw for task {TaskId}/{Strategy} — declining preview.",
                context.TaskId, context.StrategyId);
            return null;
        }

        if (result is null || result.Bytes.Length == 0)
        {
            _logger.LogDebug(
                "Playwright capture returned no bytes for task {TaskId}/{Strategy} — declining preview.",
                context.TaskId, context.StrategyId);
            return null;
        }

        return new CandidatePreview
        {
            SourceProducerId = Id,
            ScreenshotBase64 = Convert.ToBase64String(result.Bytes),
            Source = CandidatePreviewSource.PlaywrightScreenshot,
        };
    }
}
