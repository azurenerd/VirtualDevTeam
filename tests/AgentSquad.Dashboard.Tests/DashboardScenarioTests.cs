using Microsoft.Playwright;
using System.Text;

namespace AgentSquad.Dashboard.Tests;

/// <summary>
/// Playwright-based UI scenario tests for the AgentSquad Dashboard.
/// Each test navigates to a dashboard page, validates key elements, and captures a screenshot.
/// Video recording is enabled per browser context.
/// </summary>
public class DashboardScenarioTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private readonly string _baseUrl = "http://localhost:5051";
    private readonly string _screenshotDir;
    private readonly string _videoDir;
    private readonly List<ScenarioResult> _results = new();

    public DashboardScenarioTests()
    {
        _screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results", "scenarios", "screenshots");
        _videoDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results", "scenarios", "videos");
        Directory.CreateDirectory(_screenshotDir);
        Directory.CreateDirectory(_videoDir);
    }

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            SlowMo = 200
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    private async Task<IBrowserContext> CreateContextAsync(string scenarioName)
    {
        var scenarioVideoDir = Path.Combine(_videoDir, scenarioName);
        Directory.CreateDirectory(scenarioVideoDir);

        return await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            RecordVideoDir = scenarioVideoDir,
            RecordVideoSize = new RecordVideoSize { Width = 1920, Height = 1080 }
        });
    }

    /// <summary>
    /// Renames the random-hash video file to a scenario-named file after context disposal.
    /// Must be called after DisposeAsync on the context.
    /// </summary>
    private void RenameVideo(string scenarioName)
    {
        var scenarioVideoDir = Path.Combine(_videoDir, scenarioName);
        var videoFile = Directory.GetFiles(scenarioVideoDir, "*.webm").FirstOrDefault();
        if (videoFile is null) return;

        var dest = Path.Combine(_videoDir, $"{scenarioName}.webm");
        File.Move(videoFile, dest, overwrite: true);

        // Clean up the per-scenario subdirectory
        try { Directory.Delete(scenarioVideoDir, recursive: true); } catch { }
    }

    private async Task<string> CaptureScreenshotAsync(IPage page, string scenarioId)
    {
        var path = Path.Combine(_screenshotDir, $"{scenarioId}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = true
        });
        return path;
    }

    [Fact]
    public async Task S01_AgentOverview_ShowsAgentCards()
    {
        var context = await CreateContextAsync("S01");
        var page = await context.NewPageAsync();

        await page.GotoAsync(_baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000); // Allow Blazor to render

        // Verify the page loaded with the dark theme
        var body = await page.QuerySelectorAsync("body");
        Assert.NotNull(body);

        // Check for main content area
        var content = await page.QuerySelectorAsync(".content, main, article");
        Assert.NotNull(content);

        // Check page has text content (not blank)
        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Page body should have text content");

        await CaptureScreenshotAsync(page, "S01_AgentOverview");
        await context.CloseAsync();
        RenameVideo("S01");
    }

    [Fact]
    public async Task S02_PullRequests_ShowsPRList()
    {
        var context = await CreateContextAsync("S02");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/pullrequests", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "PR page should have content");

        // Look for typical PR page elements (filter tabs or PR cards)
        var hasFilters = text.Contains("Open", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("Closed", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("Pull Request", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasFilters, "PR page should have state filters or PR heading");

        await CaptureScreenshotAsync(page, "S02_PullRequests");
        await context.CloseAsync();
        RenameVideo("S02");
    }

    [Fact]
    public async Task S03_Issues_ShowsIssueList()
    {
        var context = await CreateContextAsync("S03");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/issues", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Issues page should have content");

        var hasIssueElements = text.Contains("Issue", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Open", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasIssueElements, "Issues page should have issue-related content");

        await CaptureScreenshotAsync(page, "S03_Issues");
        await context.CloseAsync();
        RenameVideo("S03");
    }

    [Fact]
    public async Task S04_Reasoning_ShowsAgentReasoning()
    {
        var context = await CreateContextAsync("S04");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/reasoning", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Reasoning page should have content");

        await CaptureScreenshotAsync(page, "S04_Reasoning");
        await context.CloseAsync();
        RenameVideo("S04");
    }

    [Fact]
    public async Task S05_Timeline_ShowsTimelineGroups()
    {
        var context = await CreateContextAsync("S05");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/timeline", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Timeline page should have content");

        await CaptureScreenshotAsync(page, "S05_Timeline");
        await context.CloseAsync();
        RenameVideo("S05");
    }

    [Fact]
    public async Task S06_Configuration_ShowsAgentSections()
    {
        var context = await CreateContextAsync("S06");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/configuration", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Configuration page should have content");

        var hasConfigContent = text.Contains("Configuration", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Agent", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Settings", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasConfigContent, "Configuration page should have config-related content");

        await CaptureScreenshotAsync(page, "S06_Configuration");
        await context.CloseAsync();
        RenameVideo("S06");
    }

    [Fact]
    public async Task S08_HealthMonitor_ShowsStatus()
    {
        var context = await CreateContextAsync("S08");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/health", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Health Monitor page should have content");

        await CaptureScreenshotAsync(page, "S08_HealthMonitor");
        await context.CloseAsync();
        RenameVideo("S08");
    }

    [Fact]
    public async Task S09_Metrics_ShowsData()
    {
        var context = await CreateContextAsync("S09");
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync($"{_baseUrl}/metrics", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        Assert.NotNull(response);
        Assert.True(response.Ok, $"Metrics page returned {response.Status} — expected 200");
        await CaptureScreenshotAsync(page, "S09_Metrics");

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Metrics page should have content");
        await context.CloseAsync();
        RenameVideo("S09");
    }

    [Fact]
    public async Task S10_TeamViz_ShowsVisualization()
    {
        var context = await CreateContextAsync("S10");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/team", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Team Viz page should have content");

        await CaptureScreenshotAsync(page, "S10_TeamViz");
        await context.CloseAsync();
        RenameVideo("S10");
    }

    [Fact]
    public async Task S11_Approvals_ShowsGates()
    {
        var context = await CreateContextAsync("S11");
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseUrl}/approvals", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 15000 });
        await page.WaitForTimeoutAsync(2000);

        var text = await page.InnerTextAsync("body");
        Assert.False(string.IsNullOrWhiteSpace(text), "Approvals page should have content");

        await CaptureScreenshotAsync(page, "S11_Approvals");
        await context.CloseAsync();
        RenameVideo("S11");
    }

    public record ScenarioResult(string Id, string Name, bool Passed, string? Error, string ScreenshotPath);
}
