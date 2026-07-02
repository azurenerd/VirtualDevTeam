using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Handles video recording and animated GIF generation from app interaction sessions.
/// Uses Playwright's built-in video recording with FFmpeg for GIF conversion.
/// Extracted from PlaywrightRunner to separate media concerns.
/// </summary>
public sealed class MediaRecorder
{
    private readonly ILogger<MediaRecorder> _logger;

    public MediaRecorder(ILogger<MediaRecorder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Fires an async background video recording for the given pages.
    /// Creates a separate Playwright context with video recording, visits the pages,
    /// closes context to finalize video, then returns. Non-blocking to caller when
    /// wrapped in Task.Run. Has its own 3-minute timeout.
    /// </summary>
    public async Task<string?> RecordVideoAsync(
        IReadOnlyList<(string Url, string Label)> pages,
        string browsersPath,
        string videoOutputDir,
        string artifactPrefix,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
            Directory.CreateDirectory(videoOutputDir);

            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
            {
                Headless = true
            });

            try
            {
                var context = await browser.NewContextAsync(new Microsoft.Playwright.BrowserNewContextOptions
                {
                    ViewportSize = new Microsoft.Playwright.ViewportSize { Width = 1920, Height = 1080 },
                    IgnoreHTTPSErrors = true,
                    RecordVideoDir = videoOutputDir,
                    RecordVideoSize = new Microsoft.Playwright.RecordVideoSize { Width = 1920, Height = 1080 }
                });

                var page = await context.NewPageAsync();
                int pagesSucceeded = 0;
                int pagesFailed = 0;

                foreach (var target in pages.Take(6))
                {
                    try
                    {
                        timeoutCts.Token.ThrowIfCancellationRequested();
                        // Use NetworkIdle first (matches Direct screenshot capture) with generous
                        // timeout, then fall back to DOMContentLoaded. The old DOMContentLoaded-only
                        // + 10s timeout was dramatically weaker than screenshot navigation and caused
                        // black videos whenever apps were slow to start (Blazor WASM, cold SPA boot).
                        try
                        {
                            await page.GotoAsync(target.Url, new Microsoft.Playwright.PageGotoOptions
                            {
                                WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle,
                                Timeout = 20000
                            });
                        }
                        catch (Microsoft.Playwright.PlaywrightException)
                        {
                            // NetworkIdle timed out — retry with DOMContentLoaded
                            await page.GotoAsync(target.Url, new Microsoft.Playwright.PageGotoOptions
                            {
                                WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                                Timeout = 10000
                            });
                        }
                        // Wait for meaningful content to render (avoids white flash between pages).
                        // CRITICAL: catch-all here. Playwright .NET binding throws System.TimeoutException
                        // (not Microsoft.Playwright.TimeoutException) when WaitForSelectorAsync times out;
                        // a narrow `catch (Microsoft.Playwright.PlaywrightException)` would let it escape
                        // and abort the entire video recording on a single page's slow render.
                        // Observed in 2026-05 run: 8/14 candidates lost their video this way.
                        try
                        {
                            await page.WaitForSelectorAsync("body :not(script):not(style):not(link)",
                                new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 3000, State = Microsoft.Playwright.WaitForSelectorState.Visible });
                        }
                        catch { /* proceed anyway */ }

                        // Blazor WASM and similar SPAs finish DOMContentLoaded with only a
                        // "Loading..." splash visible while the runtime boots. Without this
                        // poll, the entire video shows nothing but loading screens. Same
                        // helper used by TakeScreenshotOfUrlAsync.
                        await WaitForLoadingScreenToClearAsync(page, timeoutCts.Token);

                        // Dwell at top for context, then smooth-scroll to reveal below-the-fold content.
                        // Video records the viewport in real-time (unlike screenshots which use FullPage=true),
                        // so scrolling is the only way to capture content taller than 1080px.
                        await Task.Delay(1500, timeoutCts.Token);
                        await SmoothScrollPageAsync(page, timeoutCts.Token);
                        await Task.Delay(1000, timeoutCts.Token); // Pause at bottom briefly
                        pagesSucceeded++;
                    }
                    catch (OperationCanceledException) { break; }
                    // Same broadening for the outer foreach catch — WaitForLoadingScreenToClearAsync
                    // and any subsequent Playwright call can also surface as System.TimeoutException.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        pagesFailed++;
                        _logger.LogDebug(ex, "Skipping page {Url} during video recording — non-fatal", target.Url);
                    }
                }

