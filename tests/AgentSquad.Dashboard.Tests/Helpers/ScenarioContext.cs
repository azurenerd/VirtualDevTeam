using System.Diagnostics;
using Microsoft.Playwright;

namespace AgentSquad.Dashboard.Tests.Helpers;

/// <summary>
/// Manages a single GIF scenario: creates browser context with video recording,
/// captures milestone screenshots, and converts the WebM to an animated GIF on completion.
/// </summary>
public sealed class ScenarioContext : IAsyncDisposable
{
    public IPage Page { get; }
    public string ScenarioId { get; }
    public string ScenarioName { get; }

    private readonly IBrowserContext _context;
    private readonly string _videoDir;
    private readonly string _screenshotDir;
    private readonly string _gifDir;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<string> _screenshotPaths = new();

    public ScenarioContext(
        IPage page,
        IBrowserContext context,
        string scenarioId,
        string scenarioName,
        string videoDir,
        string screenshotDir,
        string gifDir)
    {
        Page = page;
        _context = context;
        ScenarioId = scenarioId;
        ScenarioName = scenarioName;
        _videoDir = videoDir;
        _screenshotDir = screenshotDir;
        _gifDir = gifDir;
    }

    /// <summary>Capture a milestone screenshot with a descriptive label.</summary>
    public async Task CaptureFrameAsync(string label)
    {
        var path = Path.Combine(_screenshotDir, $"{ScenarioId}_{label}.png");
        await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        _screenshotPaths.Add(path);
    }

    /// <summary>Wait for the page to settle after navigation (Blazor Server SSR).</summary>
    public async Task WaitForBlazorAsync(int extraMs = 500)
    {
        // Wait for network idle + a small buffer for SignalR/Blazor rendering
        try
        {
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 10000 });
        }
        catch (TimeoutException)
        {
            // Blazor Server keeps SignalR open — NetworkIdle may never fire. That's ok.
        }
        if (extraMs > 0)
            await Page.WaitForTimeoutAsync(extraMs);
    }

    /// <summary>
    /// Finalize the scenario: close context, convert WebM→GIF, return result.
    /// </summary>
    public async Task<ScenarioResult> FinalizeAsync(bool passed, string? errorMessage = null)
    {
        _stopwatch.Stop();
        await _context.CloseAsync();

        // Find the recorded WebM
        string? videoPath = null;
        string? gifPath = null;

        var webmFiles = Directory.Exists(_videoDir)
            ? Directory.GetFiles(_videoDir, "*.webm")
            : Array.Empty<string>();

        if (webmFiles.Length > 0)
        {
            videoPath = Path.Combine(
                Path.GetDirectoryName(_videoDir)!,
                $"{ScenarioId}.webm");
            File.Move(webmFiles[0], videoPath, overwrite: true);

            // Convert to GIF
            if (GifConverter.IsAvailable)
            {
                gifPath = Path.Combine(_gifDir, $"{ScenarioId}.gif");
                var converted = await GifConverter.ConvertAsync(videoPath, gifPath);
                if (!converted) gifPath = null;
            }
        }

        // Clean up video subdirectory
        try { if (Directory.Exists(_videoDir)) Directory.Delete(_videoDir, true); } catch { }

        return new ScenarioResult
        {
            Id = ScenarioId,
            Name = ScenarioName,
            Passed = passed,
            ErrorMessage = errorMessage,
            GifPath = gifPath,
            VideoPath = videoPath,
            ScreenshotPaths = _screenshotPaths.Count > 0 ? string.Join("|", _screenshotPaths) : null,
            DurationMs = _stopwatch.ElapsedMilliseconds,
        };
    }

    public async ValueTask DisposeAsync()
    {
        // Ensure context is closed even if FinalizeAsync wasn't called
        try { await _context.CloseAsync(); } catch { }
    }
}
