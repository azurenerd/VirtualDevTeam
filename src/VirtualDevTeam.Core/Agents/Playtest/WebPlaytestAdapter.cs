using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Handles <c>ui_interaction</c> scenarios via Microsoft Playwright.
/// Manages a shared browser context within a single scenario execution.
/// </summary>
/// <remarks>
/// Supported action categories: <c>page.*</c> and <c>assert.*</c>.
/// Uses the <see cref="PlaywrightRunner"/> for browser availability checks and
/// <c>PLAYWRIGHT_BROWSERS_PATH</c> resolution. The adapter creates its own Playwright
/// instance per <see cref="ExecuteAsync"/> group — callers should execute all actions
/// for one scenario in sequence using the same adapter instance.
///
/// <para>
/// <b>Known duplication:</b> This class creates its own <c>Playwright.CreateAsync()</c> +
/// <c>Chromium.LaunchAsync()</c> independently from <see cref="PlaywrightRunner"/>, which
/// maintains a separate Playwright lifecycle for screenshot/interaction capture. The duplication
/// exists because this adapter needs a <b>persistent</b> <see cref="IPage"/> across multiple
/// sequential <see cref="ExecuteAsync"/> calls within one playtest scenario, while
/// <c>PlaywrightRunner</c> creates ephemeral browser sessions per capture pass.
/// </para>
///
/// <para>
/// <b>TODO: Future consolidation</b> — Extract a shared <c>BrowserSessionFactory</c> from
/// <see cref="PlaywrightRunner"/> that handles <c>PLAYWRIGHT_BROWSERS_PATH</c> env setup,
/// <see cref="BrowserTypeLaunchOptions"/> defaults (headless, viewport), and browser lifecycle.
/// Both this adapter and <c>PlaywrightRunner</c> would consume the factory, eliminating the
/// duplicated launch logic while preserving their distinct session lifetimes.
/// </para>
/// </remarks>
public sealed class WebPlaytestAdapter : IPlaytestAdapter, IAsyncDisposable
{
    private readonly PlaywrightRunner _runner;
    private readonly ILogger<WebPlaytestAdapter> _logger;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    private static readonly HashSet<string> _handledCategories =
        new(StringComparer.OrdinalIgnoreCase) { "page", "assert", "wait", "log" };

    public WebPlaytestAdapter(PlaywrightRunner runner, ILogger<WebPlaytestAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool CanHandle(PlaytestAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _handledCategories.Contains(action.ActionCategory);
    }

    /// <inheritdoc/>
    public async Task<IPlaytestEvidence> ExecuteAsync(
        PlaytestAction action,
        AppHandle handle,
        Dictionary<string, string?> snapshots,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(snapshots);

        try
        {
            await EnsureBrowserOpenAsync(handle, ct);
            return await DispatchAsync(action, handle, snapshots, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebPlaytestAdapter: action {ActionType} failed", action.ActionType);
            return new ActionFailureEvidence(action.SurfaceVerified ?? "page_action", ex.Message);
        }
    }

    private async Task EnsureBrowserOpenAsync(AppHandle handle, CancellationToken ct)
    {
        if (_page is not null) return;

        // Use PlaywrightRunner's browser path for PLAYWRIGHT_BROWSERS_PATH consistency.
        // The adapter still creates its own Playwright instance (see class remarks for why),
        // but shares the browser binary location with PlaywrightRunner.
        if (_runner.IsReady)
        {
            // PlaywrightRunner has already validated and set PLAYWRIGHT_BROWSERS_PATH env var.
            // No additional setup needed — Playwright.CreateAsync() will use the env var.
        }

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });

        var contextOptions = new BrowserNewContextOptions();
        var context = await _browser.NewContextAsync(contextOptions);
        _page = await context.NewPageAsync();
        _logger.LogDebug("WebPlaytestAdapter: Chromium browser opened");
    }