                if (pagesSucceeded == 0 && pagesFailed > 0)
                    _logger.LogWarning("Video recording: ALL {Failed} page navigations failed — video will be blank/black", pagesFailed);
                else
                    _logger.LogInformation("Video recording: {Succeeded}/{Total} pages loaded successfully", pagesSucceeded, pagesSucceeded + pagesFailed);

                // Finalize video
                await context.CloseAsync();

                string? videoPath = FindLatestVideo(videoOutputDir);
                if (videoPath is not null)
                {
                    _logger.LogInformation("Video file found: {Path} ({Size} bytes)",
                        videoPath, new FileInfo(videoPath).Length);
                    var targetPath = Path.Combine(videoOutputDir, $"{artifactPrefix}.webm");
                    if (videoPath != targetPath)
                    {
                        try { File.Move(videoPath, targetPath, overwrite: true); }
                        catch { targetPath = videoPath; }
                    }
                    return targetPath;
                }
                _logger.LogWarning("No .webm video file found in {Dir} after recording", videoOutputDir);
                // Log dir contents for debugging
                if (Directory.Exists(videoOutputDir))
                {
                    var allFiles = Directory.GetFiles(videoOutputDir);
                    _logger.LogWarning("Video dir contains {Count} files: [{Files}]",
                        allFiles.Length, string.Join(", ", allFiles.Select(Path.GetFileName)));
                }
                return null;
            }
            finally
            {
                await browser.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Async video recording failed for {Prefix}", artifactPrefix);
            return null;
        }
    }

    /// <summary>
    /// Records a video and generates an animated GIF from an existing screenshot result.
    /// Uses the discovered page URLs from screenshots for accurate video navigation.
    /// Returns a new AppInteractionResult with video and GIF paths populated.
    /// </summary>
    public async Task<AppInteractionResult> RecordVideoAndGifAsync(
        AppInteractionResult screenshotResult,
        string browsersPath,
        string videoOutputDir,
        string artifactPrefix,
        CancellationToken ct)
    {
        string? videoPath = null;
        string? gifPath = null;

        try
        {
            // Build page list from actual discovered URLs (not base URL)
            var videoPages = screenshotResult.Screenshots
                .Where(s => !string.IsNullOrEmpty(s.Url))
                .Select(s => (Url: s.Url!, Label: s.Label))
                .ToList();

            if (videoPages.Count == 0)
            {
                _logger.LogWarning("No discovered URLs for video recording — skipping. Screenshots: {Count}, URLs: [{Urls}]",
                    screenshotResult.Screenshots.Count,
                    string.Join(", ", screenshotResult.Screenshots.Select(s => s.Url ?? "NULL")));
                return screenshotResult;
            }

            _logger.LogInformation("Recording video of {Count} discovered pages for {Prefix}",
                videoPages.Count, artifactPrefix);

            videoPath = await RecordVideoAsync(videoPages, browsersPath, videoOutputDir, artifactPrefix, ct);

            if (videoPath is not null)
            {
                _logger.LogInformation("Video recorded: {Path}", videoPath);

                // Generate animated GIF from the video (best-effort)
                try
                {
                    gifPath = await GenerateAnimatedGifAsync(videoPath, videoOutputDir, artifactPrefix, ct);
                    if (gifPath is not null)
                        _logger.LogInformation("Animated GIF generated: {Path}", gifPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Animated GIF generation failed — skipping");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video/GIF recording failed for {Prefix} — screenshots still available", artifactPrefix);
        }

        return new AppInteractionResult(
            screenshotResult.Screenshots, videoPath, gifPath,
            screenshotResult.PageAnalysis, screenshotResult.CaptureMetrics,
            screenshotResult.AppBaseUrl);
    }

    /// <summary>
    /// Generates an animated GIF from a WebM video using FFmpeg (best-effort).
    /// Returns the GIF path on success, null on failure or if FFmpeg is unavailable.
    /// </summary>
    private async Task<string?> GenerateAnimatedGifAsync(
        string webmPath, string outputDir, string artifactPrefix, CancellationToken ct)
    {
        if (!GifConverter.IsAvailable)
        {
            _logger.LogInformation("GIF generation skipped: ffmpeg not available. Install ffmpeg for animated GIF previews");
            return null;
        }

        var gifPath = Path.Combine(outputDir, $"{artifactPrefix}.gif");
        var success = await GifConverter.ConvertAsync(webmPath, gifPath, fps: 4, maxWidth: 1920, ct: ct);
        return success ? gifPath : null;
    }

    private static string? FindLatestVideo(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "*.webm")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Waits for Blazor WASM / SPA loading screens to clear by polling the page's visible
    /// text content. A page is considered "still loading" when the body text is short
    /// (&lt;200 chars) and contains a word starting with "loading" (case-insensitive).
    /// Polls every 2 seconds for up to 12 seconds.
    /// </summary>
    public async Task WaitForLoadingScreenToClearAsync(Microsoft.Playwright.IPage page, CancellationToken ct)
    {
        const int maxAttempts = 6;
        const int pollIntervalMs = 2000;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var bodyText = await page.EvaluateAsync<string>(
                    "() => document.body?.innerText?.trim() ?? ''");

                if (string.IsNullOrWhiteSpace(bodyText))
                {
                    // Empty body — might still be bootstrapping
                    _logger.LogDebug("Loading wait attempt {Attempt}/{Max}: body is empty", attempt + 1, maxAttempts);
                    await Task.Delay(pollIntervalMs, ct);
                    continue;
                }

                // Short text with "loading" pattern = still a loading screen
                if (bodyText.Length < 200 &&
                    bodyText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(w => w.StartsWith("loading", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug(
                        "Loading wait attempt {Attempt}/{Max}: detected loading screen ({Length} chars): {Preview}",
                        attempt + 1, maxAttempts, bodyText.Length,
                        bodyText.Length > 80 ? bodyText[..80] + "..." : bodyText);
                    await Task.Delay(pollIntervalMs, ct);
                    continue;
                }

                // Real content visible — done waiting
                _logger.LogDebug("Loading wait: content loaded after {Attempt} attempts ({Length} chars)",
                    attempt + 1, bodyText.Length);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Loading wait attempt {Attempt}: evaluation failed", attempt + 1);
                return; // Don't loop on page errors
            }
        }

        _logger.LogDebug("Loading wait: timed out after {Max} attempts — taking screenshot anyway", maxAttempts);
    }

    /// <summary>
    /// Smoothly scrolls the page from current position to the bottom in viewport-sized steps.
    /// Video records the viewport in real-time, so scrolling is the only way to capture
    /// below-the-fold content (screenshots use FullPage=true but video cannot).
    /// Scrolls back to top after reaching the bottom so the next navigation starts clean.
    /// </summary>
    private async Task SmoothScrollPageAsync(Microsoft.Playwright.IPage page, CancellationToken ct)
    {
        try
        {
            var dimensions = await page.EvaluateAsync<int[]>(
                "() => [document.documentElement.scrollHeight, window.innerHeight]");

            int scrollHeight = dimensions[0];
            int viewportHeight = dimensions[1];

            // No scrolling needed if content fits in viewport
            if (scrollHeight <= viewportHeight + 50)
                return;

            // Scroll down in steps of ~80% viewport height for overlap between frames
            int stepSize = (int)(viewportHeight * 0.8);
            int currentScroll = 0;

            while (currentScroll < scrollHeight - viewportHeight)
            {
                ct.ThrowIfCancellationRequested();
                currentScroll = Math.Min(currentScroll + stepSize, scrollHeight - viewportHeight);
                await page.EvaluateAsync($"window.scrollTo({{ top: {currentScroll}, behavior: 'smooth' }})");
                await Task.Delay(800, ct); // Let smooth scroll animation complete + dwell
            }

            // Brief pause at bottom, then scroll back to top
            await Task.Delay(500, ct);
            await page.EvaluateAsync("window.scrollTo({ top: 0, behavior: 'instant' })");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Non-fatal — page may not support scrolling (e.g., canvas-based games)
            _logger.LogDebug(ex, "Smooth scroll failed — page may use custom scroll handling");
        }
    }
}
