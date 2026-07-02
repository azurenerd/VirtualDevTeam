using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// Generates a contact sheet (composite grid image) from multiple screenshots.
/// Uses Playwright to render an HTML grid layout and capture it as a single PNG.
/// This avoids adding image processing NuGet dependencies — Playwright is already available.
/// </summary>
public sealed class ContactSheetGenerator
{
    private readonly ILogger<ContactSheetGenerator> _logger;

    public ContactSheetGenerator(ILogger<ContactSheetGenerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates a contact sheet from multiple labeled screenshots.
    /// Returns a single composite PNG image suitable for vision-model judge input.
    /// If only one screenshot is provided, returns it as-is (no grid needed).
    /// </summary>
    /// <param name="screenshots">Ordered list of (label, PNG bytes) pairs.</param>
    /// <param name="columns">Number of columns in the grid (default 2).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Composite PNG bytes, or null if generation failed.</returns>
    public async Task<byte[]?> GenerateAsync(
        IReadOnlyList<(string Label, byte[] PngBytes)> screenshots,
        int columns = 2,
        CancellationToken ct = default)
    {
        if (screenshots.Count == 0)
            return null;

        // Single screenshot — return as-is, no contact sheet needed
        if (screenshots.Count == 1)
            return screenshots[0].PngBytes;

        try
        {
            var html = BuildContactSheetHtml(screenshots, columns);
            return await RenderHtmlToScreenshotAsync(html, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Contact sheet generation failed — falling back to first screenshot");
            return screenshots[0].PngBytes;
        }
    }

    private static string BuildContactSheetHtml(
        IReadOnlyList<(string Label, byte[] PngBytes)> screenshots, int columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><style>");
        sb.AppendLine(@"
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body { background: #1a1a2e; padding: 12px; font-family: -apple-system, BlinkMacSystemFont, sans-serif; }
            .grid { display: grid; grid-template-columns: repeat(" + columns + @", 1fr); gap: 12px; }
            .cell { background: #16213e; border-radius: 8px; overflow: hidden; border: 1px solid #0f3460; }
            .label { padding: 8px 12px; font-size: 13px; font-weight: 600; color: #e0e0e0;
                     background: #0f3460; border-bottom: 1px solid #1a1a2e; }
            .cell img { width: 100%; height: auto; display: block; }
        ");
        sb.AppendLine("</style></head><body><div class=\"grid\">");

        for (int i = 0; i < screenshots.Count; i++)
        {
            var (label, pngBytes) = screenshots[i];
            var base64 = Convert.ToBase64String(pngBytes);
            sb.AppendLine($"<div class=\"cell\">");
            sb.AppendLine($"  <div class=\"label\">{i + 1}. {EscapeHtml(label)}</div>");
            sb.AppendLine($"  <img src=\"data:image/png;base64,{base64}\" />");
            sb.AppendLine($"</div>");
        }

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private async Task<byte[]?> RenderHtmlToScreenshotAsync(string html, CancellationToken ct)
    {
        var browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrEmpty(browsersPath))
        {
            _logger.LogDebug("PLAYWRIGHT_BROWSERS_PATH not set — cannot render contact sheet");
            return null;
        }

        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        try
        {
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 15000
            });

            // Wait briefly for images to render
            await Task.Delay(500, ct);

            var screenshotBytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = true,
                Type = ScreenshotType.Png,
            });

            _logger.LogDebug("Contact sheet generated: {Size} bytes", screenshotBytes.Length);
            return screenshotBytes;
        }
        finally
        {
            await browser.DisposeAsync();
        }
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
