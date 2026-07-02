using VirtualDevTeam.Core.Strategies.MediaCapture;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Abstraction over screenshot and interaction capture so consumers don't depend
/// on the concrete <see cref="PlaywrightRunner"/>. Enables future swap-in of
/// alternative capture backends (e.g., headless Chrome CDP, cloud browser service).
/// </summary>
public interface IMediaCaptureService
{
    /// <summary>
    /// Captures a single screenshot of the running app's main page.
    /// Equivalent to <see cref="PlaywrightRunner.CaptureAppScreenshotAsync"/>.
    /// </summary>
    Task<PlaywrightRunner.AppScreenshotResult?> CaptureScreenshotAsync(
        string workspacePath, WorkspaceConfig config, CancellationToken ct, string? taskDescription = null);

    /// <summary>
    /// Captures multiple screenshots with optional video/GIF recording.
    /// Equivalent to <see cref="PlaywrightRunner.CaptureAppInteractionAsync"/>.
    /// </summary>
    Task<AppInteractionResult?> CaptureInteractionAsync(
        string workspacePath, WorkspaceConfig config,
        string videoOutputDir, string screenshotOutputDir, string artifactPrefix,
        string? taskTitle = null, string? taskDescription = null,
        IMediaCaptureProgressSink? progressSink = null,
        CancellationToken ct = default,
        CaptureMode captureMode = CaptureMode.FullMedia,
        InteractionPlan? interactionPlan = null);

    /// <summary>Whether the capture backend is validated and ready (browsers installed, etc.).</summary>
    bool IsReady { get; }

    /// <summary>Human-readable reason when <see cref="IsReady"/> is <c>false</c>.</summary>
    string? NotReadyReason { get; }
}
