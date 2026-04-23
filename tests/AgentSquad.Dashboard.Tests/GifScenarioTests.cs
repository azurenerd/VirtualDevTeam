using AgentSquad.Dashboard.Tests.Helpers;
using Microsoft.Playwright;

namespace AgentSquad.Dashboard.Tests;

/// <summary>
/// Comprehensive UI scenario tests that produce animated GIFs.
/// Each test exercises a complete user workflow, records video, and converts to GIF.
/// Results are tracked in SQLite and rendered as an HTML report.
///
/// Run with: dotnet test --filter "FullyQualifiedName~GifScenarioTests"
/// Report at: test-results/gif-scenarios/index.html
/// </summary>
[Collection("GifScenarios")]
public class GifScenarioTests : IClassFixture<DashboardWebAppFixture>, IClassFixture<PlaywrightFixture>, IAsyncLifetime
{
    private readonly DashboardWebAppFixture _app;
    private readonly PlaywrightFixture _pw;
    private readonly string _outputDir;
    private readonly string _screenshotDir;
    private readonly string _videoDir;
    private readonly string _gifDir;
    private ScenarioResultTracker _tracker = null!;

    public GifScenarioTests(DashboardWebAppFixture app, PlaywrightFixture pw)
    {
        _app = app;
        _pw = pw;
        _outputDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results", "gif-scenarios");
        _screenshotDir = Path.Combine(_outputDir, "screenshots");
        _videoDir = Path.Combine(_outputDir, "videos");
        _gifDir = Path.Combine(_outputDir, "gifs");
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_screenshotDir);
        Directory.CreateDirectory(_videoDir);
        Directory.CreateDirectory(_gifDir);
        _tracker = new ScenarioResultTracker(Path.Combine(_outputDir, "results.db"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Generate HTML report from all results collected so far
        var results = _tracker.GetAll();
        if (results.Count > 0)
            GifReportGenerator.Generate(results, _outputDir);
        _tracker.Dispose();
        return Task.CompletedTask;
    }

    private async Task<ScenarioContext> StartScenarioAsync(string id, string name)
    {
        var scenarioVideoDir = Path.Combine(_videoDir, id);
        Directory.CreateDirectory(scenarioVideoDir);

        var context = await _pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            RecordVideoDir = scenarioVideoDir,
            RecordVideoSize = new RecordVideoSize { Width = 1920, Height = 1080 },
        });