    private async Task<IPlaytestEvidence> DispatchAsync(
        PlaytestAction action,
        AppHandle handle,
        Dictionary<string, string?> snapshots,
        CancellationToken ct)
    {
        var page = _page!;

        switch (action.ActionType.ToLowerInvariant())
        {
            // ── Navigation ──────────────────────────────────────────────────────
            case "page.goto":
            {
                var rawUrl = action.GetParam("url") ?? handle.BaseUrl;
                var url = rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? rawUrl
                    : handle.BaseUrl.TrimEnd('/') + "/" + rawUrl.TrimStart('/');

                await page.GotoAsync(url, new PageGotoOptions { Timeout = 15_000 });
                _logger.LogDebug("WebPlaytestAdapter: goto {Url}", url);
                return new ActionSuccessEvidence("page_navigation", $"Navigated to {url}");
            }

            // ── Interaction ─────────────────────────────────────────────────────
            case "page.click":
            {
                var selector = action.GetParam("selector")!;
                await page.ClickAsync(selector, new PageClickOptions { Timeout = 10_000 });
                return new ActionSuccessEvidence("page_click", $"Clicked {selector}");
            }

            case "page.fill":
            {
                var selector = action.GetParam("selector")!;
                var value = action.GetParam("value") ?? string.Empty;
                await page.FillAsync(selector, value);
                return new ActionSuccessEvidence("page_fill", $"Filled {selector}");
            }

            // ── Waiting ─────────────────────────────────────────────────────────
            case "page.waitforselector":
            {
                var selector = action.GetParam("selector")!;
                var timeout = action.GetIntParam("timeout", 5_000);
                await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { Timeout = timeout });
                if (action.SurfaceVerified is not null)
                    return new DomQueryEvidence(true, selector);
                return new ActionSuccessEvidence("page_wait", $"Selector appeared: {selector}");
            }

            case "page.waitforurl":
            {
                var pattern = action.GetParam("urlPattern") ?? action.GetParam("url") ?? string.Empty;
                await page.WaitForURLAsync(pattern, new PageWaitForURLOptions { Timeout = 5_000 });
                return new ActionSuccessEvidence("page_wait_url", $"URL matched: {pattern}");
            }

            case "wait.ms":
            {
                var ms = action.GetIntParam("milliseconds", 500);
                await Task.Delay(ms, ct);
                return new ActionSuccessEvidence("wait", $"Waited {ms}ms");
            }

            // ── JavaScript evaluation ────────────────────────────────────────────
            case "page.evaluate":
            {
                var expression = action.GetParam("expression") ?? "null";
                var result = await page.EvaluateAsync<string?>(expression);

                if (action.CapturesSnapshot && action.SnapshotKey is not null)
                    snapshots[action.SnapshotKey] = result;

                return new ActionSuccessEvidence("dom_eval", result);
            }

            // ── Screenshot ──────────────────────────────────────────────────────
            case "page.screenshot":
            {
                var filename = action.GetParam("filename") ?? $"screenshot_{DateTime.UtcNow:yyyyMMddHHmmss}.png";
                string? outputPath = null;
                byte[]? bytes = null;

                if (!string.IsNullOrEmpty(handle.ScreenshotOutputDir))
                {
                    outputPath = Path.Combine(handle.ScreenshotOutputDir, filename);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = outputPath });
                    bytes = File.Exists(outputPath) ? await File.ReadAllBytesAsync(outputPath, ct) : null;
                }
                else
                {
                    bytes = await page.ScreenshotAsync();
                }

                return new ScreenshotEvidence(filename, bytes, outputPath);
            }

            // ── DOM Assertions ──────────────────────────────────────────────────
            case "assert.selectorexists":
            {
                var selector = action.GetParam("selector")!;
                var element = await page.QuerySelectorAsync(selector);
                return new DomQueryEvidence(element is not null, selector);
            }

            case "assert.selectortext":
            {
                var selector = action.GetParam("selector")!;
                var expected = action.GetParam("expectedText");
                var element = await page.QuerySelectorAsync(selector);
                var text = element is not null ? await element.TextContentAsync() : null;
                var matched = text is not null && (expected is null
                    || text.Contains(expected, StringComparison.OrdinalIgnoreCase));
                return new DomTextEvidence(selector, text, expected, matched);
            }

            case "assert.selectorchanged":
            {
                var selector = action.GetParam("selector")!;
                var snapshotKey = action.GetParam("snapshotKey");
                var element = await page.QuerySelectorAsync(selector);
                var currentValue = element is not null ? await element.TextContentAsync() : null;
                var baseline = snapshotKey is not null && snapshots.TryGetValue(snapshotKey, out var v) ? v : null;
                var changed = currentValue != baseline;

                if (action.CapturesSnapshot && action.SnapshotKey is not null)
                    snapshots[action.SnapshotKey] = currentValue;

                return new DomSnapshotChangedEvidence(selector, snapshotKey ?? string.Empty, baseline, currentValue, changed);
            }

            case "assert.eventfired":
            {
                var eventName = action.GetParam("eventName")!;
                // Check the playtest event log injected via page.addInitScript or window.__playtestEventLog
                var logJson = await page.EvaluateAsync<string?>(
                    "window.__playtestEventLog ? JSON.stringify(window.__playtestEventLog) : null");
                var fired = logJson is not null &&
                            logJson.Contains(eventName, StringComparison.OrdinalIgnoreCase);
                return new EventBusEvidence(eventName, fired);
            }

            // ── Log snapshot ─────────────────────────────────────────────────────
            case "log.snapshot":
            {
                var label = action.GetParam("label") ?? "snapshot";
                var consoleContent = await page.EvaluateAsync<string?>(
                    "window.__playtestLog ? window.__playtestLog.join('\\n') : ''");
                if (action.CapturesSnapshot && action.SnapshotKey is not null)
                    snapshots[action.SnapshotKey] = consoleContent;
                return new ActionSuccessEvidence("log_snapshot", $"[{label}] {consoleContent?.Length ?? 0} chars");
            }

            default:
                return new InconclusiveEvidence(
                    action.SurfaceVerified ?? "page_action",
                    $"WebPlaytestAdapter: unrecognised action type '{action.ActionType}'");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { /* best effort */ }
            _browser = null;
        }
        if (_playwright is not null)
        {
            _playwright.Dispose();
            _playwright = null;
        }
        _page = null;
    }
}