        var page = await context.NewPageAsync();
        return new ScenarioContext(page, context, id, name, scenarioVideoDir, _screenshotDir, _gifDir);
    }

    private void RecordResult(ScenarioResult result)
    {
        _tracker.Record(result);
        // Also generate report incrementally so partial results are visible
        var results = _tracker.GetAll();
        GifReportGenerator.Generate(results, _outputDir);
    }

    // ─────────────────────────────────────────────────────────────
    // S01: Full Navigation Tour — visits every page via sidebar
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S01_NavigationTour_AllPages()
    {
        await using var sc = await StartScenarioAsync("S01", "Full Navigation Tour");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync(_app.BaseUrl, new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-overview");

            var navLinks = new[]
            {
                ("features", "02-features"),
                ("configuration", "03-configuration"),
                ("strategies", "04-strategies"),
                ("timeline", "05-timeline"),
                ("repository", "06-repository"),
                ("metrics", "07-metrics"),
                ("health", "08-health"),
                ("approvals", "09-approvals"),
                ("team", "10-team"),
                ("pipelines", "11-pipelines"),
                ("reasoning", "12-reasoning"),
                ("director-cli", "13-director-cli"),
            };

            foreach (var (href, label) in navLinks)
            {
                // Click the nav link
                var navSelector = $"a.nav-link[href='{href}']";
                var link = await sc.Page.QuerySelectorAsync(navSelector);
                if (link is not null)
                {
                    await link.ClickAsync();
                    await sc.WaitForBlazorAsync(800);
                    await sc.CaptureFrameAsync(label);
                }
                else
                {
                    // Try direct navigation as fallback
                    await sc.Page.GotoAsync($"{_app.BaseUrl}/{href}", new() { WaitUntil = WaitUntilState.Load, Timeout = 10000 });
                    await sc.WaitForBlazorAsync(800);
                    await sc.CaptureFrameAsync(label);
                }
            }

            // Verify we successfully navigated (check URL isn't error page)
            var url = sc.Page.Url;
            Assert.DoesNotContain("/Error", url);

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S02: Feature Draft Flow — create a new feature draft
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S02_FeatureDraftFlow_CreateAndVerify()
    {
        await using var sc = await StartScenarioAsync("S02", "Feature Draft Flow");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync($"{_app.BaseUrl}/features", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-features-page");

            // Look for "New Feature" button
            var newFeatureBtn = await sc.Page.QuerySelectorAsync("button:has-text('New Feature'), button:has-text('Add Feature'), .btn-new-feature");
            if (newFeatureBtn is not null)
            {
                await newFeatureBtn.ClickAsync();
                await sc.WaitForBlazorAsync(300);
                await sc.CaptureFrameAsync("02-form-open");

                // Fill form fields if they exist
                var titleInput = await sc.Page.QuerySelectorAsync("input[placeholder*='title' i], input[placeholder*='Title' i], .feature-form input[type='text']:first-of-type");
                if (titleInput is not null)
                    await titleInput.FillAsync("UI Test Feature — Automated");

                var descInput = await sc.Page.QuerySelectorAsync("textarea[placeholder*='description' i], textarea[placeholder*='Description' i], .feature-form textarea:first-of-type");
                if (descInput is not null)
                    await descInput.FillAsync("This feature was created by an automated UI test to verify the feature creation workflow.");

                await sc.CaptureFrameAsync("03-form-filled");

                // Look for Save Draft button
                var saveDraftBtn = await sc.Page.QuerySelectorAsync("button:has-text('Save Draft'), button:has-text('Draft')");
                if (saveDraftBtn is not null)
                {
                    await saveDraftBtn.ClickAsync();
                    await sc.WaitForBlazorAsync(500);
                    await sc.CaptureFrameAsync("04-after-save");
                }
            }
            else
            {
                await sc.CaptureFrameAsync("02-no-new-feature-btn");
            }

            // Verify page didn't error
            var bodyText = await sc.Page.InnerTextAsync("body");
            Assert.False(string.IsNullOrWhiteSpace(bodyText));

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S03: Configuration Expand/Collapse — read-only section toggle
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S03_ConfigExpandCollapse_SectionsToggle()
    {
        await using var sc = await StartScenarioAsync("S03", "Config Expand/Collapse");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync($"{_app.BaseUrl}/configuration", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-config-page");

            // Find expandable section headers
            var headers = await sc.Page.QuerySelectorAllAsync(".config-card-header, .config-section-header, h3.config-title, [class*='config'] h3, [class*='config'] .card-header");
            var clickedCount = 0;

            foreach (var header in headers.Take(6))
            {
                try
                {
                    await header.ClickAsync(new() { Timeout = 3000 });
                    await sc.WaitForBlazorAsync(400);
                    clickedCount++;
                    await sc.CaptureFrameAsync($"02-section-{clickedCount:D2}-expanded");
                }
                catch { /* Some headers may not be clickable */ }
            }

            // Collapse by clicking again
            foreach (var header in headers.Take(3))
            {
                try
                {
                    await header.ClickAsync(new() { Timeout = 3000 });
                    await sc.WaitForBlazorAsync(300);
                }
                catch { }
            }
            await sc.CaptureFrameAsync("03-some-collapsed");

            var bodyText = await sc.Page.InnerTextAsync("body");
            Assert.Contains("Configuration", bodyText, StringComparison.OrdinalIgnoreCase);

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S04: Timeline Interactions — theme switching + view modes
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S04_TimelineInteractions_ThemesAndViews()
    {
        await using var sc = await StartScenarioAsync("S04", "Timeline Interactions");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync($"{_app.BaseUrl}/timeline", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync(1000);
            await sc.CaptureFrameAsync("01-timeline-default");

            // Theme switching
            var themeNames = new[] { "Blueprint", "Metro", "Cards", "Default" };
            foreach (var theme in themeNames)
            {
                var btn = await sc.Page.QuerySelectorAsync($"button:has-text('{theme}'), .ntl-theme-btn:has-text('{theme}')");
                if (btn is not null)
                {
                    await btn.ClickAsync();
                    await sc.WaitForBlazorAsync(500);
                    await sc.CaptureFrameAsync($"02-theme-{theme.ToLower()}");
                }
            }

            // View mode toggle
            var pmViewBtn = await sc.Page.QuerySelectorAsync("button:has-text('PM'), .ntl-view-btn:has-text('PM')");
            if (pmViewBtn is not null)
            {
                await pmViewBtn.ClickAsync();
                await sc.WaitForBlazorAsync(500);
                await sc.CaptureFrameAsync("03-pm-view");
            }

            var engViewBtn = await sc.Page.QuerySelectorAsync("button:has-text('Engineering'), .ntl-view-btn:has-text('Engineering')");
            if (engViewBtn is not null)
            {
                await engViewBtn.ClickAsync();
                await sc.WaitForBlazorAsync(500);
                await sc.CaptureFrameAsync("04-engineering-view");
            }

            // Zoom controls
            var zoomInBtn = await sc.Page.QuerySelectorAsync("button:has-text('Zoom In'), .ntl-zoom-in, button[title='Zoom In']");
            if (zoomInBtn is not null)
            {
                for (int i = 0; i < 3; i++)
                {
                    await zoomInBtn.ClickAsync();
                    await sc.Page.WaitForTimeoutAsync(200);
                }
                await sc.CaptureFrameAsync("05-zoomed-in");
            }

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S05: Repository Filtering — PR/Issue tabs + state filters
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S05_RepositoryFiltering_TabsAndFilters()
    {
        await using var sc = await StartScenarioAsync("S05", "Repository Filtering");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync($"{_app.BaseUrl}/repository", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-repository-page");

            // Try PR tab
            var prTab = await sc.Page.QuerySelectorAsync("button:has-text('Pull Request'), .repo-tab:has-text('Pull'), a:has-text('Pull Request')");
            if (prTab is not null)
            {
                await prTab.ClickAsync();
                await sc.WaitForBlazorAsync(500);
                await sc.CaptureFrameAsync("02-pr-tab");
            }

            // Try filter buttons (Open, Closed, All)
            foreach (var filter in new[] { "Open", "Closed", "All" })
            {
                var filterBtn = await sc.Page.QuerySelectorAsync($"button:has-text('{filter}'):not(.repo-tab), .pr-filter-btn:has-text('{filter}'), .issue-filter-btn:has-text('{filter}')");
                if (filterBtn is not null)
                {
                    await filterBtn.ClickAsync();
                    await sc.WaitForBlazorAsync(500);
                    await sc.CaptureFrameAsync($"03-filter-{filter.ToLower()}");
                }
            }

            // Try Issues tab
            var issuesTab = await sc.Page.QuerySelectorAsync("button:has-text('Issue'), .repo-tab:has-text('Issue'), a:has-text('Issue')");
            if (issuesTab is not null)
            {
                await issuesTab.ClickAsync();
                await sc.WaitForBlazorAsync(500);
                await sc.CaptureFrameAsync("04-issues-tab");
            }

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S06: Agent Overview Drill-Down — overview → detail → back
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S06_AgentOverviewDrillDown_DetailAndBack()
    {
        await using var sc = await StartScenarioAsync("S06", "Agent Overview Drill-Down");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync(_app.BaseUrl, new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-overview");

            // Look for agent cards or links to agent detail
            var agentLink = await sc.Page.QuerySelectorAsync(
                ".agent-card a, a[href*='/agent/'], .agent-card, [class*='agent'] a");

            if (agentLink is not null)
            {
                await agentLink.ClickAsync();
                await sc.WaitForBlazorAsync(800);
                await sc.CaptureFrameAsync("02-agent-detail");

                // Check for agent detail content
                var detailText = await sc.Page.InnerTextAsync("body");
                var hasDetail = detailText.Length > 50; // Page has content
                await sc.CaptureFrameAsync("03-detail-content");

                // Navigate back
                var backBtn = await sc.Page.QuerySelectorAsync("button:has-text('Back'), a:has-text('Back'), .back-btn, a[href='/']");
                if (backBtn is not null)
                {
                    await backBtn.ClickAsync();
                    await sc.WaitForBlazorAsync(500);
                }
                else
                {
                    await sc.Page.GotoAsync(_app.BaseUrl);
                    await sc.WaitForBlazorAsync(500);
                }
                await sc.CaptureFrameAsync("04-back-to-overview");
            }
            else
            {
                // No agent cards — still capture the empty state
                await sc.CaptureFrameAsync("02-no-agents");
            }

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S07: Health Monitor — status display + diagnostic feed
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S07_HealthMonitorDiagnostics_StatusAndFeed()
    {
        await using var sc = await StartScenarioAsync("S07", "Health Monitor Diagnostics");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync($"{_app.BaseUrl}/health", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-health-page");

            // Verify health page has structure
            var bodyText = await sc.Page.InnerTextAsync("body");
            Assert.False(string.IsNullOrWhiteSpace(bodyText));
            await sc.CaptureFrameAsync("02-health-content");

            // Try diagnostic feed filter (agent dropdown)
            var agentFilter = await sc.Page.QuerySelectorAsync("select, .diagnostic-filter select, [class*='filter'] select");
            if (agentFilter is not null)
            {
                await agentFilter.ClickAsync();
                await sc.WaitForBlazorAsync(300);
                await sc.CaptureFrameAsync("03-filter-dropdown");

                // Select first option if available
                var options = await agentFilter.QuerySelectorAllAsync("option");
                if (options.Count > 1)
                {
                    await agentFilter.SelectOptionAsync(new SelectOptionValue { Index = 1 });
                    await sc.WaitForBlazorAsync(500);
                    await sc.CaptureFrameAsync("04-filtered");
                }
            }

            // Scroll down to see full content
            await sc.Page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await sc.WaitForBlazorAsync(300);
            await sc.CaptureFrameAsync("05-scrolled-bottom");

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S08: Strategies Framework View — framework + task cards
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S08_StrategiesFrameworkView_CardsAndState()
    {
        await using var sc = await StartScenarioAsync("S08", "Strategies Framework View");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync($"{_app.BaseUrl}/strategies", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-strategies-page");

            var bodyText = await sc.Page.InnerTextAsync("body");
            Assert.False(string.IsNullOrWhiteSpace(bodyText));

            // Check for framework status content
            var hasFrameworkContent = bodyText.Contains("Framework", StringComparison.OrdinalIgnoreCase)
                                  || bodyText.Contains("Strateg", StringComparison.OrdinalIgnoreCase)
                                  || bodyText.Contains("Baseline", StringComparison.OrdinalIgnoreCase);
            await sc.CaptureFrameAsync("02-framework-content");

            // Try refresh button
            var refreshBtn = await sc.Page.QuerySelectorAsync("button:has-text('Refresh'), .refresh-btn, button[title*='Refresh']");
            if (refreshBtn is not null)
            {
                await refreshBtn.ClickAsync();
                await sc.WaitForBlazorAsync(500);
                await sc.CaptureFrameAsync("03-after-refresh");
            }

            // Scroll to see all content
            await sc.Page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await sc.WaitForBlazorAsync(300);
            await sc.CaptureFrameAsync("04-full-page");

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S09: Approvals Gating — decision gates + gate cards
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S09_ApprovalsGating_GatesAndCards()
    {
        await using var sc = await StartScenarioAsync("S09", "Approvals Gating");
        ScenarioResult result;
        try
        {
            await sc.Page.GotoAsync($"{_app.BaseUrl}/approvals", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-approvals-page");

            var bodyText = await sc.Page.InnerTextAsync("body");
            Assert.False(string.IsNullOrWhiteSpace(bodyText));

            // Try filter buttons
            foreach (var filter in new[] { "Open", "Resolved", "All" })
            {
                var filterBtn = await sc.Page.QuerySelectorAsync($"button:has-text('{filter}'), .filter-btn:has-text('{filter}')");
                if (filterBtn is not null)
                {
                    await filterBtn.ClickAsync();
                    await sc.WaitForBlazorAsync(500);
                    await sc.CaptureFrameAsync($"02-filter-{filter.ToLower()}");
                }
            }

            // Scroll to see approval cards
            await sc.Page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await sc.WaitForBlazorAsync(300);
            await sc.CaptureFrameAsync("03-full-approvals");

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }

    // ─────────────────────────────────────────────────────────────
    // S10: Metrics + Team Viz — combined data overview
    // ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task S10_MetricsAndTeamViz_DataOverview()
    {
        await using var sc = await StartScenarioAsync("S10", "Metrics & Team Visualization");
        ScenarioResult result;
        try
        {
            // Metrics page
            await sc.Page.GotoAsync($"{_app.BaseUrl}/metrics", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("01-metrics-page");

            var metricsText = await sc.Page.InnerTextAsync("body");
            Assert.False(string.IsNullOrWhiteSpace(metricsText));

            // Scroll metrics page
            await sc.Page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await sc.WaitForBlazorAsync(300);
            await sc.CaptureFrameAsync("02-metrics-scrolled");

            // Navigate to Team Viz
            await sc.Page.GotoAsync($"{_app.BaseUrl}/team", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync(1000);
            await sc.CaptureFrameAsync("03-team-viz");

            // Check for SVG visualization
            var svg = await sc.Page.QuerySelectorAsync("svg, .team-radial, .radial-svg");
            if (svg is not null)
                await sc.CaptureFrameAsync("04-team-svg-rendered");

            // Navigate to Reasoning
            await sc.Page.GotoAsync($"{_app.BaseUrl}/reasoning", new() { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
            await sc.WaitForBlazorAsync();
            await sc.CaptureFrameAsync("05-reasoning-page");

            result = await sc.FinalizeAsync(true);
        }
        catch (Exception ex)
        {
            result = await sc.FinalizeAsync(false, ex.Message);
            RecordResult(result);
            throw;
        }
        RecordResult(result);
    }
}
